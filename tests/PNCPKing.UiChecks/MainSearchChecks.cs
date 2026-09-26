extern alias AppUnderTest;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Services;
using SearchVm = AppUnderTest::PNCPKing.App.ViewModels.MainViewModel;
using SearchRow = AppUnderTest::PNCPKing.App.ViewModels.ItemSearchDisplayRow;
using SearchRows = AppUnderTest::PNCPKing.App.ViewModels.RangeObservableCollection<AppUnderTest::PNCPKing.App.ViewModels.ItemSearchDisplayRow>;

internal static partial class Program
{
    private static async Task CheckMainSearchAsync()
    {
        // Exercise the actual UI paging methods without starting background services or opening user settings/data.
        var vm = (SearchVm)RuntimeHelpers.GetUninitializedObject(typeof(SearchVm));
        var rows = new SearchRows();
        SetSearchField(vm, "<ItemSearchRows>k__BackingField", rows);
        SetSearchField(vm, "<ContractResults>k__BackingField",
            new AppUnderTest::PNCPKing.App.ViewModels.RangeObservableCollection<ContractRecord>());
        SetSearchField(vm, "_performanceTelemetry", new AppUnderTest::PNCPKing.App.Services.AppPerformanceTelemetry());
        foreach (var field in new[] { "_localPendingApplyKeys", "_visibleItemKeys", "_currentItemResultKeys" })
            SetSearchField(vm, field, new HashSet<string>());
        SetSearchField(vm, "_retainedItemRows", new Dictionary<string, SearchRow>());
        SetSearchField(vm, "_loadingContractResultsGeneration", -1);
        var bufferType = typeof(SearchVm).Assembly.GetType("PNCPKing.App.ViewModels.UiBatchBuffer`1")!.MakeGenericType(typeof(SearchRow));
        var buffer = Activator.CreateInstance(bufferType, new Action<IReadOnlyList<SearchRow>>(batch =>
        {
            foreach (var row in batch) rows.Add(row);
        }))!;
        SetSearchField(vm, "_itemResultBuffer", buffer);
        var repository = DispatchProxy.Create<IPriceCacheRepository, MainSearchRepositoryProxy>();
        var proxy = (MainSearchRepositoryProxy)repository;
        proxy.Rows = Enumerable.Range(0, 130).Select(index =>
        {
            var source = LongRow(index).Source;
            return source with { Item = source.Item with { Description = index < 65 ? "café torrado" : "açúcar cristal" } };
        }).ToArray();
        SetSearchField(vm, "_priceCacheRepository", repository);
        void Start(string text)
        {
            var expressions = SearchText.ParseMainCriteria(text);
            var sequence = new MainSearchLocalSequence(repository, new SearchQuery(text, GeoScope.All), expressions);
            SetSearchField(vm, "_localSearchSequence", sequence);
            SetSearchField(vm, "_activeSearchQuery", sequence.Queries[0]);
            SetSearchField(vm, "_activeItemSearchExpression", expressions[0]);
            SetSearchField(vm, "_queryText", text);
            SetSearchField(vm, "_activeCompoundPart", 0);
            SetSearchField(vm, "_localPriceRowsLoaded", 0);
            CallSearch(vm, "ResetCurrentItemRows");
        }
        async Task Load(CancellationToken token = default) =>
            await (Task<int>)CallSearch(vm, "LoadCompoundPricePageAsync", token, null, null)!;
        try
        {
            Start("café + açúcar");
            await Load();
            Require(rows.Count == 50 && rows.All(row => row.Description == "café torrado"), "Primeira página não usou o primeiro trecho.");
            Require((bool)typeof(SearchVm).GetProperty("HasCurrentQuotationSample",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!,
                "Preços locais carregados não habilitaram a amostra para Cotações.");
            var sample = await (Task<IReadOnlyList<ItemSearchRow>>)CallSearch(
                vm, "GetCurrentQuotationSampleAsync", null, null)!;
            Require(sample.Count == 50 && sample.All(row => row.Item.Description == "café torrado"),
                "A amostra da cotação não acompanhou os preços locais exibidos.");
            var kept = rows[0];
            kept.IsPinned = true;
            kept.IsSelectedForBasket = true;
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(rows);
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription("Description", System.ComponentModel.ListSortDirection.Ascending));
            await Load();
            Require(rows.Count == 100 && vm.ContractPageSummary.StartsWith("Trecho 2/2: açúcar"), "A segunda página/painel não alternou o trecho.");
            Require(ReferenceEquals(kept, rows[0]) && kept.IsPinned && kept.IsSelectedForBasket, "A alternância perdeu a identidade ou as marcas.");
            Require(vm.ItemSearchSummary.Contains("Há trechos pendentes"), "O resumo omitiu a continuação.");
            await (Task)CallSearch(vm, "FireBatchesAsync")!; // Network service intentionally absent.

            // Invalid syntax must leave results and active cursors untouched.
            SetSearchField(vm, "_queryText", "café ++ açúcar");
            await (Task)CallSearch(vm, "SearchAsync", true, false, false)!;
            Require(rows.Count == 100 && vm.StatusText.Contains("rejeitada"), "Critério inválido substituiu os resultados.");
            await Load();
            Require(rows.Count == 115, "Critério inválido reiniciou o cursor válido.");

            Start("café + café");
            await Load();
            var calls = proxy.Calls;
            await Load();
            Require(rows.Count == 50 && proxy.Calls == calls + 1, "Página repetida gerou duplicatas ou leitura sem limite.");

            // Deliver a partial page, stop, then resume the same part.
            Start("café + açúcar");
            using var cancellation = new CancellationTokenSource();
            proxy.AfterRow = count => { if (count == 3) cancellation.Cancel(); };
            try { await Load(cancellation.Token); throw new InvalidOperationException("Cancelamento não foi observado."); }
            catch (OperationCanceledException) { }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await (Task)bufferType.GetMethod("FlushAsync")!.Invoke(buffer, null)!;
            Require(rows.Count == 3, "Cancelamento perdeu a entrega parcial.");
            proxy.AfterRow = null;
            await Load();
            Require(rows.Count == 53 && rows.Select(row => Key(row.Source)).Distinct().Count() == 53, "Retomada perdeu ou repetiu preços.");

            // Invalidate data while a read is pending. The old response must not appear.
            Start("café + açúcar");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.BeforePage = async () => { entered.TrySetResult(); await release.Task; };
            var pending = Load();
            await entered.Task;
            CallSearch(vm, "InvalidateLocalPriceCursor");
            release.SetResult();
            await pending;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(rows.Count == 0, "Uma resposta anterior à atualização do banco foi aplicada.");
            proxy.BeforePage = null;
            await (Task)CallSearch(vm, "ReloadCompoundPricesAtomicallyAsync", CancellationToken.None)!;
            Require(rows.Count == 50, "A atualização não restaurou os cursores de todos os trechos.");
            await Load();
            Require(rows.Count == 100, "A atualização não restaurou a alternância.");

            // A new search supersedes pending responses from the old sequence.
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.BeforePage = async () => { entered.TrySetResult(); await release.Task; };
            pending = Load();
            await entered.Task;
            Start("açúcar + café");
            SetSearchField(vm, "_contractSearchGeneration", 1);
            release.SetResult();
            await pending;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(rows.Count == 0, "Uma pesquisa antiga contaminou a nova lista.");
            proxy.BeforePage = null;
            await Load();
            Require(rows.Count == 50 && rows.All(row => row.Description == "açúcar cristal"), "A nova pesquisa usou critérios anteriores.");
            Console.WriteLine("Main search: passed (alternation, deduplication, identity, cancellation, invalidation, stale responses, no network services)");
        }
        finally { ((IDisposable)buffer).Dispose(); }
    }

    private static void SetSearchField(SearchVm vm, string field, object value) =>
        typeof(SearchVm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
    private static object? CallSearch(SearchVm vm, string method, params object?[] args) =>
        typeof(SearchVm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args);

    public class MainSearchRepositoryProxy : DispatchProxy
    {
        public ItemSearchRow[] Rows = [];
        public int Calls;
        public Action<int>? AfterRow;
        public Func<Task>? BeforePage;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Require(method?.Name == "SearchLocalAfterAsync" && args?.Length == 9, "A pesquisa composta chamou uma operação inesperada.");
            return ReadAsync((SearchExpression)args![1]!, (decimal?)args[2], (decimal?)args[3],
                (PriceCacheLocalCursor?)args[4], (int)args[5]!, (PriceCacheLocalReadOrder)args[6]!,
                (IProgress<PriceCacheLocalProgress>?)args[7], (CancellationToken)args[8]!);
        }
        private async Task<PriceCacheLocalPage> ReadAsync(SearchExpression expression, decimal? minimum, decimal? maximum,
            PriceCacheLocalCursor? cursor, int size, PriceCacheLocalReadOrder order,
            IProgress<PriceCacheLocalProgress>? progress, CancellationToken token)
        {
            Require(size == 50 && order == PriceCacheLocalReadOrder.Discovery, "A pesquisa alterou o contrato do leitor atual.");
            Calls++;
            if (BeforePage is not null) await BeforePage();
            var matches = Rows.Where(row => expression.MatchesItem(row.Item.Description, row.Item.Unit) &&
                (minimum is null || row.HomologatedUnitValue >= minimum) &&
                (maximum is null || row.HomologatedUnitValue <= maximum)).ToArray();
            var start = (int)(cursor?.ItemRowId ?? 0);
            var page = matches.Skip(start).Take(size).ToArray();
            var number = (cursor?.Page ?? 0) + 1;
            var count = 0;
            foreach (var row in page)
            {
                token.ThrowIfCancellationRequested();
                count++;
                cursor = new(number, 0, 0, 0, "", row.Contract.PncpId, row.Item.ItemNumber, 1, start + count);
                progress?.Report(new([row], cursor, 0, count, true, false));
                AfterRow?.Invoke(count);
            }
            token.ThrowIfCancellationRequested();
            var more = start + page.Length < matches.Length;
            progress?.Report(new([], cursor, 0, page.Length, more, true));
            return new([], number, size, more, matches.Length, page, cursor);
        }
    }
}
