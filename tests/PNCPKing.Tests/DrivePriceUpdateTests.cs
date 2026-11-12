using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PNCPKing.App.Services;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class DrivePriceUpdateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pncpking-drive-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Archive = [80, 75, 3, 4, 10, 20, 30, 40];
    private static readonly DrivePriceFile File = new("10ieeKiBIxRu7hehGb0DVt3QsoiObw9y8", "precos.pncpking", Archive.Length, 1794398450761);
    private static string Fixture(string name) => System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static string Listing(params DrivePriceFile[] files)
    {
        var rows = files.Select(f => new object?[] { f.Id, new[] { DrivePriceUpdateService.FolderId }, f.Name,
            "application/x-zip", 0, null, 0, 0, 0, f.ModifiedTime, f.ModifiedTime, null, null, f.Size }).ToArray();
        var json = JsonSerializer.Serialize(new object[] { rows });
        var literal = JsonSerializer.Serialize(json)[1..^1].Replace("'", "\\x27", StringComparison.Ordinal);
        return "<script>window['_DRIVE_ivd'] = '" + literal + "';</script>";
    }
    private static HttpResponseMessage Html(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "text/html") };
    private static HttpResponseMessage Binary(byte[]? value = null) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value ?? Archive) };

    [Fact]
    public void RealAnonymousListingSupportsArchiveAboveFourGiB()
    {
        var file = Assert.Single(DrivePriceUpdateService.ParseListing(Fixture("drive-folder.html")));
        Assert.Equal("PNCPKing-sem-cotacoes-20261111.pncpking", file.Name);
        Assert.Equal(4484945638L, file.Size);
        Assert.Equal(File.Id, file.Id);
        Assert.Equal(1794398450761L, file.ModifiedTime);
    }

    [Fact]
    public void ListingFiltersNonPackagesAndKeepsMultipleCandidatesWithoutGuessing()
    {
        var older = File with { Id = "oldfile123456", ModifiedTime = File.ModifiedTime - 1 };
        var files = DrivePriceUpdateService.ParseListing(Listing(older, File, File with { Id = "document12345", Name = "manual.pdf" }));
        Assert.Equal(new[] { File, older }, files);
        Assert.Empty(DrivePriceUpdateService.ParseListing(Listing()));
    }

    [Theory]
    [InlineData("<html>Sign in</html>")]
    [InlineData("<html>Too many downloads</html>")]
    [InlineData("<script>window['_DRIVE_ivd']='not-json';</script>")]
    [InlineData("<script>window['_DRIVE_ivd']='[[],null,null,1,[]]';</script>")]
    [InlineData("<script>window['_DRIVE_ivd']='[[],null,1,null,[]]';</script>")]
    [InlineData("<script>window['_DRIVE_ivd']='[[],\\x22next-page\\x22]';</script>")]
    public void UnexpectedOrIncompleteListingFailsClosed(string html) =>
        Assert.Throws<InvalidDataException>(() => DrivePriceUpdateService.ParseListing(html));

    [Fact]
    public void ListingRejectsDuplicateIdentities() =>
        Assert.Throws<InvalidDataException>(() => DrivePriceUpdateService.ParseListing(Listing(File, File)));

    [Fact]
    public void ConfirmationUsesOnlyTheExpectedFileAndFixedGoogleTarget()
    {
        var uri = DrivePriceUpdateService.ParseConfirmation(Fixture("drive-confirm.html"), File.Id);
        Assert.Equal("drive.usercontent.google.com", uri.Host);
        Assert.Contains("id=" + File.Id, uri.Query);
        Assert.Contains("uuid=FIXTURE-UUID", uri.Query);
        Assert.Throws<IOException>(() => DrivePriceUpdateService.ParseConfirmation(Fixture("drive-confirm.html"), "otherFile12345"));
        Assert.Throws<IOException>(() => DrivePriceUpdateService.ParseConfirmation(
            Fixture("drive-confirm.html").Replace("https://drive.usercontent.google.com/download", "https://example.com/steal"), File.Id));
    }

    [Fact]
    public async Task DownloadsViaLargeFileConfirmationAndReusesCompleteCacheWithoutRereadingIt()
    {
        var binaryRequests = 0;
        using var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("folders", StringComparison.Ordinal)) return Html(Listing(File));
            if (request.RequestUri.AbsolutePath == "/uc") return Html(Fixture("drive-confirm.html"));
            binaryRequests++;
            return Binary();
        });
        using var client = new HttpClient(handler);
        var service = new DrivePriceUpdateService(client);
        var path = await service.DownloadAsync(File, _directory);
        Assert.Equal(Archive, await System.IO.File.ReadAllBytesAsync(path));
        Assert.Equal(path, await service.DownloadAsync(File, _directory));
        Assert.Equal(1, binaryRequests);
        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
    }

    [Fact]
    public async Task NewMetadataIsCheckedBeforeReusingCachedDownload()
    {
        var current = File;
        using var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath.Contains("folders", StringComparison.Ordinal)
            ? Html(Listing(current)) : Binary());
        using var client = new HttpClient(handler);
        var service = new DrivePriceUpdateService(client);
        var path = await service.DownloadAsync(File, _directory);
        current = File with { ModifiedTime = File.ModifiedTime + 1 };
        await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(File, _directory));
        Assert.True(System.IO.File.Exists(path));
    }

    [Fact]
    public async Task CompletedDownloadPrunesOnlyObsoleteGeneratedCacheFiles()
    {
        Directory.CreateDirectory(_directory);
        var old = Path.Combine(_directory, $"{File.Id}-{File.ModifiedTime - 1}-{File.Size}.pncpking");
        var unrelated = Path.Combine(_directory, "meu-backup.pncpking");
        await System.IO.File.WriteAllBytesAsync(old, Archive);
        await System.IO.File.WriteAllBytesAsync(unrelated, Archive);
        using var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath.Contains("folders", StringComparison.Ordinal)
            ? Html(Listing(File)) : Binary());
        using var client = new HttpClient(handler);
        var path = await new DrivePriceUpdateService(client).DownloadAsync(File, _directory);
        Assert.True(System.IO.File.Exists(path));
        Assert.True(System.IO.File.Exists(unrelated));
        Assert.False(System.IO.File.Exists(old));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("html")]
    [InlineData("disguised-html")]
    [InlineData("quota")]
    [InlineData("partial-response")]
    public async Task InvalidDownloadsNeverBecomeImportableArchives(string failure)
    {
        using var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("folders", StringComparison.Ordinal)) return Html(Listing(File));
            return failure switch
            {
                "short" => Binary(Archive[..5]),
                "long" => Binary([.. Archive, 50]),
                "html" => Html("<html>Please sign in</html>"),
                "disguised-html" => Binary("<html />"u8.ToArray()),
                "quota" => new(HttpStatusCode.TooManyRequests),
                _ => new(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Archive) }
            };
        });
        using var client = new HttpClient(handler);
        var error = await Record.ExceptionAsync(() => new DrivePriceUpdateService(client).DownloadAsync(File, _directory));
        Assert.NotNull(error);
        Assert.True(error is IOException or InvalidDataException, error.ToString());
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task FailureDuringStreamingAndCancellationRemovePartialFilesAndAllowRetry()
    {
        var attempts = 0;
        using var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("folders", StringComparison.Ordinal)) return Html(Listing(File));
            return ++attempts == 1 ? new(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream()) } : Binary();
        });
        using var client = new HttpClient(handler);
        var service = new DrivePriceUpdateService(client);
        await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(File, _directory));
        Assert.Empty(Directory.GetFiles(_directory));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(File, _directory, ct: cts.Token));
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.True(System.IO.File.Exists(await service.DownloadAsync(File, _directory)));
    }

    [Theory]
    [InlineData("https://accounts.google.com/login")]
    [InlineData("https://example.com/archive")]
    [InlineData("http://drive.google.com/uc")]
    [InlineData("https://drive.google.com:444/uc")]
    public async Task UnsafeRedirectIsRejectedBeforeSendingRequest(string target)
    {
        var count = 0;
        using var handler = new FakeHandler(_ =>
        {
            count++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(target);
            return response;
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<IOException>(() => new DrivePriceUpdateService(client).ListAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ProgramOnlyCheckDoesNotConsultTheOldIncrementalPriceChannel()
    {
        var requests = new List<string>();
        using var handler = new FakeHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler);
        var check = await new GitHubUpdateService(client).CheckProgramAsync();
        Assert.Null(check.Prices);
        Assert.Single(requests);
        Assert.EndsWith("/latest", requests[0]);
    }

    [Fact]
    public void PendingRestartRetainsDriveIdentityAndProgramOnlyRemainsOptOut()
    {
        var pending = new PendingGitHubUpdate("database.db", "1.2.47", null, UpdatePricesAfterRestart: true, DrivePrices: File);
        var restored = JsonSerializer.Deserialize<PendingGitHubUpdate>(JsonSerializer.Serialize(pending, GitHubUpdateValidation.Json), GitHubUpdateValidation.Json)!;
        Assert.Equal(File, restored.DrivePrices);
        Assert.Null(restored.PriceUpdate);
        Assert.Null(restored.DeferredPrices);
        Assert.True(restored.UpdatePricesAfterRestart);
        var programOnly = pending with { DrivePrices = null, UpdatePricesAfterRestart = false };
        Assert.False(programOnly.UpdatePricesAfterRestart);
    }

    [Fact]
    public async Task DownloadedFullArchiveAcceptsLegacyFtsFormattingAndPreservesLocalQuotation()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var contract = PriceCacheTests.RecentContract("drive-download-contract", DateOnly.FromDateTime(DateTime.Today), 1);
        await source.Repository.UpsertContractsAsync([contract]);
        var localQuotations = new SqliteQuotationRepository(destination.Repository.DatabasePath);
        var local = await localQuotations.CreateProjectAsync("Cotação local preservada");
        await HistoricalTransferSchemaTests.ReformatFtsTriggerAsync(destination.Repository.DatabasePath, "contracts_fts_update");
        var sourcePath = Path.Combine(source.Directory, "source.pncpking");
        await new BackupService(source.Repository).ExportAsync(sourcePath, BackupProfile.FullWithoutQuotations);
        var file = File with { Size = new FileInfo(sourcePath).Length };
        using var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath.Contains("folders", StringComparison.Ordinal)
            ? Html(Listing(file)) : new(HttpStatusCode.OK) { Content = new StreamContent(System.IO.File.OpenRead(sourcePath)) });
        using var client = new HttpClient(handler);
        var path = await new DrivePriceUpdateService(client).DownloadAsync(file, _directory);
        var importer = new BackupService(destination.Repository);
        var inspection = await importer.InspectAsync(path);
        Assert.Equal(BackupProfile.FullWithoutQuotations, inspection.Profile);
        Assert.True(inspection.HasEnoughSpace);
        var recovery = await importer.ImportAsync(path);
        Assert.True(System.IO.File.Exists(recovery));
        Assert.NotNull(await destination.Repository.GetContractAsync(contract.PncpId));
        Assert.Equal(local.Id, Assert.Single(await localQuotations.GetProjectsAsync()).Id);
    }

    [Fact]
    public async Task LargeArchiveMetadataPassesTheOldTwoGiBLimitAndDetectsTruncatedStream()
    {
        var large = File with { Size = 4484945638L };
        var downloads = 0;
        using var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("folders", StringComparison.Ordinal)) return Html(Listing(large));
            downloads++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(Archive)) };
            response.Content.Headers.ContentLength = large.Size;
            return response;
        });
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new DrivePriceUpdateService(client).DownloadAsync(large, _directory));
        Assert.Contains("Download incompleto", error.Message);
        Assert.Equal(1, downloads);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(handle(request));
        }
    }

    private sealed class FailingStream : MemoryStream
    {
        public FailingStream() : base(Archive) { }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Position >= 4 ? ValueTask.FromException<int>(new IOException("Connection lost")) : base.ReadAsync(buffer[..Math.Min(4, buffer.Length)], ct);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
