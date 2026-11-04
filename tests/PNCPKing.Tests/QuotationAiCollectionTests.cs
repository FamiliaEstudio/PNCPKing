using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.Tests;

public sealed class QuotationAiCollectionTests
{
    [Fact]
    public async Task CollectsTheUnionOfPlusCriteriaWithoutRawPriceOrLexicalFiltering()
    {
        await using var database = await MainSearchLocalSequenceTests.SeedAsync(65);
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var run = await CreateRunAsync(database, repository, 10);
        var prices = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var service = new QuotationAiCollectionService(prices, repository, database.Repository.DatabasePath);
        var manifest = await service.CollectAsync(run);
        var item = Assert.Single(manifest.Items);
        Assert.True(item.Complete);
        Assert.Equal(130, item.CandidateCount);
        var references = await ReadAllAsync(run.OutputPath, manifest, item.Line.Id);
        Assert.Equal(130, references.Select(value => value.Id).Distinct().Count());
        Assert.False(File.Exists(run.OutputPath + ".coleta.sqlite"));
        Assert.All(references, value => Assert.True(value.UnitPrice > item.Line.MaximumUnitPrice));
        Assert.Contains(references, value => value.MatchedSearchText.Contains('\n'));
        Assert.Empty(await repository.GetReferencesAsync(item.Line.Id));
        Assert.Empty(await repository.GetManualBasketsAsync(item.Line.Id));
        using (var archive = ZipFile.OpenRead(run.OutputPath))
        using (var reader = new StreamReader(archive.GetEntry(manifest.Parts[0].Path)!.Open()))
        {
            var headerJson = await reader.ReadLineAsync();
            Assert.NotNull(headerJson);
            Assert.DoesNotContain("\"candidateCount\":", headerJson);
            var candidateJson = await reader.ReadLineAsync();
            Assert.NotNull(candidateJson);
            Assert.DoesNotContain("\"state\":", candidateJson);
            Assert.DoesNotContain("\"adequacy\":", candidateJson);
        }
        Assert.Equal(33, SqliteContractRepository.CurrentSchemaVersion);
        // The existing query intentionally returns only unfinished runs.
        Assert.Null(await repository.GetLatestAutomationRunAsync(run.ProjectId));
    }

    [Fact]
    public async Task PageBudgetReportsPartialAndMatchesIndependentFirstPages()
    {
        await using var database = await MainSearchLocalSequenceTests.SeedAsync(80);
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var run = await CreateRunAsync(database, repository, 1);
        var prices = new SqlitePriceCacheRepository(database.Repository.DatabasePath);
        var service = new QuotationAiCollectionService(prices, repository, database.Repository.DatabasePath);
        var manifest = await service.CollectAsync(run);
        var item = Assert.Single(manifest.Items);
        Assert.False(item.Complete);
        Assert.Contains("parcial", item.Message);
        var expected = new HashSet<string>();
        foreach (var expression in SearchText.ParseMainCriteria(item.Line.SearchText))
        {
            var query = new SearchQuery(expression.OriginalText, run.GeoFilter, run.StartDate, run.EndDate,
                SearchSort.Nearest, 1, 50);
            var page = await prices.SearchLocalAfterAsync(query, expression, null, null, null, 50,
                PriceCacheLocalReadOrder.Discovery);
            foreach (var row in page.Rows!) expected.Add($"{row.Contract.PncpId}|{row.Item.ItemNumber}|{row.Result!.ResultSequence}");
        }
        Assert.Equal(expected.Order(), (await ReadAllAsync(run.OutputPath, manifest, item.Line.Id))
            .Select(value => value.Id).Order());
    }

    [Fact]
    public async Task PausedPagesResumeAfterRecoveryWritesWithoutDuplicates()
    {
        await using var database = await MainSearchLocalSequenceTests.SeedAsync(65);
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var run = await CreateRunAsync(database, repository, 10);
        var service = new QuotationAiCollectionService(new SqlitePriceCacheRepository(database.Repository.DatabasePath),
            repository, database.Repository.DatabasePath);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CollectAsync(run,
            new InlineProgress(value => { if (value.Stage == "collect" && value.CandidateCount > 0) cancellation.Cancel(); }),
            cancellation.Token));
        Assert.False(File.Exists(run.OutputPath));
        Assert.True(File.Exists(run.OutputPath + ".coleta.sqlite"));
        // Simulate the normal startup recovery writing the run before the user resumes.
        await repository.UpdateAutomationRunStateAsync(run.Id, QuotationAutomationRunState.Pending, "Recuperada na abertura.");
        var messages = new List<string>();
        var manifest = await service.CollectAsync(run, new InlineProgress(value => messages.Add(value.Message)));
        var item = Assert.Single(manifest.Items);
        var references = await ReadAllAsync(run.OutputPath, manifest, item.Line.Id);
        Assert.Equal(130, references.Count);
        Assert.Equal(130, references.Select(value => value.Id).Distinct().Count());
        Assert.Contains(messages, message => message.Contains("base foi alterada", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LargeDescriptionsProduceSmallVerifiedPartsWithoutTruncation()
    {
        await using var database = await MainSearchLocalSequenceTests.SeedAsync(65, longDescriptions: true);
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var run = await CreateRunAsync(database, repository, 10);
        var service = new QuotationAiCollectionService(new SqlitePriceCacheRepository(database.Repository.DatabasePath),
            repository, database.Repository.DatabasePath);
        var manifest = await service.CollectAsync(run);
        Assert.True(manifest.Parts.Count > 1);
        Assert.All(manifest.Parts, part => Assert.InRange(part.ByteLength, 1, QuotationAiPackage.TargetPartBytes));
        var reopened = await QuotationAiPackage.ReadManifestAsync(run.OutputPath);
        using var archive = ZipFile.OpenRead(run.OutputPath);
        var count = 0;
        await foreach (var reference in QuotationAiPackage.ReadItemReferencesAsync(archive, reopened, reopened.Items[0].Line.Id))
        {
            Assert.Contains("Descritivo técnico extenso", reference.ItemDescription);
            Assert.True(reference.ItemDescription.Length > 2000);
            count++;
        }
        Assert.Equal(130, count);
    }

    [Fact]
    public async Task BaseChangeImmediatelyBeforePauseDoesNotReuseAnOldCursorOrPrice()
    {
        await using var database = await MainSearchLocalSequenceTests.SeedAsync(65);
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var run = await CreateRunAsync(database, repository, 10);
        var service = new QuotationAiCollectionService(new SqlitePriceCacheRepository(database.Repository.DatabasePath),
            repository, database.Repository.DatabasePath);
        using var cancellation = new CancellationTokenSource();
        var changed = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CollectAsync(run,
            new InlineProgress(value =>
            {
                if (changed || value.Stage != "collect" || value.CandidateCount == 0) return;
                changed = true;
                var contract = PriceCacheTests.RecentContract("compound", DateOnly.FromDateTime(DateTime.Today).AddDays(-1), 1);
                database.Repository.ReplaceItemResultsAsync(contract.PncpId, 1,
                    [PriceCacheTests.Result(contract, 1, 1, true) with
                    { HomologatedUnitValueScaled = DecimalScale.ToScaled(7m) }]).GetAwaiter().GetResult();
                cancellation.Cancel();
            }), cancellation.Token));
        var manifest = await service.CollectAsync(run);
        var references = await ReadAllAsync(run.OutputPath, manifest, manifest.Items[0].Line.Id);
        Assert.Equal(130, references.Count);
        Assert.Equal(7m, Assert.Single(references, reference => reference.Id == "compound|1|1").UnitPrice);
    }

    [Fact]
    public async Task TamperedPartFailsItsHashBeforeReturningRows()
    {
        await using var database = await MainSearchLocalSequenceTests.SeedAsync(2);
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var run = await CreateRunAsync(database, repository, 10);
        var service = new QuotationAiCollectionService(new SqlitePriceCacheRepository(database.Repository.DatabasePath),
            repository, database.Repository.DatabasePath);
        var manifest = await service.CollectAsync(run);
        using (var archive = ZipFile.Open(run.OutputPath, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry(manifest.Parts[0].Path)!;
            string text;
            using (var reader = new StreamReader(entry.Open())) text = await reader.ReadToEndAsync();
            entry.Delete();
            var changed = archive.CreateEntry(manifest.Parts[0].Path);
            using var writer = new StreamWriter(changed.Open(), new UTF8Encoding(false));
            await writer.WriteAsync(text.Replace("Caf", "Xaf", StringComparison.Ordinal));
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAllAsync(run.OutputPath, manifest, manifest.Items[0].Line.Id));
    }

    [Fact]
    public async Task CompletedSnapshotsReexportWithoutSearchingAgainAfterBaseChanges()
    {
        await using var database = await MainSearchLocalSequenceTests.SeedAsync(2);
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var run = await CreateRunAsync(database, repository, 10);
        var service = new QuotationAiCollectionService(new SqlitePriceCacheRepository(database.Repository.DatabasePath),
            repository, database.Repository.DatabasePath);
        var first = await service.CollectAsync(run);
        await repository.RenameProjectAsync(run.ProjectId, "Nome alterado após a coleta");
        var progress = new List<QuotationAiProgress>();
        var second = await service.CollectAsync(run, new InlineProgress(progress.Add));
        Assert.Equal(first.Items[0].CapturedAt, second.Items[0].CapturedAt);
        Assert.Equal(first.Items[0].CandidateCount, second.Items[0].CandidateCount);
        Assert.DoesNotContain(progress, value => value.Message.StartsWith("Item ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResumeReconcilesAnItemWhoseCheckpointCommittedBeforeItsVisibleState()
    {
        await using var database = await MainSearchLocalSequenceTests.SeedAsync(2);
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var run = await CreateRunAsync(database, repository, 10);
        var service = new QuotationAiCollectionService(new SqlitePriceCacheRepository(database.Repository.DatabasePath),
            repository, database.Repository.DatabasePath);
        await ExecuteSqlAsync(database.Repository.DatabasePath, """
            CREATE TRIGGER fail_collection_state BEFORE UPDATE OF automation_state ON quotation_lines
            BEGIN SELECT RAISE(ABORT, 'Failure after the completed checkpoint'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => service.CollectAsync(run));
        Assert.True(File.Exists(run.OutputPath + ".coleta.sqlite"));
        Assert.False(File.Exists(run.OutputPath));
        Assert.Equal(QuotationAutomationItemState.Pending,
            Assert.Single(await repository.GetLinesAsync(run.ProjectId)).AutomationState);
        await ExecuteSqlAsync(database.Repository.DatabasePath, "DROP TRIGGER fail_collection_state;");

        var progress = new List<QuotationAiProgress>();
        var manifest = await service.CollectAsync(run, new InlineProgress(progress.Add));
        var item = Assert.Single(manifest.Items);
        var restored = Assert.Single(await repository.GetLinesAsync(run.ProjectId));
        Assert.True(item.Complete);
        Assert.Equal(4, item.CandidateCount);
        Assert.Equal(QuotationAutomationItemState.Completed, restored.AutomationState);
        Assert.Equal($"{item.CandidateCount:N0} candidato(s) para IA. {item.Message}", restored.AutomationMessage);
        Assert.DoesNotContain(progress, value => value.Message.StartsWith("Item ", StringComparison.Ordinal));
        Assert.Null(await repository.GetLatestAutomationRunAsync(run.ProjectId));
    }

    [Fact]
    public async Task ExistingZipRestoresPartialItemStatusAndDoesNotRewriteAlreadyConsistentItems()
    {
        await using var database = await MainSearchLocalSequenceTests.SeedAsync(65);
        var repository = new SqliteQuotationRepository(database.Repository.DatabasePath);
        var run = await CreateRunAsync(database, repository, 1);
        var service = new QuotationAiCollectionService(new SqlitePriceCacheRepository(database.Repository.DatabasePath),
            repository, database.Repository.DatabasePath);
        var first = await service.CollectAsync(run);
        var item = Assert.Single(first.Items);
        Assert.False(item.Complete);
        Assert.False(File.Exists(run.OutputPath + ".coleta.sqlite"));
        await repository.UpdateAutomationItemStateAsync(item.Line.Id, QuotationAutomationItemState.Pending,
            "Interrupted while recording the visible state.");
        await repository.UpdateAutomationRunStateAsync(run.Id, QuotationAutomationRunState.Failed, "Interrupted.");

        var progress = new List<QuotationAiProgress>();
        var resumed = await service.CollectAsync(run, new InlineProgress(progress.Add));
        var restored = Assert.Single(await repository.GetLinesAsync(run.ProjectId));
        Assert.Equal(QuotationAutomationItemState.CompletedWithWarning, restored.AutomationState);
        Assert.Equal($"{item.CandidateCount:N0} candidato(s) para IA. {item.Message}", restored.AutomationMessage);
        Assert.Equal(item.CapturedAt, resumed.Items[0].CapturedAt);
        Assert.DoesNotContain(progress, value => value.Message.StartsWith("Item ", StringComparison.Ordinal));
        Assert.Null(await repository.GetLatestAutomationRunAsync(run.ProjectId));

        // A coherent item must not cause another SQLite write on a second recovery.
        await ExecuteSqlAsync(database.Repository.DatabasePath, """
            CREATE TRIGGER forbid_redundant_state BEFORE UPDATE OF automation_state ON quotation_lines
            BEGIN SELECT RAISE(ABORT, 'Unexpected redundant state write'); END;
            """);
        await service.CollectAsync(run);
    }

    [Fact]
    public async Task PackageOf450ItemsKeepsEachLotIndependentAndReadsInOneArchiveSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        var projectId = Guid.NewGuid();
        var items = Enumerable.Range(1, 450).Select(number =>
        {
            var line = new QuotationLine
            {
                Id = Guid.NewGuid(), ProjectId = projectId, Description = $"Item técnico {number}",
                RequestedQuantity = 10, RequestedUnit = "unidade", SearchText = $"item {number}",
                DisplayOrder = number
            };
            return new QuotationAiPackageItem
            {
                Line = line, SpecificationHash = QuotationAiPackage.SpecificationHash(line), Complete = true,
                CandidateCount = 3
            };
        }).ToArray();
        var manifest = new QuotationAiPackageManifest
        {
            PackageId = Guid.NewGuid(), ProjectId = projectId, RunId = Guid.NewGuid(), Items = items
        };
        var path = Path.Combine(database.Directory, "450-itens.zip");
        manifest = await QuotationAiPackage.WriteAsync(path, manifest, References);
        Assert.Equal(450, manifest.Items.Count);
        Assert.Equal(1350, manifest.Items.Sum(item => item.CandidateCount));
        using var archive = ZipFile.OpenRead(path);
        long found = 0;
        foreach (var item in manifest.Items)
            await foreach (var reference in QuotationAiPackage.ReadItemReferencesAsync(archive, manifest, item.Line.Id))
            {
                Assert.Equal(item.Line.Id, reference.LineId);
                found++;
            }
        Assert.Equal(1350, found);

        static async IAsyncEnumerable<QuotationReference> References(Guid lineId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            for (var number = 1; number <= 3; number++)
            {
                token.ThrowIfCancellationRequested();
                yield return new QuotationReference
                {
                    Id = $"origem-{number}|1|1", LineId = lineId, ContractId = $"origem-{number}",
                    ItemNumber = 1, ResultSequence = 1, UnitPrice = 10 + number,
                    ItemDescription = "Descrição integral do candidato", ItemUnit = "unidade"
                };
            }
            await Task.CompletedTask;
        }
    }

    private static async Task<QuotationAutomationRun> CreateRunAsync(TestDatabase database,
        SqliteQuotationRepository repository, int pages)
    {
        var project = await repository.CreateProjectAsync("Coleta IA");
        var today = DateOnly.FromDateTime(DateTime.Today);
        var item = new QuotationImportItem(1, "café + açúcar + café açúcar", "Objeto deliberadamente diferente", 10,
            "unidade", 1m, 10m, pages);
        return await repository.CreateExternalAiRunAsync(project.Id, Path.Combine(database.Directory, "resultado.zip"),
            SearchGeoFilter.All, today.AddDays(-30), today, [item], AdequacyWeights.Default);
    }

    private static async Task<List<QuotationReference>> ReadAllAsync(string path, QuotationAiPackageManifest manifest, Guid id)
    {
        var result = new List<QuotationReference>();
        await foreach (var reference in QuotationAiPackage.ReadItemReferencesAsync(path, manifest, id)) result.Add(reference);
        return result;
    }

    private static async Task ExecuteSqlAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class InlineProgress(Action<QuotationAiProgress> report) : IProgress<QuotationAiProgress>
    {
        public void Report(QuotationAiProgress value) => report(value);
    }
}
