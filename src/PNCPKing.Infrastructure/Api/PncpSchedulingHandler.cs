using System.Net;
using System.Diagnostics;
using PNCPKing.Core.Interfaces;

namespace PNCPKing.Infrastructure.Api;

/// <summary>
/// Request metadata used by the shared PNCP scheduler. Callers may either tag a
/// specific <see cref="HttpRequestMessage"/> or open an async-flow scope around
/// an existing IPncpClient call without changing that interface.
/// </summary>
public static class PncpRequestOptions
{
    public static readonly HttpRequestOptionsKey<PncpRequestPriority> PriorityKey =
        new("PNCPKing.RequestPriority");
    public static readonly HttpRequestOptionsKey<PncpRequestCategory> CategoryKey =
        new("PNCPKing.RequestCategory");

    private static readonly AsyncLocal<ScopeState?> CurrentScope = new();

    public static IDisposable BeginScope(
        PncpRequestPriority priority,
        PncpRequestCategory? category = null)
    {
        var previous = CurrentScope.Value;
        var current = new ScopeState(priority, category);
        CurrentScope.Value = current;
        return new ScopeLease(previous, current);
    }

    public static void Set(
        HttpRequestMessage request,
        PncpRequestPriority priority,
        PncpRequestCategory? category = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(PriorityKey, priority);
        if (category is { } value)
        {
            request.Options.Set(CategoryKey, value);
        }
    }

    internal static (PncpRequestPriority Priority, PncpRequestCategory Category) Resolve(
        HttpRequestMessage request)
    {
        var inferredCategory = InferCategory(request.RequestUri);
        var scope = CurrentScope.Value;
        var category = request.Options.TryGetValue(CategoryKey, out var taggedCategory)
            ? taggedCategory
            : scope?.Category ?? inferredCategory;
        var priority = request.Options.TryGetValue(PriorityKey, out var taggedPriority)
            ? taggedPriority
            : scope?.Priority ?? DefaultPriority(category);
        return (priority, category);
    }

    internal static PncpRequestPriority ResolveCurrentPriority(Uri uri)
    {
        var category = InferCategory(uri);
        return CurrentScope.Value?.Priority ?? DefaultPriority(category);
    }

    public static PncpRequestCategory InferCategory(Uri? uri)
    {
        var path = uri?.AbsolutePath ?? string.Empty;
        if (path.Contains("/itens/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/resultados", StringComparison.OrdinalIgnoreCase))
        {
            return PncpRequestCategory.ItemResults;
        }

        if (path.Contains("/itens", StringComparison.OrdinalIgnoreCase))
        {
            return PncpRequestCategory.ItemLists;
        }

        if (path.Contains("/contratacoes/", StringComparison.OrdinalIgnoreCase))
        {
            return PncpRequestCategory.Contracts;
        }

        return PncpRequestCategory.Other;
    }

    private static PncpRequestPriority DefaultPriority(PncpRequestCategory category) => category switch
    {
        PncpRequestCategory.ItemResults => PncpRequestPriority.VisiblePrices,
        PncpRequestCategory.ItemLists => PncpRequestPriority.VisiblePrices,
        _ => PncpRequestPriority.IndexMaintenance
    };

    private sealed record ScopeState(
        PncpRequestPriority Priority,
        PncpRequestCategory? Category);

    private sealed class ScopeLease(ScopeState? previous, ScopeState current) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && ReferenceEquals(CurrentScope.Value, current))
            {
                CurrentScope.Value = previous;
            }
        }
    }
}

/// <summary>
/// Delegating handler that applies the shared request limit and records actual
/// response bytes. The concurrency lease is kept until the response body is
/// consumed or disposed, not merely until its headers arrive.
/// </summary>
public sealed class PncpSchedulingHandler : DelegatingHandler
{
    private readonly PncpRequestScheduler _scheduler;
    private readonly PncpRequestTelemetry _telemetry;
    private readonly IPerformanceTelemetry _performance;

    public PncpSchedulingHandler(
        PncpRequestScheduler scheduler,
        PncpRequestTelemetry telemetry,
        IPerformanceTelemetry? performance = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _performance = performance ?? NullPerformanceTelemetry.Instance;
    }

    public PncpRequestScheduler Scheduler => _scheduler;
    public IPncpRequestTelemetry Telemetry => _telemetry;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var metadata = PncpRequestOptions.Resolve(request);
        var measurement = _telemetry.Begin(metadata.Category);
        var queuedAt = Stopwatch.GetTimestamp();
        IDisposable? lease = null;
        RequestCompletion? completion = null;
        PerformanceSpan? networkSpan = null;

        try
        {
            lease = await _scheduler.AcquireAsync(metadata.Priority, cancellationToken).ConfigureAwait(false);
            _performance.Record(
                "pncp-request",
                $"queue-{metadata.Category}",
                Stopwatch.GetElapsedTime(queuedAt));
            measurement.MarkDispatched();
            networkSpan = _performance.Begin("pncp-request", $"network-{metadata.Category}");
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            completion = new RequestCompletion(
                lease,
                measurement,
                networkSpan,
                response.IsSuccessStatusCode);
            lease = null;
            networkSpan = null;

            if (response.Content is null)
            {
                completion.Complete();
            }
            else
            {
                response.Content = new MeasuredHttpContent(response.Content, completion);
            }

            return response;
        }
        catch (Exception exception)
        {
            completion?.Complete(contentSucceeded: false);
            measurement.Complete(succeeded: false);
            networkSpan?.Fail(exception);
            throw;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private sealed class RequestCompletion(
        IDisposable lease,
        PncpRequestTelemetry.Measurement measurement,
        PerformanceSpan performanceSpan,
        bool responseSucceeded)
    {
        private IDisposable? _lease = lease;
        private int _completed;
        private long _bytes;

        public void AddBytes(long count)
        {
            if (Volatile.Read(ref _completed) == 0)
            {
                measurement.AddBytes(count);
                Interlocked.Add(ref _bytes, count);
            }
        }

        public void Complete(bool contentSucceeded = true)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            measurement.Complete(responseSucceeded && contentSucceeded);
            if (responseSucceeded && contentSucceeded)
            {
                performanceSpan.Complete(bytes: Interlocked.Read(ref _bytes));
            }
            else
            {
                performanceSpan.Fail(new HttpRequestException("PNCP request failed."), bytes: Interlocked.Read(ref _bytes));
            }
            Interlocked.Exchange(ref _lease, null)?.Dispose();
        }
    }

    private sealed class MeasuredHttpContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly RequestCompletion _completion;

        public MeasuredHttpContent(HttpContent inner, RequestCompletion completion)
        {
            _inner = inner;
            _completion = completion;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var countingStream = new CountingWriteStream(stream, _completion);
            try
            {
                await _inner.CopyToAsync(countingStream).ConfigureAwait(false);
                _completion.Complete();
            }
            catch
            {
                _completion.Complete(contentSucceeded: false);
                throw;
            }
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            var countingStream = new CountingWriteStream(stream, _completion);
            try
            {
                await _inner.CopyToAsync(countingStream, cancellationToken).ConfigureAwait(false);
                _completion.Complete();
            }
            catch
            {
                _completion.Complete(contentSucceeded: false);
                throw;
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync()
        {
            try
            {
                return new CountingReadStream(
                    await _inner.ReadAsStreamAsync().ConfigureAwait(false),
                    _completion);
            }
            catch
            {
                _completion.Complete(contentSucceeded: false);
                throw;
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            try
            {
                return new CountingReadStream(
                    await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    _completion);
            }
            catch
            {
                _completion.Complete(contentSucceeded: false);
                throw;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            if (_inner.Headers.ContentLength is { } contentLength)
            {
                length = contentLength;
                return true;
            }

            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _completion.Complete();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CountingReadStream(Stream inner, RequestCompletion completion) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Record(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Record(read);
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            try
            {
                var read = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                Record(read);
                return read;
            }
            catch
            {
                completion.Complete(contentSucceeded: false);
                throw;
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                Record(read);
                return read;
            }
            catch
            {
                completion.Complete(contentSucceeded: false);
                throw;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                completion.Complete();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                completion.Complete();
                GC.SuppressFinalize(this);
            }
        }

        private void Record(int read)
        {
            if (read == 0)
            {
                completion.Complete();
            }
            else
            {
                completion.AddBytes(read);
            }
        }
    }

    private sealed class CountingWriteStream(Stream inner, RequestCompletion completion) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            completion.AddBytes(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            completion.AddBytes(buffer.Length);
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            completion.AddBytes(count);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            completion.AddBytes(buffer.Length);
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The destination stream belongs to HttpContent, not to this wrapper.
            base.Dispose(disposing);
        }
    }
}
