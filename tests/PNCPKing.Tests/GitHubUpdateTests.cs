using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class GitHubUpdateTests
{
    private const int Schema = SqliteContractRepository.CurrentSchemaVersion;
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("conteudo publicado");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static ReleaseFile File(string name = "PNCPKing.exe", byte[]? bytes = null) => new(name, (bytes ?? Bytes).Length, Hash(bytes ?? Bytes));
    private static AppUpdateManifest App(string version = "1.2.0", int schema = Schema) => new(1, version, "win-x64", schema, File());
    private static OfficialTransferStatus Empty() => new(Guid.NewGuid().ToString("N"), null, false,
        new Dictionary<string, OfficialImportReceipt>());
    private static PriceUpdatePackage Package(bool initial, string baseId, string origin, long revision = 0) =>
        new(new(1, Schema, baseId, origin, revision, initial, Guid.NewGuid().ToString("N"), Hash(Bytes), 0),
            4096, new(Bytes.Length, Hash(Bytes), [File(Guid.NewGuid().ToString("N") + ".pncpupdate")]));
    private static PricesUpdateManifest Prices(bool cumulative = true)
    {
        var baseId = Guid.NewGuid().ToString("N");
        var origin = Guid.NewGuid().ToString("N");
        return new(1, DateTimeOffset.UtcNow, "1.1.0", Schema, Package(true, baseId, origin),
            cumulative ? Package(false, baseId, origin, 1) : null);
    }
    private static GitHubUpdateCheck Check(AppUpdateManifest? app, PricesUpdateManifest? prices) => new(
        app is null ? null : new("v" + app.Version, app), prices is null ? null : new("precos", prices),
        "Programa não publicado.", "Preços não publicados.");

    [Theory]
    [InlineData("1.1.0", false)]
    [InlineData("1.0.9", false)]
    [InlineData("1.2.0", true)]
    [InlineData("1.10.0", true)]
    public void ProgramVersionIsNumericAndNeverDowngrades(string version, bool update)
    {
        var plan = GitHubUpdateValidation.Plan(Check(App(version), null), new(1, 1, 0), Schema, Empty());
        Assert.Equal(update, plan.App is not null);
        Assert.Empty(plan.Packages);
    }

    [Theory]
    [InlineData("v1.1.0")]
    [InlineData("1.1")]
    [InlineData("1.1.0-beta")]
    [InlineData("01.1.0")]
    public void InvalidVersionsAreRejected(string version) => Assert.Throws<InvalidDataException>(() => GitHubUpdateValidation.ParseVersion(version));

    [Fact]
    public void FirstUseIncludesBaseThenLatestCumulativeAndIndependentPrices()
    {
        var prices = Prices();
        var plan = GitHubUpdateValidation.Plan(Check(App("1.1.0"), prices), new(1, 1, 0), Schema, Empty());
        Assert.Null(plan.App);
        Assert.Equal(new[] { prices.Base, prices.Cumulative! }, plan.Packages);
        Assert.False(plan.ReplaceBase);
        Assert.Equal(prices.Base.Download.Size + prices.Cumulative!.Download.Size, plan.DownloadSize);
        Assert.NotNull(GitHubUpdateValidation.Plan(Check(App(), prices), new(1, 1, 0), Schema, Empty()).App);
    }

    [Fact]
    public void ReplacementRequiresExplicitAdoptionAndReceiptsBelongToSelectedDatabase()
    {
        var prices = Prices();
        var foreign = Empty() with { BaseId = Guid.NewGuid().ToString("N"), BaseReady = true };
        Assert.True(GitHubUpdateValidation.Plan(Check(null, prices), new(1, 1, 0), Schema, foreign).ReplaceBase);
        var state = Empty() with { BaseId = prices.Base.Manifest.BaseId, BaseReady = true };
        Assert.Single(GitHubUpdateValidation.Plan(Check(null, prices), new(1, 1, 0), Schema, state).Packages);
        state = state with { Imports = new Dictionary<string, OfficialImportReceipt>
            { [prices.Cumulative!.Manifest.PackageId] = new(prices.Cumulative.Manifest.Sha256, false, true) } };
        Assert.False(GitHubUpdateValidation.Plan(Check(null, prices), new(1, 1, 0), Schema, state).HasUpdates);
        Assert.Equal(2, GitHubUpdateValidation.Plan(Check(null, prices), new(1, 1, 0), Schema, Empty()).Packages.Count);
    }

    [Fact]
    public void InterruptedInitialBaseIsResumedBeforeCumulative()
    {
        var prices = Prices();
        var state = Empty() with { BaseId = prices.Base.Manifest.BaseId, BaseReady = false };
        Assert.Equal(2, GitHubUpdateValidation.Plan(Check(null, prices), new(1, 1, 0), Schema, state).Packages.Count);
    }

    [Fact]
    public void PricesRequireMatchingSchemaAndMinimumAppVersion()
    {
        var prices = Prices() with { MinimumAppVersion = "1.2.0" };
        var blocked = GitHubUpdateValidation.Plan(Check(null, prices), new(1, 1, 0), Schema, Empty());
        Assert.Empty(blocked.Packages);
        Assert.Contains("compatível", blocked.PricesStatus);
        Assert.Equal(2, GitHubUpdateValidation.Plan(Check(App(), prices), new(1, 1, 0), Schema, Empty()).Packages.Count);
        Assert.Empty(GitHubUpdateValidation.Plan(Check(App(schema: Schema + 1), prices), new(1, 1, 0), Schema, Empty()).Packages);
    }

    [Fact]
    public void PriceManifestRejectsMixedLineageAndDuplicateParts()
    {
        var prices = Prices();
        GitHubUpdateValidation.Validate(prices);
        Assert.Throws<InvalidDataException>(() => GitHubUpdateValidation.Validate(prices with
            { Cumulative = prices.Cumulative! with { Manifest = prices.Cumulative.Manifest with { Origin = Guid.NewGuid().ToString("N") } } }));
        Assert.Throws<InvalidDataException>(() => GitHubUpdateValidation.Validate(prices with
            { Cumulative = prices.Cumulative! with { Download = prices.Base.Download } }));
    }

    [Theory]
    [InlineData("../PNCPKing.exe", 10)]
    [InlineData("C:\\PNCPKing.exe", 10)]
    [InlineData("PNCPKing.exe", 0)]
    [InlineData("PNCPKing.exe", 2147483648L)]
    public void InvalidAssetPathsAndSizesAreRejected(string name, long size) =>
        Assert.Throws<InvalidDataException>(() => GitHubUpdateValidation.ValidateFile(new(name, size, Hash(Bytes))));

    [Fact]
    public async Task MissingReleaseIsReportedWithoutBlockingOtherChannel()
    {
        using var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)
            ? new(HttpStatusCode.NotFound) : PricesResponse(request, Prices(false)));
        using var client = new HttpClient(handler);
        // Keep one descriptor stable between the API response and the manifest download.
        var prices = Prices(false);
        handler.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)
            ? new(HttpStatusCode.NotFound) : PricesResponse(request, prices);
        var check = await new GitHubUpdateService(client).CheckAsync();
        Assert.Null(check.App);
        Assert.NotNull(check.Prices);
        Assert.Contains("nenhuma publicação", check.AppStatus);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task ApiErrorsAreNotReportedAsAlreadyUpdated(HttpStatusCode status)
    {
        using var client = new HttpClient(new FakeHandler(_ => new(status)));
        var check = await new GitHubUpdateService(client).CheckAsync();
        Assert.Null(check.App);
        Assert.Null(check.Prices);
        Assert.Contains("indisponíveis", check.AppStatus);
    }

    [Fact]
    public async Task StableAppAssetsAreMatchedAndPrereleasesIgnored()
    {
        var app = App();
        var prerelease = false;
        using var client = new HttpClient(new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/precos", StringComparison.Ordinal)) return new(HttpStatusCode.NotFound);
            if (request.RequestUri.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal))
                return Json(new { tag_name = "v1.2.0", draft = false, prerelease, assets = new[]
                    { Asset(File("app-update.json")), Asset(app.File) } });
            return Json(app);
        }));
        Assert.NotNull((await new GitHubUpdateService(client).CheckAsync()).App);
        prerelease = true;
        Assert.Null((await new GitHubUpdateService(client).CheckAsync()).App);
    }

    [Fact]
    public async Task InvalidMetadataDoesNotBlockIndependentChannel()
    {
        var prices = Prices(false);
        using var client = new HttpClient(new FakeHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)
            ? new(HttpStatusCode.OK) { Content = new StringContent("{invalid") } : PricesResponse(request, prices)));
        var check = await new GitHubUpdateService(client).CheckAsync();
        Assert.Null(check.App);
        Assert.NotNull(check.Prices);
    }

    [Fact]
    public async Task MultipartDownloadReconstructsAndReusesCompletedPayload()
    {
        using var directory = new TemporaryDirectory();
        var first = Encoding.UTF8.GetBytes("parte um");
        var second = Encoding.UTF8.GetBytes("parte dois");
        var all = first.Concat(second).ToArray();
        var payload = new UpdatePayload(all.Length, Hash(all), [File("one.part", first), File("two.part", second)]);
        var calls = 0;
        using var client = new HttpClient(new FakeHandler(request =>
        {
            calls++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith("one.part", StringComparison.Ordinal) ? first : second) };
        }));
        var service = new GitHubUpdateService(client);
        var path = await service.DownloadAsync("precos", payload, directory.Path);
        Assert.Equal(all, await System.IO.File.ReadAllBytesAsync(path));
        Assert.Equal(path, await service.DownloadAsync("precos", payload, directory.Path));
        Assert.Equal(2, calls);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial"));
    }

    [Fact]
    public async Task InterruptedDownloadKeepsVerifiedPartsForRetry()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var first = File("one.part");
        var secondBytes = Encoding.UTF8.GetBytes("segunda parte");
        var second = File("two.part", secondBytes);
        var all = Bytes.Concat(secondBytes).ToArray();
        var payload = new UpdatePayload(all.Length, Hash(all), [first, second]);
        var firstRequests = 0;
        using var client = new HttpClient(new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("one.part", StringComparison.Ordinal)) firstRequests++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith("one.part", StringComparison.Ordinal) ? Bytes : secondBytes) };
        }));
        var service = new GitHubUpdateService(client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync("precos", payload, directory.Path,
            new InlineProgress(p => { if (p.Received == first.Size) cancellation.Cancel(); }), cancellation.Token));
        Assert.Single(Directory.GetFiles(directory.Path, "*.part"));
        await service.DownloadAsync("precos", payload, directory.Path);
        Assert.Equal(1, firstRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruncatedOrCorruptedDownloadCannotBecomeACompletedPayload(bool truncated)
    {
        using var directory = new TemporaryDirectory();
        var bad = truncated ? Bytes[..^1] : Enumerable.Repeat((byte)'x', Bytes.Length).ToArray();
        using var client = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bad) }));
        var payload = new UpdatePayload(Bytes.Length, Hash(Bytes), [File("file.part")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubUpdateService(client).DownloadAsync("precos", payload, directory.Path));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void InsufficientSpaceFailsBeforeDownloading()
    {
        using var directory = new TemporaryDirectory();
        Assert.Throws<IOException>(() => GitHubUpdateService.EnsureSpace(directory.Path, long.MaxValue));
    }

    [Fact]
    public async Task DownloadedMetadataIsCheckedAgainstTheActualExportAndReceiptsAreReadFromDatabase()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var path = Path.Combine(source.Directory, "initial.pncpupdate");
        var exported = await new OfficialUpdateService(source.Repository.DatabasePath).ExportAsync(path);
        var metadata = await OfficialUpdateService.ReadManifestAsync(path);
        var bytes = await System.IO.File.ReadAllBytesAsync(path);
        var package = new PriceUpdatePackage(exported, metadata.ExpandedSize, new(bytes.Length, Hash(bytes), [File("initial.pncpupdate", bytes)]));
        await GitHubUpdateService.ValidatePackageAsync(path, package);
        await Assert.ThrowsAsync<InvalidDataException>(() => GitHubUpdateService.ValidatePackageAsync(path,
            package with { Manifest = exported with { Units = exported.Units + 1 } }));
        var official = new OfficialUpdateService(destination.Repository.DatabasePath);
        var before = await official.GetTransferStatusAsync();
        Assert.Null(before.BaseId);
        await official.ImportAsync(path);
        var after = await official.GetTransferStatusAsync();
        Assert.Equal(before.Origin, after.Origin);
        Assert.Equal(exported.BaseId, after.BaseId);
        Assert.True(after.BaseReady);
        Assert.True(after.Imports[exported.PackageId].Completed);
        Assert.Equal(exported.Sha256, after.Imports[exported.PackageId].Checksum);
    }

    private static object Asset(ReleaseFile file) => new { name = file.Name, size = file.Size, state = "uploaded", digest = "sha256:" + file.Sha256 };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value, GitHubUpdateValidation.Json)) };
    private static HttpResponseMessage PricesResponse(HttpRequestMessage request, PricesUpdateManifest prices) =>
        request.RequestUri!.AbsolutePath.EndsWith("/precos", StringComparison.Ordinal)
            ? Json(new { tag_name = "precos", draft = false, prerelease = false,
                assets = new[] { Asset(File("prices-update.json")), Asset(prices.Base.Download.Parts[0]) } }) : Json(prices);
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Respond(request)); }
    }
    private sealed class InlineProgress(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    { public void Report(UpdateDownloadProgress value) => report(value); }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PNCPKing.UpdateTests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
