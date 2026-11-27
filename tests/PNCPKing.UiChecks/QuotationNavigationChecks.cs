extern alias AppUnderTest;

using System.IO;
using System.Reflection;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;
using AppModels = AppUnderTest::PNCPKing.App.ViewModels;
using AppServices = AppUnderTest::PNCPKing.App.Services;
using ItemWindow = AppUnderTest::PNCPKing.App.Views.QuotationItemWindow;

internal static partial class Program
{
    private static async Task CheckQuotationNavigationScopeAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.UiChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connections = new BasketCountingConnections(Path.Combine(directory, "synthetic.db"));
            var db = new SqliteContractRepository(connections);
            await db.InitializeAsync();
            var repo = new SqliteQuotationRepository(connections);
            var service = new QuotationService(repo, new QuotationAnalyzer());
            var project = await repo.CreateProjectAsync("Navegação sintética");
            async Task<Guid> CreateLine(string description)
            {
                var lineId = Guid.NewGuid();
                var prices = Enumerable.Range(1, 3).Select(i => new QuotationReference
                {
                    Id = $"p{i}", LineId = lineId, ContractId = $"11222333000181-1-{i:D6}/2026",
                    ItemNumber = 1, ResultSequence = 1, SupplierName = "Fornecedor " + i,
                    ItemDescription = description, ItemUnit = "un", UnitPrice = 10, ResultDate = new(2026, 11, 1)
                }).ToArray();
                await repo.SaveSampleAsync(project.Id, lineId, new(description, 1, "un", null, null), prices);
                return lineId;
            }
            var first = await CreateLine("Kit A");
            var second = await CreateLine("Kit B");
            var manual = await repo.SaveManualBasketAsync(second, null, "Manual confirmada", ["p1", "p2", "p3"]);
            await repo.ConfirmBasketAsync(second, manual.Key);
            var third = await CreateLine("Kit C");
            for (var i = 1; i <= 101; i++)
                await repo.SaveManualBasketAsync(third, null, $"Manual {i:D3}", ["p1", "p2", "p3"]);
            var thirdAnalysis = (await service.GetAnalysisAsync(project.Id, third))!;
            var offPage = thirdAnalysis.Baskets.Skip(100).First(b => b.IsManual);
            await repo.ConfirmBasketAsync(third, offPage.Key);
            var report = await service.GetReportAsync(project.Id);
            var main = CreateBasketTestMain(service, report);
            var rows = report.Lines.Select(l => new AppModels.QuotationLineDisplay(l)).ToArray();
            main.SelectedQuotationLine = rows.Single(l => l.Line.Id == first);
            var shared = main.QuotationBaskets.Single(b => !b.Source.IsManual).Key;
            main.SelectedQuotationBasket = main.QuotationBaskets.Single(b => b.Key == shared);
            connections.OpenCount = 0;
            main.SelectedQuotationLine = rows.Single(l => l.Line.Id == second);
            var keptVisible = main.SelectedQuotationBasket?.Key == shared;
            main.SelectedQuotationLine = rows.Single(l => l.Line.Id == third);
            var stayedOnFirstPage = main.QuotationBasketPage == 1 && main.SelectedQuotationBasket?.Key != offPage.Key;
            main.SelectedQuotationLine = rows.Single(l => l.Line.Id == second);
            var navigationOpens = connections.OpenCount;

            var store = new InternetEvidenceStore(directory);
            var documents = new ScopeDocumentPages();
            var item = new AppModels.QuotationItemViewModel(main, service, new QuotationItemSearchService(db, repo, null!),
                new SqliteSweetCodeRepository(connections), new InternetPriceService(repo, service, store),
                store, documents, null!, directory, project.Id, second);
            var refreshField = typeof(AppModels.QuotationItemViewModel).GetField("_lastRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var finishes = new List<DateTimeOffset>();
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(item.IsBusy) && !item.IsBusy)
                    finishes.Add((DateTimeOffset)refreshField.GetValue(item)!);
            };
            connections.OpenCount = 0;
            var closeWindow = await OpenScopeItemWindowAsync(item, directory, store);
            try
            {
                var coldOpens = connections.OpenCount;
                var coldFinishes = finishes.Count;
                var timestampAfterNotification = finishes.Count == 1 && finishes[0] == DateTimeOffset.MinValue &&
                    (DateTimeOffset)refreshField.GetValue(item)! > finishes[0];
                var openedOnManual = item.SelectedBasket?.Key == manual.Key;
                connections.OpenCount = 0;
                await item.PrepareReferenceDocumentsAsync("p1");
                var ordinaryDocumentOpens = connections.OpenCount;
                var beforePending = finishes.Count;
                documents.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var documentTask = item.PrepareReferenceDocumentsAsync("p1");
                Require(item.IsBusy, "A operação de documentos deveria estar em andamento.");
                await item.LoadAsync(shared);
                documents.Pause.SetResult();
                await documentTask;
                await WaitForBasketUiAsync(() => !item.IsBusy);
                var documentOpens = connections.OpenCount - ordinaryDocumentOpens;
                var documentFinishes = finishes.Count - beforePending;
                var keptManual = item.SelectedBasket?.Key == manual.Key;
                connections.OpenCount = 0;
                var beforeExplicit = finishes.Count;
                await item.LoadAsync();
                await WaitForBasketUiAsync(() => !item.IsBusy);
                var explicitFinishes = finishes.Count - beforeExplicit;
                Console.WriteLine($"navigation opens={navigationOpens} retained={keptVisible} first-page={stayedOnFirstPage}; " +
                    $"cold opens={coldOpens} finishes={coldFinishes} manual={openedOnManual} timestamp-after={timestampAfterNotification}; " +
                    $"document opens={ordinaryDocumentOpens}; document-pending opens={documentOpens} finishes={documentFinishes} retained={keptManual}; " +
                    $"explicit opens={connections.OpenCount} finishes={explicitFinishes}");
                Require(keptVisible && stayedOnFirstPage && main.QuotationBasketPage == 1 && navigationOpens == 0 &&
                    main.SelectedQuotationLine?.Line is { SelectionConfirmed: true } chosen && chosen.SelectedBasketKey == manual.Key,
                    "Navegar deve manter a cesta visível compatível e a escolha persistida.");
                Require(coldOpens > 0 && coldFinishes == 1 && openedOnManual && timestampAfterNotification,
                    "O primeiro carregamento ou a ordem da notificação IsBusy mudou.");
                Require(ordinaryDocumentOpens == 0 && documentOpens == 0 && documentFinishes == 1 && keptManual,
                    "O encerramento dos documentos consumiu a recarga pendente ou alterou a seleção.");
                Require(explicitFinishes == 2 && item.SelectedBasket?.Key == manual.Key,
                    "A recarga explícita não consumiu exatamente uma atualização pendente, sem trocar a chave.");
            }
            finally { await closeWindow(); }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private static async Task<Func<Task>> OpenScopeItemWindowAsync(AppModels.QuotationItemViewModel item, string directory, InternetEvidenceStore store)
    {
        var settings = new AppServices.AppSettingsService();
        typeof(AppServices.AppSettingsService).GetField("_settingsPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(settings, Path.Combine(directory, "settings.json"));
        var window = new ItemWindow(item, null!, store, new AppServices.DataGridColumnLayoutService(settings, new(directory, true)));
        window.Show();
        await WaitForBasketUiAsync(() => window.IsLoaded && item.Line is not null && !item.IsBusy);
        return async () =>
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };
    }

    private sealed class ScopeDocumentPages : IContractRelevantPageService
    {
        public TaskCompletionSource? Pause { get; set; }
        public async Task<RelevantDocumentPagesResult> CreateAsync(PncpContractKey contract, IReadOnlyList<string> expressions,
            string destinationPath, IProgress<DocumentProcessingProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (Pause is not null) await Pause.Task.WaitAsync(cancellationToken);
            return new() { Bundle = new() { Contract = contract, Pdfs = [] }, Warnings = [], Expressions = [] };
        }
    }
}
