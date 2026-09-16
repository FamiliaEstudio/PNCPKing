using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Services;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class OfficialUpdateTests
{
    private static ContractRecord Contract(string id, int day = 0) =>
        PriceCacheTests.RecentContract(id, DateOnly.FromDateTime(DateTime.Today), 1) with
        { GlobalUpdatedAt = DateTimeOffset.UtcNow.Date.AddDays(day) };

    [Fact]
    public async Task InitialThenCumulativePreservesDestinationAndTransfersEmptySnapshots()
    {
        await using var a = await TestDatabase.CreateAsync();
        await using var b = await TestDatabase.CreateAsync();
        var ca = Contract("a");
        await a.Repository.UpsertContractsAsync([ca]);
        await a.Repository.UpsertItemsAsync(ca.PncpId, [PriceCacheTests.Item(ca, 1)], false);
        await a.Repository.ReplaceItemResultsAsync(ca.PncpId, 1, []);
        await b.Repository.UpsertContractsAsync([Contract("local")]);
        await SqlAsync(b, "CREATE TABLE user_test(value TEXT); INSERT INTO user_test VALUES('cotação local');");
        var sa = new OfficialUpdateService(a.Repository.DatabasePath);
        var sb = new OfficialUpdateService(b.Repository.DatabasePath);
        var initial = Path.Combine(a.Directory, "initial.pncpupdate");
        var manifest = await sa.ExportAsync(initial);
        Assert.True(manifest.InitialBase);
        Assert.Equal(3, manifest.Units);
        var imported = await sb.ImportAsync(initial);
        Assert.Equal(3, imported.Applied);
        Assert.Equal(0, imported.Conflicts);
        Assert.NotNull(await b.Repository.GetContractAsync("local"));
        Assert.NotNull(await b.Repository.GetItemSnapshotAsync(ca.PncpId));
        Assert.Equal(ItemHydrationStatus.Complete, (await b.Repository.GetItemAsync(ca.PncpId, 1))!.HydrationStatus);
        Assert.Empty((await b.Repository.GetCachedItemResultsAsync(ca.PncpId, 1))!.Results);
        Assert.Equal("cotação local", await ScalarAsync(b, "SELECT value FROM user_test"));
        var noChanges = Path.Combine(a.Directory, "empty.pncpupdate");
        Assert.Equal(0, (await sa.ExportAsync(noChanges)).Units);
        var fromB = Path.Combine(b.Directory, "local.pncpupdate");
        Assert.Equal(1, (await sb.ExportAsync(fromB)).Units); // only destination differences, not the initial base
        Assert.Equal(1, (await sa.ImportAsync(fromB)).Applied);
        await a.Repository.UpsertContractsAsync([ca with { Object = "café novo", GlobalUpdatedAt = ca.GlobalUpdatedAt!.Value.AddHours(1) }]);
        await a.Repository.UpsertItemsAsync(ca.PncpId, [], false);
        var delta = Path.Combine(a.Directory, "delta.pncpupdate");
        Assert.False((await sa.ExportAsync(delta)).InitialBase);
        var result = await sb.ImportAsync(delta);
        Assert.Equal(0, result.Conflicts);
        Assert.Equal("café novo", (await b.Repository.GetContractAsync(ca.PncpId))!.Object);
        Assert.Null(await b.Repository.GetItemAsync(ca.PncpId, 1));
        Assert.Equal(result, await sb.ImportAsync(delta));
    }

    [Fact]
    public async Task TwoComputersPreserveNewerVersionsAndRecordUnorderedConflicts()
    {
        await using var a = await TestDatabase.CreateAsync();
        await using var b = await TestDatabase.CreateAsync();
        var contract = Contract("same");
        await a.Repository.UpsertContractsAsync([contract]);
        var sa = new OfficialUpdateService(a.Repository.DatabasePath);
        var sb = new OfficialUpdateService(b.Repository.DatabasePath);
        var path = Path.Combine(a.Directory, "base.pncpupdate");
        await sa.ExportAsync(path); await sb.ImportAsync(path);
        await a.Repository.UpsertContractsAsync([contract with { Object = "origem" }]);
        path = Path.Combine(a.Directory, "conflict.pncpupdate");
        await sa.ExportAsync(path);
        Assert.Equal(1, (await sb.ImportAsync(path)).Conflicts);
        Assert.Equal(contract.Object, (await b.Repository.GetContractAsync("same"))!.Object);
        await b.Repository.UpsertContractsAsync([contract with { Object = "mais novo", GlobalUpdatedAt = contract.GlobalUpdatedAt!.Value.AddHours(2) }]);
        path = Path.Combine(b.Directory, "newer.pncpupdate");
        await sb.ExportAsync(path);
        Assert.Equal(1, (await sa.ImportAsync(path)).Applied);
        Assert.Equal("mais novo", (await a.Repository.GetContractAsync("same"))!.Object);
        Assert.Equal(0L, await ScalarAsync(a, "SELECT COUNT(*) FROM official_conflicts"));
    }

    [Fact]
    public async Task CancellationResumesCommittedBatchesAndLaterCumulativeContainsEarlierChanges()
    {
        await using var a = await TestDatabase.CreateAsync();
        await using var b = await TestDatabase.CreateAsync();
        var sa = new OfficialUpdateService(a.Repository.DatabasePath);
        var sb = new OfficialUpdateService(b.Repository.DatabasePath);
        var path = Path.Combine(a.Directory, "base.pncpupdate");
        await sa.ExportAsync(path); await sb.ImportAsync(path);
        await a.Repository.UpsertContractsAsync(Enumerable.Range(0, 100).Select(i => Contract($"c{i:000}")).ToArray());
        path = Path.Combine(a.Directory, "first.pncpupdate");
        await sa.ExportAsync(path);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sb.ImportAsync(path,
            new InlineProgress(s => { if (s.StartsWith("Importação:")) cancel.Cancel(); }), cancel.Token));
        var count = (await b.Repository.GetCountsAsync()).Contracts;
        Assert.InRange(count, 1, 32);
        await a.Repository.UpsertContractsAsync([Contract("last")]);
        path = Path.Combine(a.Directory, "latest.pncpupdate");
        Assert.Equal(101, (await sa.ExportAsync(path)).Units);
        await sb.ImportAsync(path);
        Assert.Equal(101, (await b.Repository.GetCountsAsync()).Contracts);
        Assert.Equal(0L, await ScalarAsync(b, "SELECT COUNT(*) FROM official_conflicts"));
    }

    [Fact]
    public async Task InvalidBaseAndChecksumCannotMutateDestination()
    {
        await using var a = await TestDatabase.CreateAsync();
        await using var b = await TestDatabase.CreateAsync();
        var sa = new OfficialUpdateService(a.Repository.DatabasePath);
        var sb = new OfficialUpdateService(b.Repository.DatabasePath);
        var path = Path.Combine(a.Directory, "base.pncpupdate");
        await sa.ExportAsync(path);
        var delta = Path.Combine(a.Directory, "delta.pncpupdate");
        await a.Repository.UpsertContractsAsync([Contract("a")]);
        await sa.ExportAsync(delta);
        await Assert.ThrowsAsync<InvalidDataException>(() => sb.ImportAsync(delta));
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!;
            OfficialUpdateManifest manifest;
            using (var stream = entry.Open()) manifest = JsonSerializer.Deserialize<OfficialUpdateManifest>(stream)!;
            entry.Delete();
            using var output = zip.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(output, manifest with { Sha256 = "bad" });
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => sb.ImportAsync(path));
        Assert.Equal(0, (await b.Repository.GetCountsAsync()).Contracts);
        Assert.Null(await ScalarAsync(b, "SELECT base_id FROM official_transfer_state"));
    }

    [Fact]
    public async Task InvalidEmptyResultUnitIsRejectedBeforeAnyBatchIsApplied()
    {
        await using var a = await TestDatabase.CreateAsync();
        await using var b = await TestDatabase.CreateAsync();
        var contracts = Enumerable.Range(0, 40).Select(i => Contract($"c{i:000}")).ToArray();
        await a.Repository.UpsertContractsAsync(contracts);
        await a.Repository.UpsertItemsAsync(contracts[0].PncpId, [PriceCacheTests.Item(contracts[0], 1)], false);
        await a.Repository.ReplaceItemResultsAsync(contracts[0].PncpId, 1, []);
        var path = Path.Combine(a.Directory, "invalid-unit.pncpupdate");
        var manifest = await new OfficialUpdateService(a.Repository.DatabasePath).ExportAsync(path);
        var payload = Path.Combine(a.Directory, "payload.db");
        using (var zip = ZipFile.OpenRead(path)) zip.GetEntry("updates.db")!.ExtractToFile(payload);
        await using (var connection = new SqliteConnection($"Data Source={payload};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM units WHERE kind=3";
            var unit = JsonNode.Parse((string)(await command.ExecuteScalarAsync())!)!;
            unit["Key2"] = "invalid";
            var json = unit.ToJsonString();
            command.CommandText = "UPDATE units SET key2='invalid',payload=$json,hash=$hash WHERE kind=3";
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
            await command.ExecuteNonQueryAsync();
        }
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("updates.db")!.Delete();
            zip.CreateEntryFromFile(payload, "updates.db");
            zip.GetEntry("manifest.json")!.Delete();
            using var output = zip.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(output, manifest with { Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(payload))) });
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => new OfficialUpdateService(b.Repository.DatabasePath).ImportAsync(path));
        Assert.Equal(0, (await b.Repository.GetCountsAsync()).Contracts);
        Assert.Null(await ScalarAsync(b, "SELECT base_id FROM official_transfer_state"));
    }

    [Fact]
    public async Task CacheRemovalAndRolledBackWritesDoNotExportOfficialDeletions()
    {
        await using var a = await TestDatabase.CreateAsync();
        var contract = Contract("a");
        await a.Repository.UpsertContractsAsync([contract]);
        await a.Repository.UpsertItemsAsync("a", [PriceCacheTests.Item(contract, 1)], false);
        await a.Repository.ReplaceItemResultsAsync("a", 1, [PriceCacheTests.Result(contract, 1, 1, true)]);
        var service = new OfficialUpdateService(a.Repository.DatabasePath);
        await service.ExportAsync(Path.Combine(a.Directory, "base.pncpupdate"));
        await SqlAsync(a, "BEGIN; UPDATE contracts SET object='rolled back'; ROLLBACK; DELETE FROM items; DELETE FROM contract_item_snapshots;");
        Assert.Equal(0, (await service.ExportAsync(Path.Combine(a.Directory, "delta.pncpupdate"))).Units);
        Assert.Equal(0L, await ScalarAsync(a, "SELECT COUNT(*) FROM official_changes"));
    }

    [Fact]
    public async Task CompleteCoverageAndPrivateQuotationsSurviveTransfer()
    {
        await using var a = await TestDatabase.CreateAsync();
        await using var b = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        await a.Repository.EnsureCoverageWindowAsync(today, today, [6]);
        await a.Repository.SetCoverageStatusAsync(today, today, 6, "ALL", CoverageStatus.Complete, 0);
        var quotes = new SqliteQuotationRepository(b.Repository.DatabasePath);
        var project = await quotes.CreateProjectAsync("Particular");
        var line = Guid.NewGuid();
        var reference = new QuotationReference
        {
            Id = "local",
            LineId = line,
            ContractId = "private",
            ItemNumber = 1,
            ResultSequence = 1,
            ItemDescription = "Café",
            ItemUnit = "KG",
            SupplierName = "Fornecedor",
            SupplierTaxId = "11222333000181",
            UnitPrice = 10,
            PublicationDate = DateTimeOffset.Now,
            Source = QuotationReferenceSource.PncpIncisoII,
            Adequacy = new(50, 20, 10, 15, 5, "Teste")
        };
        await quotes.SaveSampleAsync(project.Id, line, new QuotationLineInput("Café", 1, "KG", null, null), [reference]);
        var basket = await quotes.SaveManualBasketAsync(line, null, "Minha cesta", ["local"]);
        var path = Path.Combine(a.Directory, "coverage.pncpupdate");
        Assert.Equal(1, (await new OfficialUpdateService(a.Repository.DatabasePath).ExportAsync(path)).Units);
        await new OfficialUpdateService(b.Repository.DatabasePath).ImportAsync(path);
        Assert.True(await b.Repository.IsCoverageCompleteAsync(today, today));
        Assert.Equal("Particular", Assert.Single(await quotes.GetProjectsAsync()).Name);
        Assert.Equal("local", Assert.Single(await quotes.GetReferencesAsync(line)).Id);
        Assert.Equal(basket.Id, Assert.Single(await quotes.GetManualBasketsAsync(line)).Id);
    }

    [Fact]
    public async Task CatalogChangesAndExplicitNewBaseAreSupported()
    {
        await using var a = await TestDatabase.CreateAsync();
        await using var b = await TestDatabase.CreateAsync();
        await SqlAsync(a, "INSERT INTO catalog_entries(catalog_kind,code,description,search_text,remote_updated_at) VALUES(1,'123','café','cafe','2026-09-01T00:00:00Z');");
        var sa = new OfficialUpdateService(a.Repository.DatabasePath);
        var sb = new OfficialUpdateService(b.Repository.DatabasePath);
        var path = Path.Combine(a.Directory, "base.pncpupdate");
        await sa.ExportAsync(path); await sb.ImportAsync(path);
        Assert.Equal("café", await ScalarAsync(b, "SELECT description FROM catalog_entries WHERE code='123'"));
        await SqlAsync(a, "UPDATE catalog_entries SET description='café novo',search_text='cafe novo',remote_updated_at='2026-09-02T00:00:00Z' WHERE code='123'");
        var delta = Path.Combine(a.Directory, "delta.pncpupdate");
        await sa.ExportAsync(delta); Assert.Equal(1, (await sb.ImportAsync(delta)).Applied);
        await sa.ExportAsync(path, newBase: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => sb.ImportAsync(path));
        await b.Repository.UpsertContractsAsync([Contract("preserved")]);
        await sb.ImportAsync(path, replaceBase: true);
        Assert.NotNull(await b.Repository.GetContractAsync("preserved"));
        Assert.Equal("café novo", await ScalarAsync(b, "SELECT description FROM catalog_entries WHERE code='123'"));
    }

    [Fact]
    public async Task InitialBaseCancellationRequiresResumeBeforeCumulativeImport()
    {
        await using var a = await TestDatabase.CreateAsync();
        await using var b = await TestDatabase.CreateAsync();
        await a.Repository.UpsertContractsAsync(Enumerable.Range(0, 70).Select(i => Contract($"initial-{i:000}")).ToArray());
        var sa = new OfficialUpdateService(a.Repository.DatabasePath); var sb = new OfficialUpdateService(b.Repository.DatabasePath);
        var path = Path.Combine(a.Directory, "base.pncpupdate"); await sa.ExportAsync(path);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sb.ImportAsync(path, new InlineProgress(s => { if (s.StartsWith("Importação:")) cancel.Cancel(); }), cancel.Token));
        var delta = Path.Combine(a.Directory, "delta.pncpupdate"); await sa.ExportAsync(delta);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sb.ImportAsync(delta));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sb.ExportAsync(Path.Combine(b.Directory, "invalid.pncpupdate")));
        await sb.ImportAsync(path); Assert.Equal(70, (await b.Repository.GetCountsAsync()).Contracts);
        Assert.Equal(0, (await sb.ExportAsync(Path.Combine(b.Directory, "empty.pncpupdate"))).Units);
    }

    private sealed class InlineProgress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }
    private static async Task SqlAsync(TestDatabase db, string sql)
    { await using var c = new SqliteConnection($"Data Source={db.Repository.DatabasePath};Foreign Keys=True;Pooling=False"); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
    private static async Task<object?> ScalarAsync(TestDatabase db, string sql)
    { await using var c = new SqliteConnection($"Data Source={db.Repository.DatabasePath};Pooling=False"); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; var value = await cmd.ExecuteScalarAsync(); return value is DBNull ? null : value; }
}
