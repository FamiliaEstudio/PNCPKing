using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media.Imaging;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Services;
using SearchParser = PNCPKing.Core.Search.SearchText;

namespace PNCPKing.App.ViewModels;

public sealed class QuotationItemViewModel : ObservableObject, IAsyncDisposable
{
    private readonly MainViewModel _main;
    private readonly QuotationService _quotations;
    private readonly IQuotationItemSearchService _itemSearch;
    private readonly ISweetCodeRepository _sweetCodes;
    private readonly IInternetPriceService _internetPrices;
    private readonly IInternetEvidenceStore _evidenceStore;
    private readonly IContractRelevantPageService _relevantPages;
    private readonly IPdfPageRasterizer _rasterizer;
    private readonly ICatalogRepository _catalogRepository;
    private readonly ICatalogSearchService _catalogSearch;
    private readonly string _dataFolder;
    private readonly Guid _projectId;
    private readonly Guid _lineId;
    private QuotationLineDisplay? _line;
    private QuotationBasketDisplay? _selectedBasket;
    private QuotationPriceDisplayRow? _selectedPrice;
    private QuotationReferenceDisplay? _selectedReference;
    private ItemSearchDisplayRow? _selectedSearchRow;
    private InternetPriceDraft? _selectedDraft;
    private ReferenceViewScope _referenceScope = ReferenceViewScope.InBasket;
    private ItemSearchPromptSlot _selectedPromptSlot = ItemSearchPromptSlot.Restrictive;
    private QuotationItemSearchWorkspace? _workspace;
    private string _searchText = string.Empty;
    private SearchGeoFilter _selectedGeoFilter = SearchGeoFilter.All;
    private SearchSortOption _selectedSortOption;
    private DateRangeOption _selectedDateRange;
    private DateTime? _customStartDate;
    private DateTime? _customEndDate;
    private string _minimumPriceText = string.Empty;
    private string _maximumPriceText = string.Empty;
    private int _batchCount = ItemSearchDefaults.InitialBatchCount;
    private bool _sweetCodeEnabled;
    private string? _selectedSweetCode;
    private string _searchSummary = "Selecione um prompt e pesquise.";
    private double _searchProgress;
    private string _progressText = "Carregando progresso…";
    private string _documentSummary = "Selecione uma referência e prepare as páginas relevantes.";
    private string? _relevantPdfPath;
    private bool _isBusy;
    private bool _isSearchBusy;
    private bool _refreshPending;
    private bool _disposed;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private CancellationTokenSource? _searchCancellation;
    private string _catalogQuery = string.Empty;
    private CatalogKindOption _selectedCatalogKind = new("Todos", null);
    private CatalogSearchResultDisplay? _selectedCatalogResult;
    private CatalogHierarchyFilter? _catalogHierarchyFilter;
    private bool _catalogAvailable;
    private bool _catalogHierarchyLoaded;
    private bool _isCatalogSearchBusy;
    private int _catalogPage = 1;
    private int _catalogTotalCandidates;
    private string _catalogStatus = "Verificando o catálogo local…";

    public QuotationItemViewModel(
        MainViewModel main,
        QuotationService quotations,
        IQuotationItemSearchService itemSearch,
        ISweetCodeRepository sweetCodes,
        IInternetPriceService internetPrices,
        IInternetEvidenceStore evidenceStore,
        IContractRelevantPageService relevantPages,
        IPdfPageRasterizer rasterizer,
        string dataFolder,
        Guid projectId,
        Guid lineId)
    {
        _main = main;
        _quotations = quotations;
        _itemSearch = itemSearch;
        _sweetCodes = sweetCodes;
        _internetPrices = internetPrices;
        _evidenceStore = evidenceStore;
        _relevantPages = relevantPages;
        _rasterizer = rasterizer;
        _catalogRepository = main.CatalogRepository;
        _catalogSearch = main.CatalogSearchService;
        _dataFolder = dataFolder;
        _projectId = projectId;
        _lineId = lineId;
        GeoFilters = BuildGeoFilters();
        SortOptions =
        [
            new SearchSortOption("Relevância", SearchSort.Relevance),
            new SearchSortOption("Mais recentes", SearchSort.Newest),
            new SearchSortOption("Mais próximas", SearchSort.Nearest)
        ];
        _selectedSortOption = SortOptions[2];
        DateRanges =
        [
            new DateRangeOption("Últimos 7 dias", 7),
            new DateRangeOption("Últimos 30 dias", 30),
            new DateRangeOption("Últimos 90 dias", 90),
            new DateRangeOption("Últimos 180 dias", 180),
            new DateRangeOption("Últimos 365 dias", 365),
            new DateRangeOption("Datas personalizadas", null, true)
        ];
        _selectedDateRange = DateRanges[4];
        _customStartDate = DateTime.Today.AddDays(-364);
        _customEndDate = DateTime.Today;
        _main.TimedQuotationProgressChanged += OnTimedProgress;
        CatalogKinds =
        [
            _selectedCatalogKind,
            new CatalogKindOption("CATMAT", CatalogKind.Catmat),
            new CatalogKindOption("CATSER", CatalogKind.Catser)
        ];
        UpdateProgress(_main.LatestTimedQuotationProgress);
    }

    public MainViewModel Main => _main;
    public Guid ProjectId => _projectId;
    public Guid LineId => _lineId;
    public IReadOnlyList<SearchGeoFilter> GeoFilters { get; }
    public IReadOnlyList<SearchSortOption> SortOptions { get; }
    public IReadOnlyList<DateRangeOption> DateRanges { get; }
    public ObservableCollection<QuotationBasketDisplay> Baskets { get; } = [];
    public ObservableCollection<QuotationReferenceDisplay> References { get; } = [];
    public ObservableCollection<QuotationPriceDisplayRow> VisibleReferences { get; } = [];
    public ObservableCollection<ItemSearchDisplayRow> SearchRows { get; } = [];
    public ObservableCollection<InternetPriceDraft> Drafts { get; } = [];
    public ObservableCollection<DocumentThumbnailDisplay> DocumentThumbnails { get; } = [];
    public ObservableCollection<string> SweetCodeExpressions { get; } = [];
    public ObservableCollection<CatalogSearchResultDisplay> CatalogResults { get; } = [];
    public ObservableCollection<CatalogHierarchyNode> CatalogHierarchy { get; } = [];
    public IReadOnlyList<CatalogKindOption> CatalogKinds { get; }

    public QuotationLineDisplay? Line
    {
        get => _line;
        private set
        {
            if (SetProperty(ref _line, value))
            {
                OnPropertyChanged(nameof(Header));
                OnPropertyChanged(nameof(ItemSummary));
                OnPropertyChanged(nameof(CatalogSelectionSummary));
            }
        }
    }

    public string Header => Line?.Description ?? "Item da cotação";

    public string ItemSummary => Line is null
        ? string.Empty
        : $"Quantidade {Line.RequestedQuantity:N4} {Line.RequestedUnit} · " +
          $"alvo {Line.RequestedBasketSize:N0} preços · " +
          $"coletados {Line.CollectedCount:N0} · elegíveis {Line.EligibleCount:N0} · " +
          $"nível {Line.ActivePromptLevel} · situação {Line.Status}";

    public string CatalogQuery
    {
        get => _catalogQuery;
        set => SetProperty(ref _catalogQuery, value);
    }

    public CatalogKindOption SelectedCatalogKind
    {
        get => _selectedCatalogKind;
        set
        {
            if (SetProperty(ref _selectedCatalogKind, value))
            {
                _catalogHierarchyFilter = null;
                OnPropertyChanged(nameof(CatalogHierarchyLabel));
            }
        }
    }

    public CatalogSearchResultDisplay? SelectedCatalogResult
    {
        get => _selectedCatalogResult;
        set => SetProperty(ref _selectedCatalogResult, value);
    }

    public bool IsCatalogAvailable
    {
        get => _catalogAvailable;
        private set => SetProperty(ref _catalogAvailable, value);
    }

    public bool IsCatalogSearchBusy
    {
        get => _isCatalogSearchBusy;
        private set => SetProperty(ref _isCatalogSearchBusy, value);
    }

    public int CatalogPage
    {
        get => _catalogPage;
        private set
        {
            if (SetProperty(ref _catalogPage, Math.Max(1, value)))
            {
                OnPropertyChanged(nameof(CatalogPageSummary));
            }
        }
    }

    public int CatalogTotalCandidates
    {
        get => _catalogTotalCandidates;
        private set
        {
            if (SetProperty(ref _catalogTotalCandidates, value))
            {
                OnPropertyChanged(nameof(CatalogPageSummary));
            }
        }
    }

    public string CatalogPageSummary => CatalogTotalCandidates == 0
        ? "Nenhum candidato"
        : $"Página {CatalogPage:N0} · {CatalogTotalCandidates:N0} candidato(s) ordenado(s)";

    public string CatalogStatus
    {
        get => _catalogStatus;
        private set => SetProperty(ref _catalogStatus, value);
    }

    public string CatalogHierarchyLabel => _catalogHierarchyFilter is null
        ? "Toda a hierarquia"
        : "Filtro da árvore aplicado";

    public string CatalogSelectionSummary => Line?.Line.CatalogSelection switch
    {
        null => "Código principal: nenhum",
        { IsActive: false } selection =>
            $"Código principal: {selection.Label} — INATIVO; escolha um substituto.",
        { } selection => $"Código principal: {selection.Label} — {selection.Description}"
    };

    public QuotationBasketDisplay? SelectedBasket
    {
        get => _selectedBasket;
        set
        {
            if (SetProperty(ref _selectedBasket, value))
            {
                SelectedPrice = null;
                RebuildVisibleReferences();
                OnPropertyChanged(nameof(BasketCalculation));
            }
        }
    }

    public QuotationPriceDisplayRow? SelectedPrice
    {
        get => _selectedPrice;
        set
        {
            if (SetProperty(ref _selectedPrice, value))
            {
                SelectedReference = value is null
                    ? null
                    : new QuotationReferenceDisplay(value.Source);
            }
        }
    }

    public QuotationReferenceDisplay? SelectedReference
    {
        get => _selectedReference;
        set => SetProperty(ref _selectedReference, value);
    }

    public ItemSearchDisplayRow? SelectedSearchRow
    {
        get => _selectedSearchRow;
        set => SetProperty(ref _selectedSearchRow, value);
    }

    public InternetPriceDraft? SelectedDraft
    {
        get => _selectedDraft;
        set => SetProperty(ref _selectedDraft, value);
    }

    public ReferenceViewScope ReferenceScope
    {
        get => _referenceScope;
        set
        {
            if (SetProperty(ref _referenceScope, value))
            {
                RebuildVisibleReferences();
                OnPropertyChanged(nameof(ReferenceScopeSummary));
            }
        }
    }

    public string ReferenceScopeSummary => ReferenceScope switch
    {
        ReferenceViewScope.InBasket => "Preços da cesta selecionada",
        ReferenceViewScope.EligibleOutsideBasket => "Elegíveis fora da cesta",
        ReferenceViewScope.RejectedOrDuplicate => "Descartados ou duplicados",
        _ => "Todos os preços encontrados"
    };

    public ItemSearchPromptSlot SelectedPromptSlot
    {
        get => _selectedPromptSlot;
        private set
        {
            if (SetProperty(ref _selectedPromptSlot, value))
            {
                OnPropertyChanged(nameof(PromptSlotLabel));
            }
        }
    }

    public string PromptSlotLabel => SelectedPromptSlot switch
    {
        ItemSearchPromptSlot.Restrictive => "Restritivo",
        ItemSearchPromptSlot.Intermediate => "Intermediário",
        ItemSearchPromptSlot.Broad => "Amplo",
        _ => "Personalizado"
    };

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshSweetCodeSuggestions();
            }
        }
    }

    public SearchGeoFilter SelectedGeoFilter
    {
        get => _selectedGeoFilter;
        set => SetProperty(ref _selectedGeoFilter, value);
    }

    public SearchSortOption SelectedSortOption
    {
        get => _selectedSortOption;
        set => SetProperty(ref _selectedSortOption, value);
    }

    public DateRangeOption SelectedDateRange
    {
        get => _selectedDateRange;
        set
        {
            if (SetProperty(ref _selectedDateRange, value))
            {
                OnPropertyChanged(nameof(IsCustomDateRange));
            }
        }
    }

    public bool IsCustomDateRange => SelectedDateRange.IsCustom;

    public DateTime? CustomStartDate
    {
        get => _customStartDate;
        set => SetProperty(ref _customStartDate, value);
    }

    public DateTime? CustomEndDate
    {
        get => _customEndDate;
        set => SetProperty(ref _customEndDate, value);
    }

    public string MinimumPriceText
    {
        get => _minimumPriceText;
        set => SetProperty(ref _minimumPriceText, value);
    }

    public string MaximumPriceText
    {
        get => _maximumPriceText;
        set => SetProperty(ref _maximumPriceText, value);
    }

    public int BatchCount
    {
        get => _batchCount;
        set => SetProperty(ref _batchCount, Math.Clamp(value, 1, 100));
    }

    public bool SweetCodeEnabled
    {
        get => _sweetCodeEnabled;
        set
        {
            if (SetProperty(ref _sweetCodeEnabled, value))
            {
                _ = _sweetCodes.SetEnabledAsync(value);
                RefreshSweetCodeSuggestions();
            }
        }
    }

    public string? SelectedSweetCode
    {
        get => _selectedSweetCode;
        set => SetProperty(ref _selectedSweetCode, value);
    }

    public string SearchSummary
    {
        get => _searchSummary;
        private set => SetProperty(ref _searchSummary, value);
    }

    public double SearchProgress
    {
        get => _searchProgress;
        private set => SetProperty(ref _searchProgress, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string DocumentSummary
    {
        get => _documentSummary;
        private set => SetProperty(ref _documentSummary, value);
    }

    public string? RelevantPdfPath
    {
        get => _relevantPdfPath;
        private set
        {
            if (SetProperty(ref _relevantPdfPath, value))
            {
                OnPropertyChanged(nameof(CanOpenRelevantPdf));
            }
        }
    }

    public bool CanOpenRelevantPdf =>
        !string.IsNullOrWhiteSpace(RelevantPdfPath) && File.Exists(RelevantPdfPath);

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsSearchBusy
    {
        get => _isSearchBusy;
        private set => SetProperty(ref _isSearchBusy, value);
    }

    public string BasketCalculation => SelectedBasket is null
        ? "Nenhuma cesta selecionada."
        : $"Média {SelectedBasket.AveragePrice:C4} · menor {SelectedBasket.MinimumPrice:C4} · " +
          $"maior {SelectedBasket.MaximumPrice:C4} · " +
          $"desvio máximo {SelectedBasket.MaximumDeviationPercent:N2}% · " +
          $"{SelectedBasket.Status}";

    public bool SearchDefinitionChanged =>
        _workspace is not null && !HasSameDefinition(_workspace, BuildWorkspace());

    public async Task LoadAsync(string? preferredBasketKey = null)
    {
        if (IsBusy)
        {
            _refreshPending = true;
            return;
        }

        IsBusy = true;
        try
        {
            var previousBasketKey = preferredBasketKey ?? SelectedBasket?.Key;
            var analyses = await _quotations.GetAnalysesAsync(_projectId).ConfigureAwait(true);
            var analysis = analyses.SingleOrDefault(item => item.Line.Id == _lineId)
                           ?? throw new InvalidOperationException("O item da cotação não existe mais.");
            Line = new QuotationLineDisplay(analysis);
            if (string.IsNullOrWhiteSpace(CatalogQuery))
            {
                CatalogQuery = analysis.Line.EffectiveDisplayName;
            }
            await RefreshCatalogAvailabilityAsync().ConfigureAwait(true);
            UpdateProgress(_main.LatestTimedQuotationProgress);
            Baskets.Clear();
            foreach (var basket in analysis.Baskets)
            {
                Baskets.Add(new QuotationBasketDisplay(
                    basket,
                    basket.Key == analysis.Line.SelectedBasketKey));
            }

            References.Clear();
            foreach (var reference in analysis.References
                         .OrderBy(reference => reference.Source)
                         .ThenByDescending(reference => reference.Adequacy.Total)
                         .ThenBy(reference => reference.UnitPrice))
            {
                References.Add(new QuotationReferenceDisplay(reference));
            }

            Drafts.Clear();
            foreach (var draft in await _internetPrices.GetDraftsAsync(_lineId).ConfigureAwait(true))
            {
                Drafts.Add(draft);
            }

            SelectedBasket = Baskets.FirstOrDefault(item => item.Key == previousBasketKey) ??
                             Baskets.FirstOrDefault(item => item.Key == analysis.Line.SelectedBasketKey) ??
                             Baskets.FirstOrDefault();
            if (_workspace is null)
            {
                var library = await _sweetCodes.LoadAsync().ConfigureAwait(true);
                _sweetCodeEnabled = library.Enabled;
                SweetCodeExpressions.Clear();
                foreach (var expression in library.Expressions)
                {
                    SweetCodeExpressions.Add(expression);
                }

                OnPropertyChanged(nameof(SweetCodeEnabled));
                await LoadPromptSlotAsync(ItemSearchPromptSlot.Restrictive).ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
            _lastRefresh = DateTimeOffset.UtcNow;
            if (_refreshPending)
            {
                _refreshPending = false;
                _ = LoadAsync();
            }
        }
    }

    public async Task RenameItemAsync(string displayName)
    {
        var previous = Line?.Line.EffectiveDisplayName ?? string.Empty;
        await _quotations.RenameLineDisplayNameAsync(_lineId, displayName).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(CatalogQuery) ||
            string.Equals(CatalogQuery.Trim(), previous, StringComparison.Ordinal))
        {
            CatalogQuery = displayName;
        }

        await LoadAsync(SelectedBasket?.Key).ConfigureAwait(true);
        await _main.RefreshQuotationItemAsync(_projectId, _lineId).ConfigureAwait(true);
    }

    public async Task SearchCatalogAsync(int page = 1)
    {
        if (!IsCatalogAvailable)
        {
            throw new InvalidOperationException(
                "A pesquisa será liberada depois que o primeiro snapshot completo de CATMAT e CATSER for publicado.");
        }

        if (string.IsNullOrWhiteSpace(CatalogQuery))
        {
            throw new ArgumentException("Digite um nome, característica ou código para pesquisar.");
        }

        IsCatalogSearchBusy = true;
        try
        {
            var result = await _catalogSearch.SearchAsync(new CatalogSearchQuery(
                    CatalogQuery,
                    SelectedCatalogKind.Kind,
                    _catalogHierarchyFilter,
                    Math.Max(1, page),
                    50))
                .ConfigureAwait(true);
            CatalogResults.Clear();
            foreach (var item in result.Results)
            {
                CatalogResults.Add(new CatalogSearchResultDisplay(item));
            }

            CatalogPage = result.Page;
            CatalogTotalCandidates = result.TotalCandidates;
            SelectedCatalogResult = CatalogResults.FirstOrDefault();
            CatalogStatus = CatalogResults.Count == 0
                ? "Nenhum código ativo corresponde aos termos e ao filtro escolhidos."
                : "Candidatos ativos ordenados por correspondência; verde é igual/equivalente, vermelho é conflito e cinza é ausência.";
        }
        finally
        {
            IsCatalogSearchBusy = false;
        }
    }

    public Task PreviousCatalogPageAsync() => SearchCatalogAsync(Math.Max(1, CatalogPage - 1));
    public Task NextCatalogPageAsync() => SearchCatalogAsync(CatalogPage + 1);

    public void ApplyCatalogHierarchy(CatalogHierarchyNode? node)
    {
        _catalogHierarchyFilter = node?.Filter;
        if (node is not null)
        {
            SelectedCatalogKind = CatalogKinds.First(option => option.Kind == node.Kind);
            _catalogHierarchyFilter = node.Filter;
        }

        OnPropertyChanged(nameof(CatalogHierarchyLabel));
    }

    public async Task AssignSelectedCatalogAsync()
    {
        var entry = SelectedCatalogResult?.Source.Entry
                    ?? throw new InvalidOperationException("Selecione um código ativo para atribuir.");
        await _quotations.SetLineCatalogSelectionAsync(_lineId, new QuotationCatalogSelection
        {
            Kind = entry.Kind,
            Code = entry.Code,
            Description = entry.Description,
            SelectedAt = DateTimeOffset.UtcNow,
            IsActive = true
        }).ConfigureAwait(true);
        await LoadAsync(SelectedBasket?.Key).ConfigureAwait(true);
        await _main.RefreshQuotationItemAsync(_projectId, _lineId).ConfigureAwait(true);
    }

    public async Task RemoveCatalogSelectionAsync()
    {
        await _quotations.SetLineCatalogSelectionAsync(_lineId, null).ConfigureAwait(true);
        await LoadAsync(SelectedBasket?.Key).ConfigureAwait(true);
        await _main.RefreshQuotationItemAsync(_projectId, _lineId).ConfigureAwait(true);
    }

    private async Task RefreshCatalogAvailabilityAsync()
    {
        var states = await _catalogRepository.GetSyncStatesAsync().ConfigureAwait(true);
        IsCatalogAvailable = states.Count == 2 && states.All(state =>
            state.Status == CatalogSyncStatus.Complete && state.ActiveRecords > 0);
        if (!IsCatalogAvailable)
        {
            CatalogStatus = "Primeira carga em andamento ou ausente. O último catálogo completo continuará disponível nas atualizações futuras.";
            return;
        }

        CatalogStatus = "Catálogo local completo. Pesquise livremente por nome, atributo, medida ou código.";
        if (!_catalogHierarchyLoaded)
        {
            await LoadCatalogHierarchyAsync().ConfigureAwait(true);
        }
    }

    private async Task LoadCatalogHierarchyAsync()
    {
        var paths = await _catalogRepository.GetHierarchyAsync().ConfigureAwait(true);
        CatalogHierarchy.Clear();
        foreach (var kind in new[] { CatalogKind.Catmat, CatalogKind.Catser })
        {
            var root = new CatalogHierarchyNode
            {
                Label = kind == CatalogKind.Catmat ? "CATMAT — materiais" : "CATSER — serviços",
                Kind = kind
            };
            foreach (var path in paths.Where(path => path.Kind == kind))
            {
                AddHierarchyPath(root, path);
            }

            CatalogHierarchy.Add(root);
        }

        _catalogHierarchyLoaded = true;
    }

    private static void AddHierarchyPath(CatalogHierarchyNode root, CatalogHierarchyPath path)
    {
        var levels = new[]
        {
            (path.Level1Code, path.Level1Name),
            (path.Level2Code, path.Level2Name),
            (path.Level3Code, path.Level3Name),
            (path.Level4Code, path.Level4Name),
            (path.Level5Code, path.Level5Name)
        };
        var codes = new string[5];
        var current = root;
        for (var index = 0; index < levels.Length; index++)
        {
            var (code, name) = levels[index];
            if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(name)) break;
            codes[index] = code;
            var child = current.Children.FirstOrDefault(node =>
                string.Equals(node.Filter.GetType().GetProperty($"Level{index + 1}Code")?.GetValue(node.Filter)?.ToString(), code, StringComparison.Ordinal));
            if (child is null)
            {
                child = new CatalogHierarchyNode
                {
                    Label = string.IsNullOrWhiteSpace(code) ? name : $"{code} — {name}",
                    Kind = path.Kind,
                    Filter = new CatalogHierarchyFilter(codes[0], codes[1], codes[2], codes[3], codes[4])
                };
                current.Children.Add(child);
            }

            current = child;
        }
    }

    public async Task LoadPromptSlotAsync(ItemSearchPromptSlot slot)
    {
        if (Line is null)
        {
            return;
        }

        if (_workspace is not null && !SearchDefinitionChanged)
        {
            await _itemSearch.SavePreferencesAsync(BuildWorkspace()).ConfigureAwait(true);
        }

        SelectedPromptSlot = slot;
        var seed = CreateSeedWorkspace(slot);
        var state = await _itemSearch.LoadAsync(seed).ConfigureAwait(true);
        ApplyWorkspace(state);
    }

    public void InsertSelectedSweetCode()
    {
        if (string.IsNullOrWhiteSpace(SelectedSweetCode))
        {
            return;
        }

        SearchText = string.IsNullOrWhiteSpace(SearchText)
            ? SelectedSweetCode
            : $"{SearchText.Trim()} {SelectedSweetCode.Trim()}";
    }

    public async Task RunSearchAsync(bool restart)
    {
        if (IsSearchBusy)
        {
            return;
        }

        var workspace = BuildWorkspace();
        _ = SearchParser.Parse(workspace.SearchText);
        IsSearchBusy = true;
        SearchProgress = 0;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        try
        {
            var local = await _itemSearch.GetLocalSummaryAsync(
                    workspace,
                    _searchCancellation.Token)
                .ConfigureAwait(true);
            SearchSummary =
                $"{local.CandidateContracts:N0} contratação(ões) candidatas; " +
                $"{local.CachedMatchingItems:N0} item(ns) parcial(is) no cache; " +
                $"{local.CachedItemsWithActivePrices:N0} com preço parcial no cache. " +
                $"Iniciando {workspace.BatchCount:N0} lote(s).";
            var progress = new Progress<QuotationItemSearchProgress>(value =>
            {
                SearchProgress = value.Percentage;
                SearchSummary =
                    $"{value.Message} Nesta ação: {value.ProcessedContracts:N0}/{value.RequestedContracts:N0}; " +
                    $"total: {value.ContractsExamined:N0} contratos, {value.MatchedItems:N0} itens, " +
                    $"{value.RevealedPrices:N0} preços; listas cache/API " +
                    $"{value.ItemListsFromCache:N0}/{value.ItemListsFromApi:N0}; " +
                    $"resultados API {value.ItemResultApiCalls:N0}; falhas {value.FailedCalls:N0}.";
            });
            var rowProgress = new Progress<IReadOnlyList<ItemSearchRow>>(AppendSearchRows);
            var state = await _itemSearch.RunAsync(
                    workspace,
                    restart,
                    progress,
                    rowProgress,
                    _searchCancellation.Token)
                .ConfigureAwait(true);
            ApplyWorkspace(state);
            SearchProgress = 100;
        }
        catch (OperationCanceledException)
        {
            SearchSummary = "Pesquisa interrompida; o checkpoint do último contrato concluído foi preservado.";
            var state = await _itemSearch.LoadAsync(workspace, CancellationToken.None).ConfigureAwait(true);
            ApplyWorkspace(state);
        }
        finally
        {
            IsSearchBusy = false;
        }
    }

    public async Task ApplyPriceFilterAsync()
    {
        var workspace = BuildWorkspace();
        if (SearchDefinitionChanged && workspace.Checkpoint.ContractsExamined > 0)
        {
            throw new InvalidOperationException(
                "Os critérios principais foram alterados. Use Pesquisar para confirmar o reinício deste prompt.");
        }

        await _itemSearch.SavePreferencesAsync(workspace).ConfigureAwait(true);
        ApplyWorkspace(await _itemSearch.LoadAsync(workspace).ConfigureAwait(true));
        SearchSummary = $"Faixa aplicada: {SearchRows.Count:N0} linha(s) visível(is).";
    }

    public void StopSearch() => _searchCancellation?.Cancel();

    public async Task AddSearchRowsAsync(IReadOnlyList<ItemSearchDisplayRow> rows)
    {
        if (Line is null || rows.Count == 0)
        {
            return;
        }

        var valid = rows
            .Where(row =>
                row.Source.PriceState == ItemSearchPriceState.Homologated &&
                row.Source.Result is { IsActive: true, HomologatedUnitValue: > 0 })
            .Select(row => row.Source)
            .ToArray();
        if (valid.Length == 0)
        {
            throw new InvalidOperationException("Selecione preços homologados ativos e positivos.");
        }

        var basket = await EnsureManualBasketAsync(copyAutomatic: true).ConfigureAwait(true);
        var line = Line.Line;
        var saved = await _quotations.SaveManualBasketAsync(
            _projectId,
            line.Id,
            new QuotationLineInput(
                line.Description,
                line.RequestedQuantity,
                line.RequestedUnit,
                line.MinimumUnitPrice,
                line.MaximumUnitPrice)
            {
                Weights = line.Weights,
                RequestedBasketSize = line.RequestedBasketSize
            },
            basket?.Id,
            basket?.Name ?? NextManualName(),
            valid).ConfigureAwait(true);
        await LoadAsync(saved.Basket.Key).ConfigureAwait(true);
    }

    public async Task SetReferenceMembershipAsync(
        QuotationPriceDisplayRow row,
        bool include)
    {
        if (Line is null)
        {
            return;
        }

        if (!include)
        {
            SelectedPrice = row;
            await RemoveSelectedReferenceAsync().ConfigureAwait(true);
            return;
        }

        QuotationManualBasket basket;
        if (SelectedBasket?.Source is { IsManual: true, ManualBasketId: not null } manual)
        {
            basket = await _quotations.AddManualBasketReferenceAsync(
                    _lineId,
                    manual.ManualBasketId.Value,
                    row.Id)
                .ConfigureAwait(true);
        }
        else if (SelectedBasket is not null)
        {
            basket = await _quotations.CreateManualCopyAsync(
                    Line.Analysis,
                    SelectedBasket.Source)
                .ConfigureAwait(true);
            basket = await _quotations.AddManualBasketReferenceAsync(
                    _lineId,
                    basket.Id,
                    row.Id)
                .ConfigureAwait(true);
        }
        else
        {
            basket = await _quotations.CreateManualBasketAsync(
                    _lineId,
                    NextManualName(),
                    [row.Id])
                .ConfigureAwait(true);
        }

        await LoadAsync($"manual:{basket.Id:N}").ConfigureAwait(true);
    }

    public async Task RemoveSelectedReferenceAsync()
    {
        if (Line is null || SelectedBasket is null || SelectedPrice is null)
        {
            return;
        }

        if (SelectedBasket.Source.IsManual &&
            SelectedBasket.Source.ManualBasketId is { } basketId)
        {
            await _quotations.RemoveManualBasketReferenceAsync(
                basketId,
                SelectedPrice.Id).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
            return;
        }

        var copied = await _quotations.CreateManualCopyAsync(
            Line.Analysis,
            SelectedBasket.Source,
            excludedReferenceId: SelectedPrice.Id).ConfigureAwait(true);
        await LoadAsync($"manual:{copied.Id:N}").ConfigureAwait(true);
    }

    public async Task ConfirmSelectedBasketAsync()
    {
        if (Line is null || SelectedBasket is null)
        {
            return;
        }

        await _quotations.ConfirmBasketAsync(
            Line.Analysis,
            SelectedBasket.Key).ConfigureAwait(true);
        await LoadAsync(SelectedBasket.Key).ConfigureAwait(true);
    }

    public async Task<QuotationManualBasket?> EnsureManualBasketAsync(bool copyAutomatic)
    {
        if (Line is null)
        {
            return null;
        }

        if (SelectedBasket?.Source is { IsManual: true, ManualBasketId: not null } manual)
        {
            return await _quotations.CreateManualCopyAsync(
                Line.Analysis,
                manual).ConfigureAwait(true);
        }

        if (copyAutomatic && SelectedBasket is not null)
        {
            var copied = await _quotations.CreateManualCopyAsync(
                Line.Analysis,
                SelectedBasket.Source).ConfigureAwait(true);
            await LoadAsync($"manual:{copied.Id:N}").ConfigureAwait(true);
            return copied;
        }

        return null;
    }

    public async Task SaveInternetDraftAsync(InternetPriceDraft draft)
    {
        await _internetPrices.SaveDraftAsync(draft).ConfigureAwait(true);
        await LoadAsync(SelectedBasket?.Key).ConfigureAwait(true);
    }

    public async Task CompleteInternetDraftAsync(
        InternetPriceDraft draft,
        Guid? basketId,
        string basketName)
    {
        var result = await _internetPrices.CompleteDraftAsync(
            _projectId,
            draft,
            basketId,
            basketName).ConfigureAwait(true);
        await LoadAsync(result.Basket.Key).ConfigureAwait(true);
    }

    public async Task DeleteSelectedDraftAsync()
    {
        if (SelectedDraft is null)
        {
            return;
        }

        await _internetPrices.DeleteDraftAsync(SelectedDraft.Id).ConfigureAwait(true);
        await LoadAsync(SelectedBasket?.Key).ConfigureAwait(true);
    }

    public async Task DeleteSelectedInternetReferenceAsync()
    {
        if (SelectedPrice?.Source.Source != QuotationReferenceSource.InternetIncisoIII)
        {
            return;
        }

        await _internetPrices.DeleteInternetReferenceAsync(
            _lineId,
            SelectedPrice.Id).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }

    public async Task<InternetPriceEvidence> GetSelectedInternetEvidenceAsync()
    {
        if (SelectedPrice?.Source.Source != QuotationReferenceSource.InternetIncisoIII)
        {
            throw new InvalidOperationException("Selecione um preço da internet.");
        }

        var evidence = await _internetPrices.GetEvidenceAsync(_lineId).ConfigureAwait(true);
        return evidence.TryGetValue(SelectedPrice.Id, out var stored)
            ? stored
            : throw new InvalidDataException("Os prints desta referência não foram encontrados.");
    }

    public void OpenSource(QuotationPriceDisplayRow? price = null, ItemSearchDisplayRow? search = null)
    {
        var url = price?.PortalUrl ?? search?.PortalUrl;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    public void AccessFullDocuments(
        QuotationPriceDisplayRow? price = null,
        ItemSearchDisplayRow? search = null)
    {
        if (search is not null)
        {
            _main.AccessItemDocumentsCommand.Execute(search);
        }
        else if (price is not null)
        {
            _main.AccessQuotationDocumentsCommand.Execute(
                new QuotationReferenceDisplay(price.Source));
        }
    }

    public async Task PreparePriceDocumentsAsync(QuotationPriceDisplayRow row)
    {
        SelectedPrice = row;
        await PrepareSelectedDocumentsAsync().ConfigureAwait(true);
    }

    public async Task PrepareReferenceDocumentsAsync(string referenceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceId);
        if (Line is null)
        {
            await LoadAsync().ConfigureAwait(true);
        }

        var reference = References.FirstOrDefault(value =>
            string.Equals(value.Id, referenceId, StringComparison.Ordinal));
        if (reference is null)
        {
            throw new InvalidOperationException(
                "A referência selecionada não está mais disponível neste item.");
        }

        SelectedReference = reference;
        SelectedPrice = VisibleReferences.FirstOrDefault(value =>
            string.Equals(value.Id, referenceId, StringComparison.Ordinal));
        await PrepareSelectedDocumentsAsync().ConfigureAwait(true);
    }

    public async Task PrepareSearchDocumentsAsync(ItemSearchDisplayRow row)
    {
        if (Line is null)
        {
            return;
        }

        SelectedSearchRow = row;
        var expressions = BuildExpressions(
            Line.Line,
            row.Item.Description,
            row.Source.MatchedSearchText);
        await PreparePncpDocumentsAsync(
                PncpContractKey.FromContract(row.Contract),
                row.Item.ItemNumber,
                expressions)
            .ConfigureAwait(true);
    }

    public async Task PrepareSelectedDocumentsAsync()
    {
        var reference = SelectedReference;
        if (reference is null || Line is null)
        {
            return;
        }

        if (reference.Source.Source == QuotationReferenceSource.InternetIncisoIII)
        {
            IsBusy = true;
            DocumentThumbnails.Clear();
            RelevantPdfPath = null;
            try
            {
                var evidence = await _internetPrices.GetEvidenceAsync(_lineId).ConfigureAwait(true);
                if (!evidence.TryGetValue(reference.Id, out var stored))
                {
                    throw new InvalidDataException("Os prints desta referência não foram encontrados.");
                }

                DocumentThumbnails.Add(new DocumentThumbnailDisplay(
                    "Print do preço",
                    CreateBitmap(await _evidenceStore.ReadVerifiedAsync(stored.PriceImage).ConfigureAwait(true))));
                DocumentThumbnails.Add(new DocumentThumbnailDisplay(
                    "Print do CNPJ",
                    CreateBitmap(await _evidenceStore.ReadVerifiedAsync(stored.TaxIdImage).ConfigureAwait(true))));
                DocumentSummary = "Referência do Inciso III: dois prints íntegros disponíveis.";
            }
            finally
            {
                IsBusy = false;
            }

            return;
        }

        if (!PncpContractKey.TryParse(
                reference.Source.ContractId,
                reference.Source.PortalUrl,
                out var contract) ||
            contract is null)
        {
            throw new InvalidOperationException("A contratação PNCP não pôde ser identificada.");
        }

        await PreparePncpDocumentsAsync(
                contract,
                reference.Source.ItemNumber,
                BuildExpressions(
                    Line.Line,
                    reference.Source.ItemDescription,
                    reference.Source.MatchedSearchText))
            .ConfigureAwait(true);
    }

    public void OpenRelevantPdf()
    {
        if (CanOpenRelevantPdf)
        {
            Process.Start(new ProcessStartInfo(RelevantPdfPath!) { UseShellExecute = true });
        }
    }

    public void PauseAutomation() => _main.CancelQuotationAutomationCommand.Execute(null);
    public void ResumeAutomation() => _main.ResumeQuotationAutomationCommand.Execute(null);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _main.TimedQuotationProgressChanged -= OnTimedProgress;
        _searchCancellation?.Cancel();
        if (_workspace is not null)
        {
            try
            {
                var value = SearchDefinitionChanged && _workspace.Checkpoint.ContractsExamined > 0
                    ? _workspace with
                    {
                        MinimumUnitPrice = ParseOptionalDecimal(MinimumPriceText, "preço mínimo"),
                        MaximumUnitPrice = ParseOptionalDecimal(MaximumPriceText, "preço máximo"),
                        BatchCount = BatchCount
                    }
                    : BuildWorkspace();
                await _itemSearch.SavePreferencesAsync(value).ConfigureAwait(false);
            }
            catch
            {
                // O checkpoint de cada contrato já foi persistido. Falha ao salvar
                // apenas preferências visuais não deve impedir o fechamento.
            }
        }

        _searchCancellation?.Dispose();
    }

    private async Task PreparePncpDocumentsAsync(
        PncpContractKey contract,
        long itemNumber,
        IReadOnlyList<string> expressions)
    {
        IsBusy = true;
        DocumentThumbnails.Clear();
        RelevantPdfPath = null;
        try
        {
            var previewFolder = Path.Combine(_dataFolder, "document-preview");
            Directory.CreateDirectory(previewFolder);
            var destination = Path.Combine(
                previewFolder,
                $"{contract.PncpId.Replace('/', '_')}_{itemNumber}_relevantes.pdf");
            var progress = new Progress<DocumentProcessingProgress>(value =>
                DocumentSummary = value.Message);
            var result = await _relevantPages.CreateAsync(
                contract,
                expressions,
                destination,
                progress).ConfigureAwait(true);
            RelevantPdfPath = result.OutputPath;
            DocumentSummary =
                $"{result.MatchedPageCount:N0} página(s) relevante(s), " +
                $"{result.OccurrenceCount:N0} ocorrência(s), " +
                $"{result.Warnings.Count:N0} aviso(s).";
            if (result.OutputPath is null)
            {
                return;
            }

            var thumbnails = Math.Min(result.MatchedPageCount, 24);
            for (var page = 1; page <= thumbnails; page++)
            {
                var rendered = await _rasterizer.RenderAsync(
                    result.OutputPath,
                    page,
                    dpi: 110).ConfigureAwait(true);
                DocumentThumbnails.Add(new DocumentThumbnailDisplay(
                    $"Página relevante {page:N0}",
                    CreateBitmap(rendered.PngBytes)));
            }

            if (result.MatchedPageCount > thumbnails)
            {
                DocumentSummary += $" Miniaturas: primeiras {thumbnails:N0} páginas; abra o PDF para ver todas.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnTimedProgress(TimedQuotationProgress value)
    {
        UpdateProgress(value);
        if (value.UpdatedLineId == _lineId &&
            DateTimeOffset.UtcNow - _lastRefresh >= TimeSpan.FromMilliseconds(350))
        {
            _ = LoadAsync(SelectedBasket?.Key);
        }
    }

    private void UpdateProgress(TimedQuotationProgress? value)
    {
        if (value is null)
        {
            ProgressText = Line is null
                ? "Nenhuma atualização de automação recebida nesta sessão."
                : $"Automação: {Line.AutomationStatus} · {Line.AutomationMessage}";
            return;
        }

        ProgressText =
            $"Tempo {value.ActiveElapsed:hh\\:mm\\:ss} · restante {value.Remaining:hh\\:mm\\:ss} · " +
            $"lote {value.BatchNumber:N0}, contrato {value.ContractInBatch:N0}/{value.ContractsInBatch:N0} · " +
            $"{value.UniqueContractsProcessed:N0} contratos únicos · listas cache/API " +
            $"{value.ItemListsFromCache:N0}/{value.ItemListsFromApi:N0} · " +
            $"{value.MatchedItems:N0} correspondências · {value.RevealedPrices:N0} preços · " +
            $"resolvidos {value.ResolvedItems:N0} · falhas {value.FailedCalls:N0} · " +
            $"prompt global: {value.CurrentContractPrompt}";
    }

    private void RebuildVisibleReferences()
    {
        if (Line is null)
        {
            return;
        }

        var selectedIds = SelectedBasket?.Source.References
            .Select(reference => reference.Id)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var selectedId = SelectedPrice?.Id;
        VisibleReferences.Clear();
        foreach (var reference in Line.Analysis.References)
        {
            var inBasket = selectedIds.Contains(reference.Id);
            var visible = ReferenceScope switch
            {
                ReferenceViewScope.InBasket => inBasket,
                ReferenceViewScope.EligibleOutsideBasket =>
                    !inBasket && reference.State == QuotationReferenceState.Eligible,
                ReferenceViewScope.RejectedOrDuplicate =>
                    reference.State != QuotationReferenceState.Eligible,
                _ => true
            };
            if (visible)
            {
                VisibleReferences.Add(new QuotationPriceDisplayRow(reference, inBasket));
            }
        }

        SelectedPrice = VisibleReferences.FirstOrDefault(row => row.Id == selectedId) ??
                        VisibleReferences.FirstOrDefault();
        OnPropertyChanged(nameof(ReferenceScopeSummary));
    }

    private QuotationItemSearchWorkspace CreateSeedWorkspace(ItemSearchPromptSlot slot)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return new QuotationItemSearchWorkspace
        {
            LineId = _lineId,
            Slot = slot,
            SearchText = GetPromptText(slot),
            GeoFilter = SearchGeoFilter.All,
            StartDate = today.AddDays(-364),
            EndDate = today,
            Sort = SearchSort.Nearest,
            BatchCount = ItemSearchDefaults.InitialBatchCount
        };
    }

    private string GetPromptText(ItemSearchPromptSlot slot) => slot switch
    {
        ItemSearchPromptSlot.Restrictive =>
            Line?.Line.PromptSet?.RestrictiveText ?? Line?.Line.SearchText ?? string.Empty,
        ItemSearchPromptSlot.Intermediate =>
            Line?.Line.PromptSet?.IntermediateText ?? string.Empty,
        ItemSearchPromptSlot.Broad =>
            Line?.Line.PromptSet?.BroadText ?? string.Empty,
        _ => string.Empty
    };

    private QuotationItemSearchWorkspace BuildWorkspace()
    {
        var (start, end) = ResolveDateRange();
        var (minimum, maximum) = ParsePriceRange();
        return (_workspace ?? CreateSeedWorkspace(SelectedPromptSlot)) with
        {
            LineId = _lineId,
            Slot = SelectedPromptSlot,
            SearchText = SearchText.Trim(),
            GeoFilter = SelectedGeoFilter,
            StartDate = start,
            EndDate = end,
            Sort = SelectedSortOption.Value,
            MinimumUnitPrice = minimum,
            MaximumUnitPrice = maximum,
            BatchCount = BatchCount,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private void ApplyWorkspace(QuotationItemSearchState state)
    {
        _workspace = state.Workspace;
        SearchText = state.Workspace.SearchText;
        SelectedGeoFilter = GeoFilters.FirstOrDefault(value =>
                                value.Kind == state.Workspace.GeoFilter.Kind &&
                                string.Equals(
                                    value.Uf,
                                    state.Workspace.GeoFilter.Uf,
                                    StringComparison.OrdinalIgnoreCase)) ??
                            SearchGeoFilter.All;
        SelectedSortOption = SortOptions.First(value => value.Value == state.Workspace.Sort);
        ApplyDateRange(state.Workspace.StartDate, state.Workspace.EndDate);
        MinimumPriceText = state.Workspace.MinimumUnitPrice?.ToString("N4") ?? string.Empty;
        MaximumPriceText = state.Workspace.MaximumUnitPrice?.ToString("N4") ?? string.Empty;
        BatchCount = state.Workspace.BatchCount;
        SearchRows.Clear();
        AppendSearchRows(state.Rows);
        SearchSummary = state.Workspace.Checkpoint.ContractsExamined == 0
            ? "Pesquisa ainda não iniciada neste prompt."
            : $"{state.Workspace.StatusMessage} Retomada disponível após " +
              $"{state.Workspace.Checkpoint.ContractsExamined:N0} contratação(ões); " +
              $"{SearchRows.Count:N0} linha(s) visível(is).";
    }

    private void AppendSearchRows(IEnumerable<ItemSearchRow> rows)
    {
        var keys = SearchRows.Select(RowKey).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows.Select(value => new ItemSearchDisplayRow(value)))
        {
            if (keys.Add(RowKey(row)))
            {
                SearchRows.Add(row);
            }
        }
    }

    private static string RowKey(ItemSearchDisplayRow row) =>
        $"{row.Contract.PncpId}|{row.Item.ItemNumber}|{row.Result?.ResultSequence ?? 0}";

    private static bool HasSameDefinition(
        QuotationItemSearchWorkspace left,
        QuotationItemSearchWorkspace right) =>
        string.Equals(
            SearchParser.Normalize(left.SearchText),
            SearchParser.Normalize(right.SearchText),
            StringComparison.Ordinal) &&
        left.GeoFilter.Kind == right.GeoFilter.Kind &&
        string.Equals(left.GeoFilter.Uf, right.GeoFilter.Uf, StringComparison.OrdinalIgnoreCase) &&
        left.StartDate == right.StartDate &&
        left.EndDate == right.EndDate &&
        left.Sort == right.Sort;

    private void ApplyDateRange(DateOnly start, DateOnly end)
    {
        CustomStartDate = start.ToDateTime(TimeOnly.MinValue);
        CustomEndDate = end.ToDateTime(TimeOnly.MinValue);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var days = end == today ? end.DayNumber - start.DayNumber + 1 : -1;
        SelectedDateRange = DateRanges.FirstOrDefault(value => value.Days == days) ??
                            DateRanges.Single(value => value.IsCustom);
    }

    private (DateOnly Start, DateOnly End) ResolveDateRange()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (!SelectedDateRange.IsCustom)
        {
            var days = SelectedDateRange.Days ?? 365;
            return (today.AddDays(-(days - 1)), today);
        }

        if (CustomStartDate is null || CustomEndDate is null)
        {
            throw new ArgumentException("Informe as duas datas do período personalizado.");
        }

        var start = DateOnly.FromDateTime(CustomStartDate.Value.Date);
        var end = DateOnly.FromDateTime(CustomEndDate.Value.Date);
        if (start > end)
        {
            throw new ArgumentException("A data inicial deve ser anterior ou igual à data final.");
        }

        return (start, end);
    }

    private (decimal? Minimum, decimal? Maximum) ParsePriceRange()
    {
        var minimum = ParseOptionalDecimal(MinimumPriceText, "preço mínimo");
        var maximum = ParseOptionalDecimal(MaximumPriceText, "preço máximo");
        if (minimum < 0 || maximum < 0)
        {
            throw new ArgumentException("A faixa de preço não aceita valores negativos.");
        }

        if (minimum is not null && maximum is not null && minimum > maximum)
        {
            throw new ArgumentException("O preço mínimo deve ser menor ou igual ao preço máximo.");
        }

        return (minimum, maximum);
    }

    private static decimal? ParseOptionalDecimal(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) ||
            decimal.TryParse(
                text.Replace(',', '.'),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out value))
        {
            return value;
        }

        throw new ArgumentException($"Informe um {label} válido.");
    }

    private void RefreshSweetCodeSuggestions()
    {
        SelectedSweetCode = SweetCodeEnabled
            ? SweetCodeExpressions.FirstOrDefault(expression =>
                SearchParser.Normalize(expression).Contains(
                    SearchParser.Normalize(SearchText),
                    StringComparison.Ordinal))
            : null;
    }

    private string NextManualName()
    {
        var names = Baskets.Where(item => item.Source.IsManual)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var number = 1; ; number++)
        {
            var value = $"Manual {number:N0}";
            if (!names.Contains(value))
            {
                return value;
            }
        }
    }

    private static IReadOnlyList<string> BuildExpressions(
        QuotationLine line,
        string itemDescription,
        string matchedSearchText)
    {
        var values = new[]
        {
            line.Description,
            itemDescription,
            line.PromptSet?.RestrictiveText,
            line.PromptSet?.IntermediateText,
            line.PromptSet?.BroadText,
            matchedSearchText
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
        var stopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "para", "com", "sem", "tipo", "modelo", "unidade", "pacote", "caixa",
            "produto", "material", "conforme", "mínimo", "maximo"
        };
        var basicTerms = values
            .SelectMany(value =>
            {
                try
                {
                    return SearchParser.Parse(value).PositiveText
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                }
                catch (FormatException)
                {
                    return value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                }
            })
            .Select(SearchParser.Normalize)
            .Where(term => term.Length >= 4 && !stopWords.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .Take(6);
        values.AddRange(basicTerms);
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<SearchGeoFilter> BuildGeoFilters()
    {
        var filters = new List<SearchGeoFilter>
        {
            SearchGeoFilter.All,
            SearchGeoFilter.NearRibeirao,
            SearchGeoFilter.Southeast
        };
        filters.AddRange(new[]
        {
            "AC", "AL", "AP", "AM", "BA", "CE", "DF", "ES", "GO", "MA", "MT", "MS",
            "MG", "PA", "PB", "PR", "PE", "PI", "RJ", "RN", "RS", "RO", "RR", "SC",
            "SP", "SE", "TO"
        }.Select(SearchGeoFilter.State));
        return filters;
    }

    private static BitmapImage CreateBitmap(ReadOnlyMemory<byte> bytes)
    {
        var image = new BitmapImage();
        using var stream = new MemoryStream(bytes.ToArray());
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 720;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

public sealed record DocumentThumbnailDisplay(string Caption, BitmapImage Image);
