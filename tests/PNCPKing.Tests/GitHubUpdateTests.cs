using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class GitHubUpdateTests
{
    private const int Schema = SqliteContractRepository.CurrentSchemaVersion;
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("conteúdo verificado");
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static ReleaseFile File(string name, byte[]? bytes = null) => new(name, (bytes ?? Bytes).Length, Hash(bytes ?? Bytes));
    private static OfficialTransferStatus Empty() => new(new Dictionary<string, OfficialImportReceipt>());

    private static OfficialUpdateManifest Manifest()
    {
        var end = DateOnly.FromDateTime(DateTime.Today);
        var start = end.AddDays(-9);
        var chunks = Enumerable.Range(0, 10).Select(index => new OfficialUpdateChunk(
            "day:" + start.AddDays(index).ToString("yyyy-MM-dd"), "publication-day",
            "days/" + start.AddDays(index).ToString("yyyy-MM-dd") + ".db", start.AddDays(index),
            4096, Hash(Bytes), Hash(Encoding.UTF8.GetBytes("logical-" + index)), 0, 0, 0, 0, 0, 1)).ToArray();
        return new(2, Schema, start, end, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N"), chunks);
    }

    private static PriceUpdatePackage Package()
    {
        var manifest = Manifest();
        var file = File("prices.pncpupdate");
        return new(manifest, manifest.Chunks.Sum(chunk => chunk.ExpandedSize),
            new(file.Size, file.Sha256, [file]));
    }

    private static PricesUpdateManifest Prices() => new(2, DateTimeOffset.UtcNow, "1.2.0", Schema, Package());
    private static AppUpdateManifest App(string version = "1.3.0", int schema = Schema) =>
        new(1, version, "win-x64", schema, File("PNCPKing.exe"));
    private static GitHubUpdateCheck Check(AppUpdateManifest? app, PricesUpdateManifest? prices) => new(
        app is null ? null : new("v" + app.Version, app), prices is null ? null : new("precos", prices),
        "Programa não publicado.", "Preços não publicados.");

    [Theory]
    [InlineData("1.2.0", false)]
    [InlineData("1.1.9", false)]
    [InlineData("1.3.0", true)]
    [InlineData("1.10.0", true)]
    public void ProgramVersionIsNumericAndNeverDowngrades(string version, bool update)
    {
        var plan = GitHubUpdateValidation.Plan(Check(App(version), null), new(1, 2, 0), Schema, Empty());
        Assert.Equal(update, plan.App is not null);
        Assert.Null(plan.Package);
    }

    [Theory]
    [InlineData("v1.2.0")]
    [InlineData("1.2")]
    [InlineData("1.2.0-beta")]
    [InlineData("01.2.0")]
    public void InvalidVersionsAreRejected(string version) =>
        Assert.Throws<InvalidDataException>(() => GitHubUpdateValidation.ParseVersion(version));

    [Fact]
    public void OneMobilePackageIsIndependentOfDatabaseOriginAndCompletedReceiptSkipsIt()
    {
        var prices = Prices();
        var plan = GitHubUpdateValidation.Plan(Check(null, prices), new(1, 2, 0), Schema, Empty());
        Assert.Equal(prices.Update, plan.Package);
        Assert.Equal(prices.Update.Download.Size, plan.DownloadSize);
        Assert.Contains("backups", plan.PricesStatus);

        var state = new OfficialTransferStatus(new Dictionary<string, OfficialImportReceipt>
        {
            [prices.Update.Manifest.PackageId] = new(new string('a', 64), true)
        });
        Assert.Null(GitHubUpdateValidation.Plan(Check(null, prices), new(1, 2, 0), Schema, state).Package);
    }

    [Fact]
    public void PricesRequireMatchingSchemaMinimumVersionAndOneAsset()
    {
        var prices = Prices();
        GitHubUpdateValidation.Validate(prices);
        Assert.Null(GitHubUpdateValidation.Plan(Check(null, prices with { MinimumAppVersion = "1.3.0" }),
            new(1, 2, 0), Schema, Empty()).Package);
        Assert.Throws<InvalidDataException>(() => GitHubUpdateValidation.Validate(prices with { Format = 1 }));
        Assert.Throws<InvalidDataException>(() => GitHubUpdateValidation.Validate(prices with
        {
            Update = prices.Update with
            {
                Download = prices.Update.Download with { Parts = [File("one"), File("two")] }
            }
        }));
    }

    [Theory]
    [InlineData("../PNCPKing.exe", 10)]
    [InlineData("C:\\PNCPKing.exe", 10)]
    [InlineData("PNCPKing.exe", 0)]
    [InlineData("PNCPKing.exe", 2147483648L)]
    public void InvalidAssetPathsAndSizesAreRejected(string name, long size) =>
        Assert.Throws<InvalidDataException>(() => GitHubUpdateValidation.ValidateFile(new(name, size, Hash(Bytes))));

    [Fact]
    public async Task GitHubDownloadCalculatesExternalHashWhileWritingAndReusesVerifiedFile()
    {
        using var directory = new TemporaryDirectory();
        var file = File("prices.pncpupdate");
        var payload = new UpdatePayload(file.Size, file.Sha256, [file]);
        var calls = 0;
        using var client = new HttpClient(new FakeHandler(_ =>
        {
            calls++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
        }));
        var service = new GitHubUpdateService(client);
        var path = await service.DownloadAsync("precos", payload, directory.Path);
        Assert.Equal(Bytes, await System.IO.File.ReadAllBytesAsync(path));
        Assert.True(System.IO.File.Exists(path + ".verified"));
        Assert.Equal(path, await service.DownloadAsync("precos", payload, directory.Path));
        Assert.Equal(1, calls);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruncatedOrCorruptedDownloadCannotBecomeCompleted(bool truncated)
    {
        using var directory = new TemporaryDirectory();
        var bad = truncated ? Bytes[..^1] : Enumerable.Repeat((byte)'x', Bytes.Length).ToArray();
        using var client = new HttpClient(new FakeHandler(_ =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(bad) }));
        var file = File("prices.pncpupdate");
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubUpdateService(client)
            .DownloadAsync("precos", new(file.Size, file.Sha256, [file]), directory.Path));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task MissingReleaseAndInvalidIndependentChannelAreReportedSeparately()
    {
        var prices = Prices();
        using var handler = new FakeHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)
                ? new(HttpStatusCode.NotFound)
                : PricesResponse(request, prices));
        using var client = new HttpClient(handler);
        var check = await new GitHubUpdateService(client).CheckAsync();
        Assert.Null(check.App);
        Assert.NotNull(check.Prices);
        Assert.Contains("nenhuma publicação", check.AppStatus);
    }

    [Fact]
    public async Task DownloadedMetadataMatchesActualV2ExportAndReceiptSurvivesIndependentDatabase()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        await source.Repository.EnsureCoverageWindowAsync(today.AddDays(-9), today, [6]);
        await source.Repository.SetCoverageStatusAsync(today.AddDays(-9), today, 6, "ALL", CoverageStatus.Complete, 0);
        var path = Path.Combine(source.Directory, "prices.pncpupdate");
        var exported = await new OfficialUpdateService(source.Repository.DatabasePath).ExportAsync(path);
        var metadata = await OfficialUpdateService.ReadManifestAsync(path);
        var bytes = await System.IO.File.ReadAllBytesAsync(path);
        var file = File("prices.pncpupdate", bytes);
        var package = new PriceUpdatePackage(exported, metadata.ExpandedSize, new(file.Size, file.Sha256, [file]));
        await GitHubUpdateService.ValidatePackageAsync(path, package);
        await Assert.ThrowsAsync<InvalidDataException>(() => GitHubUpdateService.ValidatePackageAsync(path,
            package with { Manifest = exported with { PackageId = Guid.NewGuid().ToString("N") } }));

        var official = new OfficialUpdateService(destination.Repository.DatabasePath);
        await official.ImportAsync(path);
        var state = await official.GetTransferStatusAsync();
        Assert.True(state.Imports[exported.PackageId].Completed);
        Assert.Matches("^[a-f0-9]{64}$", state.Imports[exported.PackageId].Checksum);
    }

    private static object Asset(ReleaseFile file) => new
    {
        name = file.Name,
        size = file.Size,
        state = "uploaded",
        digest = "sha256:" + file.Sha256
    };

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, GitHubUpdateValidation.Json))
    };

    private static HttpResponseMessage PricesResponse(HttpRequestMessage request, PricesUpdateManifest prices) =>
        request.RequestUri!.AbsolutePath.EndsWith("/precos", StringComparison.Ordinal)
            ? Json(new
            {
                tag_name = "precos",
                draft = false,
                prerelease = false,
                assets = new[] { Asset(File("prices-update.json")), Asset(prices.Update.Download.Parts[0]) }
            })
            : Json(prices);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "PNCPKing.UpdateTests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
