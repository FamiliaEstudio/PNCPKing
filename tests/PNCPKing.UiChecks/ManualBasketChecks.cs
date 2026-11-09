extern alias AppUnderTest;

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;
using AppModels = AppUnderTest::PNCPKing.App.ViewModels;
using BasketWindow = AppUnderTest::PNCPKing.App.Views.ManualBasketWindow;
using MainVm = AppUnderTest::PNCPKing.App.ViewModels.MainViewModel;

internal static partial class Program
{
    private static async Task CheckManualBasketLoadingAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var firstProject = new QuotationProject(Guid.NewGuid(), "Primeiro", now, now);
        var secondProject = new QuotationProject(Guid.NewGuid(), "Segundo", now, now);
        QuotationLine Line(QuotationProject project, string description) => new()
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Description = description,
            RequestedQuantity = 25m, RequestedUnit = "un"
        };
        var firstLine = Line(firstProject, "Item antigo");
        var secondLine = Line(secondProject, "Item escolhido");
        var thirdLine = Line(secondProject, "Outro item");
        var firstLoad = new TaskCompletionSource<IReadOnlyList<QuotationLine>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoad = new TaskCompletionSource<IReadOnlyList<QuotationLine>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var basketLoad = new TaskCompletionSource<IReadOnlyList<QuotationManualBasket>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestedProjects = new List<Guid>();
        var requestedItems = new List<Guid>();
        var basket = new QuotationManualBasket
        {
            Id = Guid.NewGuid(), LineId = secondLine.Id, Name = "Minha cesta", ReferenceIds = ["p1", "p2", "p3"]
        };
        var window = new BasketWindow([firstProject, secondProject],
            (id, _) => { requestedProjects.Add(id); return id == firstProject.Id ? firstLoad.Task : secondLoad.Task; },
            (id, _) =>
            {
                requestedItems.Add(id);
                return id == secondLine.Id ? basketLoad.Task : Task.FromResult<IReadOnlyList<QuotationManualBasket>>([]);
            }, firstProject.Id, "Café", "", "");
        var projects = (ComboBox)window.FindName("ProjectComboBox");
        var lines = (ComboBox)window.FindName("LineComboBox");
        var baskets = (ComboBox)window.FindName("BasketComboBox");
        var save = (Button)window.FindName("SaveButton");
        try
        {
            window.Show();
            await WaitForBasketUiAsync(() => requestedProjects.Count == 1);
            Require(requestedProjects.Single() == firstProject.Id && requestedItems.Count == 0 && !save.IsEnabled,
                "Abrir a janela carregou outros projetos/cestas ou liberou o salvamento prematuramente.");
            projects.SelectedItem = secondProject;
            secondLoad.SetResult([secondLine, thirdLine]);
            await WaitForBasketUiAsync(() => save.IsEnabled && lines.Items.Count == 3);
            firstLoad.SetResult([firstLine]);
            await Task.Delay(30);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(lines.Items.Count == 3 && requestedProjects.Count == 2 && requestedItems.Count == 0,
                "Uma resposta antiga substituiu os itens do projeto escolhido.");
            lines.SelectedIndex = 1;
            Require(!save.IsEnabled && requestedItems.SequenceEqual([secondLine.Id]),
                "A janela não aguardou as cestas do item escolhido.");
            lines.SelectedIndex = 2;
            await WaitForBasketUiAsync(() => save.IsEnabled);
            basketLoad.SetResult([basket]);
            await Task.Delay(30);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(baskets.Items.Count == 1 && ((TextBox)window.FindName("DescriptionTextBox")).Text == thirdLine.Description,
                "Uma resposta antiga misturou cestas de itens diferentes.");
            lines.SelectedIndex = 1;
            await WaitForBasketUiAsync(() => save.IsEnabled && baskets.Items.Count == 2);
            Require(((TextBox)window.FindName("BasketNameTextBox")).Text == "Manual 2",
                "A sugestão do nome perdeu a contagem das cestas manuais.");
            baskets.SelectedIndex = 1;
            window.Hide();
            _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            Require(window.ShowDialog() == true && window.ExistingProjectId == secondProject.Id &&
                    window.ExistingLineId == secondLine.Id && window.ExistingBasketId == basket.Id &&
                    window.Input?.RequestedQuantity == 25m,
                "Salvar não preservou o projeto, item, cesta ou quantidade escolhidos.");
        }
        finally { window.Close(); }

        var pending = new TaskCompletionSource<IReadOnlyList<QuotationLine>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken closingToken = default;
        var closing = new BasketWindow([firstProject], (_, token) => { closingToken = token; return pending.Task; },
            (_, _) => throw new InvalidOperationException("Não deve carregar cestas ao fechar."), firstProject.Id, "Café", "", "");
        closing.Show();
        await WaitForBasketUiAsync(() => closingToken.CanBeCanceled);
        closing.Close();
        Require(closingToken.IsCancellationRequested, "Fechar a janela não cancelou o carregamento pendente.");
        pending.SetResult([firstLine]);
        await Task.Delay(30);
        Console.WriteLine("Manual basket loading: chosen project/item only, stale responses, save destination and close cancellation passed.");
    }

    private static async Task WaitForBasketUiAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Require(timer.Elapsed < TimeSpan.FromSeconds(10), "O carregamento da janela de cesta não terminou.");
            await Task.Delay(10);
        }
    }

    private static async Task CheckSavedManualBasketRefreshAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.UiChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = new SqliteContractRepository(Path.Combine(directory, "test.db"));
            await database.InitializeAsync();
            var connections = new BasketCountingConnections(database.DatabasePath);
            var repository = new SqliteQuotationRepository(connections);
            var quotations = new QuotationService(repository, new QuotationAnalyzer());
            var project = await quotations.CreateProjectAsync("Atualização local");
            var input = new QuotationLineInput("Café", 25m, "KG", null, null);
            var rows = Enumerable.Range(1, 3).Select(index => DisplayRow(index).Source).ToArray();
            var saved = await quotations.SaveManualBasketAsync(project.Id, null, input, null, "Manual", rows);
            var other = await quotations.SaveManualBasketAsync(project.Id, null, input with { Description = "Outro item" },
                null, "Outra cesta", rows);
            var report = await quotations.GetReportAsync(project.Id);
            var main = (MainVm)RuntimeHelpers.GetUninitializedObject(typeof(MainVm));
            foreach (var field in typeof(MainVm).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                if (field.FieldType.Name.StartsWith("RangeObservableCollection`", StringComparison.Ordinal) ||
                    field.FieldType.Name.StartsWith("ObservableCollection`", StringComparison.Ordinal))
                    field.SetValue(main, Activator.CreateInstance(field.FieldType));
            SetSearchField(main, "_retainedItemRows", new Dictionary<string, AppModels.ItemSearchDisplayRow>());
            SetSearchField(main, "_quotationService", quotations);
            SetSearchField(main, "_selectedQuotationProject", new AppModels.QuotationProjectDisplay(report.Project));
            SetSearchField(main, "_quotationWordReport", report);
            SetSearchField(main, "<ExportQuotationWordCommand>k__BackingField", new AppModels.AsyncRelayCommand(() => Task.CompletedTask));
            SetSearchField(main, "<ExportQuotationPriceWordCommand>k__BackingField", new AppModels.AsyncRelayCommand(() => Task.CompletedTask));
            saved = await quotations.SaveManualBasketAsync(project.Id, saved.Analysis.Line.Id, input, saved.Basket.Id,
                saved.Basket.Name, [DisplayRow(4).Source]);
            connections.OpenCount = 0;
            await (Task)CallSearch(main, "ShowSavedManualBasketAsync", saved.Analysis, saved.Basket.Key)!;
            Require(connections.OpenCount <= 2, "Salvar uma cesta releu ou recalculou o projeto inteiro.");
            Require(main.QuotationLines.Select(value => value.Line.Id).SequenceEqual(report.Lines.Select(value => value.Line.Id)),
                "Atualizar uma cesta mudou a ordem dos itens.");
            Require(ReferenceEquals(main.QuotationLines.Single(value => value.Line.Id == other.Analysis.Line.Id).Analysis,
                    report.Lines.Single(value => value.Line.Id == other.Analysis.Line.Id)),
                "Atualizar uma cesta recalculou outro item.");
            Require(main.SelectedQuotationBasket?.Key == saved.Basket.Key &&
                    main.SelectedQuotationLine?.Analysis.Line.SelectionConfirmed == true &&
                    (await repository.GetLineAsync(project.Id, saved.Analysis.Line.Id))!.SelectionConfirmed,
                "Reaproveitar a análise perdeu a seleção ou a confirmação persistida.");
            Console.WriteLine("Manual basket refresh: preserved order, persisted confirmation and no unrelated item reads passed.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private sealed class BasketCountingConnections(string path) : ISqliteConnectionFactory
    {
        private readonly SqliteConnectionFactory _inner = new(path);
        public int OpenCount { get; set; }
        public string DatabasePath => _inner.DatabasePath;
        public ISqliteWorkCoordinator WorkCoordinator => _inner.WorkCoordinator;
        public int MigrationCacheKib => _inner.MigrationCacheKib;
        public long MmapBytes => _inner.MmapBytes;
        public int WorkerThreads => _inner.WorkerThreads;
        public string ProfileName => _inner.ProfileName;
        public SqliteSearchTuning SearchTuning => _inner.SearchTuning;
        public Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            OpenCount++;
            return _inner.OpenAsync(cancellationToken);
        }
    }
}
