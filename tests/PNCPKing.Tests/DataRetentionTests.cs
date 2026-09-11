using Microsoft.Data.Sqlite;
using PNCPKing.App.ViewModels;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class DataRetentionTests
{
    [Theory]
    [InlineData("2026-09-10", 11, "2025-10-10")]
    [InlineData("2026-09-10", 10, "2025-11-10")]
    [InlineData("2025-01-31", 11, "2024-02-29")]
    [InlineData("2026-01-31", 11, "2025-02-28")]
    [InlineData("2026-03-31", 11, "2025-04-30")]
    public void PeriodsUseCalendarMonths(string date, int months, string expected)
    {
        var today = DateOnly.Parse(date);
        Assert.Equal(DateOnly.Parse(expected), DataWindow.Start(today, months));
        Assert.Equal(DateOnly.Parse(expected), new DateRangeOption("Meses", null, Months: months).Start(today));
    }

    [Fact]
    public void CustomPeriodsAreInclusiveAndExpiredSavedPeriodsRestart()
    {
        var today = new DateOnly(2026, 9, 10);
        var start = DataWindow.Start(today);
        DataWindow.Validate(start, today, today);
        Assert.Throws<ArgumentException>(() => DataWindow.Validate(start.AddDays(-1), today, today));
        Assert.Throws<ArgumentException>(() => DataWindow.Validate(start, today.AddDays(1), today));
        Assert.Throws<ArgumentException>(() => DataWindow.Validate(today, start, today));
        Assert.Equal((start, today), DataWindow.Normalize(start.AddYears(-1), start.AddDays(-1), today));
        Assert.Equal((start, today.AddDays(-10)), DataWindow.Normalize(start.AddMonths(-1), today.AddDays(-10), today));
        Assert.Equal(today.AddDays(-6), new DateRangeOption("7 dias", 7).Start(today));
    }

    [Fact]
    public async Task RetentionRemovesPinnedAndQuotedPricesPreservesProjectsAndInvalidatesBaskets()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var cutoff = DataWindow.Start(today);
        var old = PriceCacheTests.RecentContract("old", cutoff.AddDays(-1), 1);
        var edge = PriceCacheTests.RecentContract("edge", cutoff, 2);
        var undated = PriceCacheTests.RecentContract("undated", today, 3) with { PublicationDate = null };
        await database.Repository.UpsertContractsAsync([old, edge, undated]);
        var cache = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        await cache.SetAuthorizationAsync(true, today.AddDays(-364), today);
        await cache.PrepareWindowAsync(today.AddDays(-364), today);
        foreach (var contract in new[] { old, edge })
        {
            await database.Repository.UpsertItemsAsync(contract.PncpId, [PriceCacheTests.Item(contract, 1)], false);
            await database.Repository.ReplaceItemResultsAsync(contract.PncpId, 1, [PriceCacheTests.Result(contract, 1, 1, true)]);
            await cache.MarkContractCompleteAsync(contract.PncpId, contract.GlobalUpdatedAt);
        }
        await cache.MarkContractPinnedAsync(old.PncpId);
        await cache.SetNationalPriceIndexAuthorizationAsync(true, today.AddDays(-364), today);
        await cache.PrepareNationalPriceIndexAsync(today.AddDays(-364), today);
        var quotes = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await quotes.CreateProjectAsync("Histórico misto");
        var lineId = Guid.NewGuid();
        var oldReference = Reference(lineId, "old-price", old.PncpId, old.PublicationDate) with { ResultDate = today };
        var edgeReference = Reference(lineId, "edge-price", edge.PncpId, edge.PublicationDate);
        var snapshotOnly = Reference(lineId, "snapshot", "absent-contract", old.PublicationDate);
        var webOld = Reference(lineId, "web-old", "", null) with {
            Source = QuotationReferenceSource.InternetIncisoIII, ResultDate = cutoff.AddDays(-1) };
        var webNew = Reference(lineId, "web-new", "", old.PublicationDate) with {
            Source = QuotationReferenceSource.InternetIncisoIII, ResultDate = today };
        var unknown = Reference(lineId, "unknown", undated.PncpId, null);
        await quotes.SaveSampleAsync(project.Id, lineId, new QuotationLineInput("Café", 1, "pacote", null, null),
            [oldReference, edgeReference, snapshotOnly, webOld, webNew, unknown]);
        var mixed = await quotes.SaveManualBasketAsync(lineId, null, "Mista", [oldReference.Id, edgeReference.Id]);
        await quotes.SaveManualBasketAsync(lineId, null, "Vencida", [snapshotOnly.Id]);
        await quotes.ConfirmBasketAsync(lineId, "manual:" + mixed.Id.ToString("N"));
        await quotes.SaveWorkspaceAsync(new QuotationItemSearchWorkspace {
            LineId = lineId, Slot = ItemSearchPromptSlot.Custom, SearchText = "café",
            StartDate = today.AddYears(-1), EndDate = today,
            Checkpoint = new() { ContractsExamined = 50, CandidateSetExhausted = true } });

        var result = await database.Repository.MaintainRetentionAsync(today);

        Assert.Equal(1, result.RemovedContracts);
        Assert.Equal(3, result.RemovedReferences);
        Assert.Equal(1, result.AffectedLines);
        Assert.Null(await database.Repository.GetContractAsync(old.PncpId));
        Assert.NotNull(await database.Repository.GetContractAsync(edge.PncpId));
        Assert.NotNull(await database.Repository.GetContractAsync(undated.PncpId));
        Assert.Null(await database.Repository.GetItemAsync(old.PncpId, 1));
        Assert.Single(await quotes.GetProjectsAsync());
        var line = Assert.Single(await quotes.GetLinesAsync(project.Id));
        Assert.Equal("Café", line.Description);
        Assert.False(line.SelectionConfirmed);
        Assert.Null(line.SelectedBasketKey);
        Assert.Equal(new[] { "edge-price", "unknown", "web-new" },
            (await quotes.GetReferencesAsync(lineId)).Select(r => r.Id).Order().ToArray());
        var basket = Assert.Single(await quotes.GetManualBasketsAsync(lineId));
        Assert.Equal(mixed.Id, basket.Id);
        var workspace = await quotes.GetWorkspaceAsync(lineId, ItemSearchPromptSlot.Custom);
        Assert.Equal(cutoff, workspace!.StartDate);
        Assert.Equal(0, workspace.Checkpoint.ContractsExamined);
        Assert.False(workspace.Checkpoint.CandidateSetExhausted);
        Assert.Equal(1, (await cache.GetProgressAsync()).TotalContracts);
        Assert.Equal(cutoff, (await cache.GetNationalPriceIndexProgressAsync()).StartDate);
        Assert.Equal((2L, 1L, 1L), await database.Repository.GetCountsAsync());
        Assert.False((await database.Repository.MaintainRetentionAsync(today)).Applied);

        // Delayed network writes and captured UI snapshots cannot restore expired rows.
        await database.Repository.UpsertContractsAsync([old]);
        Assert.Null(await database.Repository.GetContractAsync(old.PncpId));
        await quotes.SaveSampleAsync(project.Id, lineId, new QuotationLineInput("Café", 1, "pacote", null, null), [oldReference, edgeReference]);
        Assert.DoesNotContain(await quotes.GetReferencesAsync(lineId), r => r.Id == oldReference.Id);
        Assert.Equal(1, (await database.Repository.MaintainRetentionAsync(today.AddDays(1))).RemovedContracts);
        Assert.Null(await database.Repository.GetContractAsync(edge.PncpId));
    }

    [Fact]
    public async Task FailedCleanupRollsBackReferencesAndCanBeRetriedOffline()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var old = PriceCacheTests.RecentContract("old", DataWindow.Start(today).AddDays(-1), 1);
        await database.Repository.UpsertContractsAsync([old]);
        var quotes = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var project = await quotes.CreateProjectAsync("Rollback");
        var lineId = Guid.NewGuid();
        await quotes.SaveSampleAsync(project.Id, lineId, new QuotationLineInput("Café", 1, "pacote", null, null),
            [Reference(lineId, "old", old.PncpId, old.PublicationDate)]);
        await ExecuteAsync(database.Repository.DatabasePath,
            "CREATE TRIGGER refuse_delete BEFORE DELETE ON contracts BEGIN SELECT RAISE(ABORT, 'test interruption'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => database.Repository.MaintainRetentionAsync(today));
        Assert.Single(await quotes.GetReferencesAsync(lineId));
        Assert.NotNull(await database.Repository.GetContractAsync(old.PncpId));
        await ExecuteAsync(database.Repository.DatabasePath, "DROP TRIGGER refuse_delete;");
        Assert.Equal(1, (await database.Repository.MaintainRetentionAsync(today)).RemovedReferences);
    }

    [Fact]
    public async Task MigrationAndCompactionAreResumableVerifySearchAndRunOnlyOnce()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var old = DataWindow.Start(today).AddDays(-1);
        await database.Repository.UpsertContractsAsync(Enumerable.Range(1, 100).Select(i =>
            PriceCacheTests.RecentContract("old-" + i, old, 1) with { Object = new string('x', 12000) }).ToArray());
        var keep = PriceCacheTests.RecentContract("keep", today, 2) with { Object = "café preservado" };
        await database.Repository.UpsertContractsAsync([keep]);
        await database.Repository.UpsertItemsAsync(keep.PncpId, [PriceCacheTests.Item(keep, 1)], false);
        await DowngradeTo26Async(database.Repository.DatabasePath);
        var migrated = await database.Repository.InitializeAsync();
        Assert.Equal(26, migrated.PreviousVersion);
        Assert.Equal(27, migrated.CurrentVersion);
        Assert.Equal([27], migrated.AppliedMigrations);
        Assert.Empty((await database.Repository.InitializeAsync()).AppliedMigrations);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            database.Repository.MaintainRetentionAsync(today, compact: true, cancellationToken: cancelled.Token));
        var pending = await database.Repository.MaintainRetentionCoreAsync(today, true, false,
            CancellationToken.None, availableFreeBytes: 0);
        Assert.Equal(100, pending.RemovedContracts);
        Assert.False(pending.Compacted);
        Assert.Contains("pendente", pending.Message);
        using var interrupted = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            database.Repository.MaintainRetentionAsync(today, compact: true, cancellationToken: interrupted.Token,
                progress: new InlineProgress(message =>
                {
                    if (message.StartsWith("Compactando", StringComparison.Ordinal)) interrupted.Cancel();
                })));
        Assert.Equal(1, (await database.Repository.GetCountsAsync()).Contracts);
        var compacted = await database.Repository.MaintainRetentionAsync(today, compact: true);
        Assert.True(compacted.Compacted, compacted.Message);
        Assert.True(compacted.BytesAfter < compacted.BytesBefore, $"{compacted.BytesBefore} → {compacted.BytesAfter}");
        var matches = await database.Repository.SearchAsync(new SearchQuery("café", GeoScope.All));
        Assert.Equal(keep.PncpId, Assert.Single(matches.Results).PncpId);
        Assert.NotNull(await database.Repository.GetItemAsync(keep.PncpId, 1));
        Assert.False((await database.Repository.MaintainRetentionAsync(today, compact: true)).Compacted);
    }

    [Fact]
    public async Task QuotationImportDiscardsOldSnapshotsAndKeepsCurrentPrices()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var destination = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var quotes = new SqliteQuotationRepository(source.Repository.DatabasePath);
        var project = await quotes.CreateProjectAsync("Importação antiga");
        var lineId = Guid.NewGuid();
        var oldDate = DataWindow.Start(today).AddDays(-1).ToDateTime(TimeOnly.MinValue);
        await quotes.SaveSampleAsync(project.Id, lineId, new QuotationLineInput("Café", 1, "pacote", null, null),
            [Reference(lineId, "old", "absent-old", oldDate), Reference(lineId, "new", "absent-new", DateTimeOffset.Now)]);
        await quotes.SaveManualBasketAsync(lineId, null, "Mista", ["old", "new"]);
        var path = Path.Combine(source.Directory, "mixed.pncpcotacao");
        await new QuotationPackageService(source.Repository.DatabasePath, source.Directory).ExportAsync(path, project.Id);
        await destination.Repository.MaintainRetentionAsync(today);
        var result = await new QuotationPackageService(destination.Repository.DatabasePath, destination.Directory)
            .ImportAsync(path, QuotationPackageImportMode.PreserveIdentity);
        Assert.Contains(result.Warnings, warning => warning.Contains("11 meses"));
        var restored = new SqliteQuotationRepository(destination.Repository.DatabasePath);
        Assert.Equal("new", Assert.Single(await restored.GetReferencesAsync(lineId)).Id);
        Assert.Single(await restored.GetManualBasketsAsync(lineId));
        Assert.Single(await restored.GetLinesAsync(project.Id));
    }

    private static QuotationReference Reference(Guid line, string id, string contract, DateTimeOffset? publication) => new()
    {
        Id = id, LineId = line, ContractId = contract, ItemNumber = 1, ResultSequence = 1,
        ItemDescription = "Café", ItemUnit = "pacote", SupplierName = "Fornecedor",
        SupplierTaxId = "11222333000181", UnitPrice = 10,
        PublicationDate = publication, ResultDate = DateOnly.FromDateTime(DateTime.Today),
        Source = QuotationReferenceSource.PncpIncisoII,
        Adequacy = new(50, 20, 10, 15, 5, "Teste")
    };

    internal static Task DowngradeTo26Async(string path) => ExecuteAsync(path, """
        DROP TRIGGER IF EXISTS contracts_retention_insert;
        DROP TRIGGER IF EXISTS quotation_references_retention_insert;
        DROP TRIGGER IF EXISTS quotation_references_retention_update;
        ALTER TABLE maintenance_state DROP COLUMN last_retention_date;
        ALTER TABLE maintenance_state DROP COLUMN retention_cutoff;
        ALTER TABLE maintenance_state DROP COLUMN retention_compaction_completed;
        UPDATE schema_info SET version = 26 WHERE id = 1;
        """);

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Foreign Keys=True");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
