using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PNCPKing.App.Views;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.ViewModels;

public sealed partial class MainViewModel
{
    private const int QuotationBasketPageSize = 100;
    private QuotationService _quotationService = null!;
    private IQuotationWorkbookService _quotationWorkbookService = null!;
    private IQuotationWorkbookImportService _quotationWorkbookImportService = null!;
    private IQuotationPackageService _quotationPackageService = null!;
    private CancellationTokenSource? _quotationAutomationCancellation;
    private TaskCompletionSource<bool>? _quotationAutomationCompletion;
    private QuotationProjectDisplay? _selectedQuotationProject;
    private QuotationLineDisplay? _selectedQuotationLine;
    private QuotationBasketDisplay? _selectedQuotationBasket;
    private QuotationReferenceDisplay? _selectedBasketReference;
    private QuotationPriceDisplayRow? _selectedVisibleQuotationReference;
    private ReferenceViewScope _quotationReferenceScope = ReferenceViewScope.InBasket;
    private int _quotationBasketPage = 1;
    private string _quotationSummary = "Nenhum projeto de cotação selecionado.";
    private QuotationItemWindow? _quotationItemWindow;
    private TimedQuotationProgress? _latestTimedQuotationProgress;

    public event Action<TimedQuotationProgress>? TimedQuotationProgressChanged;

    public TimedQuotationProgress? LatestTimedQuotationProgress
    {
        get => _latestTimedQuotationProgress;
        private set => SetProperty(ref _latestTimedQuotationProgress, value);
    }

    public bool IsQuotationAutomationRunning => _quotationAutomationCancellation is not null;

    public RangeObservableCollection<QuotationProjectDisplay> QuotationProjects { get; } = [];
    public RangeObservableCollection<QuotationLineDisplay> QuotationLines { get; } = [];
    public RangeObservableCollection<QuotationBasketDisplay> QuotationBaskets { get; } = [];
    public RangeObservableCollection<QuotationReferenceDisplay> QuotationReferences { get; } = [];
    public RangeObservableCollection<QuotationReferenceDisplay> SelectedBasketReferences { get; } = [];
    public RangeObservableCollection<QuotationPriceDisplayRow> VisibleQuotationReferences { get; } = [];

    public ICommand UseQuotationSampleCommand { get; private set; } = null!;
    public ICommand UpdateQuotationSampleCommand { get; private set; } = null!;
    public ICommand AdjustQuotationWeightsCommand { get; private set; } = null!;
    public ICommand ConfirmQuotationBasketCommand { get; private set; } = null!;
    public ICommand ExportQuotationCommand { get; private set; } = null!;
    public ICommand ExportQuotationPackageCommand { get; private set; } = null!;
    public ICommand ImportQuotationPackageCommand { get; private set; } = null!;
    public ICommand PreviousQuotationBasketPageCommand { get; private set; } = null!;
    public ICommand NextQuotationBasketPageCommand { get; private set; } = null!;
    public ICommand OpenQuotationReferenceCommand { get; private set; } = null!;
    public ICommand AccessQuotationDocumentsCommand { get; private set; } = null!;
    public ICommand NewQuotationCommand { get; private set; } = null!;
    public ICommand RenameQuotationCommand { get; private set; } = null!;
    public ICommand DeleteQuotationCommand { get; private set; } = null!;
    public ICommand DeleteQuotationLineCommand { get; private set; } = null!;
    public ICommand RenameQuotationLineCommand { get; private set; } = null!;
    public ICommand ImportQuotationCommand { get; private set; } = null!;
    public ICommand AiQuotationCommand { get; private set; } = null!;
    public ICommand ResumeQuotationAutomationCommand { get; private set; } = null!;
    public ICommand CancelQuotationAutomationCommand { get; private set; } = null!;
    public ICommand RefineQuotationPromptsCommand { get; private set; } = null!;
    public ICommand OpenRestrictiveQuotationSearchCommand { get; private set; } = null!;
    public ICommand OpenIntermediateQuotationSearchCommand { get; private set; } = null!;
    public ICommand OpenBroadQuotationSearchCommand { get; private set; } = null!;
    public ICommand RenameManualBasketCommand { get; private set; } = null!;
    public ICommand DeleteManualBasketCommand { get; private set; } = null!;
    public ICommand RemoveManualBasketReferenceCommand { get; private set; } = null!;
    public ICommand OpenQuotationItemCommand { get; private set; } = null!;

    public QuotationProjectDisplay? SelectedQuotationProject
    {
        get => _selectedQuotationProject;
        set
        {
            if (SetProperty(ref _selectedQuotationProject, value))
            {
                _ = LoadQuotationProjectAsync(value?.Id);
                NotifyCommands();
            }
        }
    }

    public QuotationLineDisplay? SelectedQuotationLine
    {
        get => _selectedQuotationLine;
        set
        {
            if (SetProperty(ref _selectedQuotationLine, value))
            {
                QuotationBasketPage = 1;
                BindSelectedQuotationLine();
                NotifyCommands();
            }
        }
    }

    public QuotationBasketDisplay? SelectedQuotationBasket
    {
        get => _selectedQuotationBasket;
        set
        {
            if (SetProperty(ref _selectedQuotationBasket, value))
            {
                SelectedBasketReferences.Clear();
                SelectedBasketReference = null;
                if (value is not null)
                {
                    foreach (var reference in value.Source.References)
                    {
                        SelectedBasketReferences.Add(new QuotationReferenceDisplay(reference));
                    }
                }

                RebuildVisibleQuotationReferences();
                NotifyCommands();
            }
        }
    }

    public QuotationPriceDisplayRow? SelectedVisibleQuotationReference
    {
        get => _selectedVisibleQuotationReference;
        set => SetProperty(ref _selectedVisibleQuotationReference, value);
    }

    public ReferenceViewScope QuotationReferenceScope
    {
        get => _quotationReferenceScope;
        set
        {
            if (SetProperty(ref _quotationReferenceScope, value))
            {
                RebuildVisibleQuotationReferences();
                OnPropertyChanged(nameof(QuotationReferenceScopeSummary));
            }
        }
    }

    public string QuotationReferenceScopeSummary => QuotationReferenceScope switch
    {
        ReferenceViewScope.InBasket => "Na cesta",
        ReferenceViewScope.EligibleOutsideBasket => "Elegíveis fora",
        ReferenceViewScope.RejectedOrDuplicate => "Descartados/duplicados",
        _ => "Todos"
    };

    public QuotationReferenceDisplay? SelectedBasketReference
    {
        get => _selectedBasketReference;
        set
        {
            if (SetProperty(ref _selectedBasketReference, value))
            {
                NotifyCommands();
            }
        }
    }

    public int QuotationBasketPage
    {
        get => _quotationBasketPage;
        private set
        {
            if (SetProperty(ref _quotationBasketPage, Math.Max(1, value)))
            {
                OnPropertyChanged(nameof(QuotationBasketPageSummary));
            }
        }
    }

    public string QuotationBasketPageSummary
    {
        get
        {
            var total = SelectedQuotationLine?.BasketCount ?? 0;
            var pages = Math.Max(1, (int)Math.Ceiling(total / (double)QuotationBasketPageSize));
            var pool = SelectedQuotationLine?.BasketPoolCount ?? 0;
            var eligible = SelectedQuotationLine?.EligibleCount ?? 0;
            return $"Página {QuotationBasketPage:N0} de {pages:N0} — {total:N0} cesta(s) automática(s)/manual(is); " +
                   $"conjunto auditável: {pool:N0} de {eligible:N0} referência(s) elegível(is)";
        }
    }

    public string QuotationSummary
    {
        get => _quotationSummary;
        private set => SetProperty(ref _quotationSummary, value);
    }

    private void InitializeQuotation(
        QuotationService quotationService,
        IQuotationWorkbookService quotationWorkbookService,
        IQuotationWorkbookImportService quotationWorkbookImportService,
        IQuotationPackageService quotationPackageService)
    {
        _quotationService = quotationService;
        _quotationWorkbookService = quotationWorkbookService;
        _quotationWorkbookImportService = quotationWorkbookImportService;
        _quotationPackageService = quotationPackageService;
        UseQuotationSampleCommand = new AsyncRelayCommand(
            UseCurrentSampleAsync,
            () => !IsFileBusy && !IsPriceBusy && _itemSearchService.CurrentSession is not null);
        UpdateQuotationSampleCommand = new AsyncRelayCommand(
            UpdateCurrentSampleAsync,
            () => !IsFileBusy && !IsPriceBusy && SelectedQuotationLine is not null &&
                  SelectedQuotationProject is not null && _itemSearchService.CurrentSession is not null);
        AdjustQuotationWeightsCommand = new AsyncRelayCommand(
            AdjustQuotationWeightsAsync,
            () => !IsFileBusy && SelectedQuotationLine is not null && SelectedQuotationProject is not null);
        ConfirmQuotationBasketCommand = new AsyncRelayCommand(
            ConfirmSelectedBasketAsync,
            () => !IsFileBusy && SelectedQuotationLine is not null && SelectedQuotationBasket is not null);
        ExportQuotationCommand = new AsyncRelayCommand(
            ExportQuotationAsync,
            () => !IsFileBusy && !IsDocumentBusy &&
                  SelectedQuotationProject is not null && QuotationLines.Count > 0);
        ExportQuotationPackageCommand = new AsyncRelayCommand(
            ExportQuotationPackageAsync,
            () => !IsFileBusy && !IsPriceBusy && !IsDocumentBusy &&
                  SelectedQuotationProject is not null);
        ImportQuotationPackageCommand = new AsyncRelayCommand(
            ImportQuotationPackageAsync,
            () => !IsFileBusy && !IsPriceBusy && !IsDocumentBusy);
        PreviousQuotationBasketPageCommand = new RelayCommand(
            () => ChangeQuotationBasketPage(QuotationBasketPage - 1),
            () => QuotationBasketPage > 1);
        NextQuotationBasketPageCommand = new RelayCommand(
            () => ChangeQuotationBasketPage(QuotationBasketPage + 1),
            () => SelectedQuotationLine is not null &&
                  QuotationBasketPage * QuotationBasketPageSize < SelectedQuotationLine.BasketCount);
        OpenQuotationReferenceCommand = new RelayCommand<QuotationReferenceDisplay>(
            OpenQuotationReference,
            reference => reference is not null && Uri.TryCreate(reference.Source.PortalUrl, UriKind.Absolute, out _));
        AccessQuotationDocumentsCommand = new AsyncRelayCommand<QuotationReferenceDisplay>(
            AccessQuotationDocumentsAsync,
            reference => reference is not null && !IsDocumentBusy && !IsFileBusy &&
                         PncpContractKey.TryParse(
                             reference.Source.ContractId,
                             reference.Source.PortalUrl,
                             out _));
        NewQuotationCommand = new AsyncRelayCommand(NewQuotationAsync, () => !IsFileBusy && !IsPriceBusy);
        RenameQuotationCommand = new AsyncRelayCommand(
            RenameQuotationAsync,
            () => !IsFileBusy && !IsPriceBusy && SelectedQuotationProject is not null);
        DeleteQuotationCommand = new AsyncRelayCommand(
            DeleteQuotationAsync,
            () => !IsFileBusy && !IsPriceBusy && SelectedQuotationProject is not null);
        DeleteQuotationLineCommand = new AsyncRelayCommand(
            DeleteQuotationLineAsync,
            () => !IsFileBusy && !IsPriceBusy && SelectedQuotationLine is not null);
        RenameQuotationLineCommand = new AsyncRelayCommand(
            RenameQuotationLineAsync,
            () => !IsFileBusy && !IsPriceBusy && SelectedQuotationLine is not null &&
                  SelectedQuotationProject is not null);
        ImportQuotationCommand = new AsyncRelayCommand(
            ImportQuotationAsync,
            () => !IsFileBusy && !IsPriceBusy && !IsDocumentBusy);
        AiQuotationCommand = new AsyncRelayCommand(
            StartAiQuotationAsync,
            () => !IsFileBusy && !IsPriceBusy && !IsDocumentBusy);
        ResumeQuotationAutomationCommand = new AsyncRelayCommand(
            ResumeQuotationAutomationAsync,
            () => !IsFileBusy && !IsPriceBusy && !IsDocumentBusy &&
                  SelectedQuotationProject is not null);
        CancelQuotationAutomationCommand = new RelayCommand(
            () => _quotationAutomationCancellation?.Cancel(),
            () => _quotationAutomationCancellation is not null);
        RefineQuotationPromptsCommand = new AsyncRelayCommand(
            RefineQuotationPromptsAsync,
            () => !IsFileBusy && !IsPriceBusy && !IsDocumentBusy &&
                  SelectedQuotationProject is not null);
        OpenRestrictiveQuotationSearchCommand = new RelayCommand(
            () => OpenQuotationSearch(PromptMatchLevel.Restrictive),
            () => SelectedQuotationLine is not null);
        OpenIntermediateQuotationSearchCommand = new RelayCommand(
            () => OpenQuotationSearch(PromptMatchLevel.Intermediate),
            () => SelectedQuotationLine?.Line.PromptSet is { IntermediateText.Length: > 0 });
        OpenBroadQuotationSearchCommand = new RelayCommand(
            () => OpenQuotationSearch(PromptMatchLevel.Broad),
            () => SelectedQuotationLine?.Line.PromptSet is { BroadText.Length: > 0 });
        RenameManualBasketCommand = new AsyncRelayCommand(
            RenameSelectedManualBasketAsync,
            () => !IsFileBusy && SelectedQuotationBasket?.Source.IsManual == true);
        DeleteManualBasketCommand = new AsyncRelayCommand(
            DeleteSelectedManualBasketAsync,
            () => !IsFileBusy && SelectedQuotationBasket?.Source.IsManual == true);
        RemoveManualBasketReferenceCommand = new AsyncRelayCommand(
            RemoveSelectedManualBasketReferenceAsync,
            () => !IsFileBusy && SelectedQuotationBasket?.Source.IsManual == true &&
                  SelectedBasketReference is not null);
        OpenQuotationItemCommand = new RelayCommand(
            OpenSelectedQuotationItem,
            () => SelectedQuotationProject is not null && SelectedQuotationLine is not null);
    }

    public void OpenSelectedQuotationItem() =>
        OpenSelectedQuotationItemCore(null);

    public void OpenSelectedQuotationReferenceDocuments(string referenceId) =>
        OpenSelectedQuotationItemCore(referenceId);

    private void OpenSelectedQuotationItemCore(string? referenceId)
    {
        var project = SelectedQuotationProject;
        var line = SelectedQuotationLine;
        if (project is null || line is null)
        {
            return;
        }

        _priceCacheService.PauseForVisibleActivity();
        _visibleIdleResumeCancellation?.Cancel();
        _visibleIdleResumeCancellation?.Dispose();
        _visibleIdleResumeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _startupCancellation.Token);
        var resumeIndex = false;
        var resumeCatalog = false;
        if (IsIndexBusy && !_syncService.IsPaused)
        {
            _syncService.Pause();
            IsIndexPaused = true;
            _indexPausedForVisibleActivity = true;
            resumeIndex = true;
        }

        if (IsCatalogBusy && !IsCatalogPaused)
        {
            _catalogSyncService.Pause();
            IsCatalogPaused = true;
            _catalogPausedForVisibleActivity = true;
            resumeCatalog = true;
        }

        _ = ResumeVisiblePausedWorkAfterIdleAsync(
            resumeIndex,
            resumeCatalog,
            resumePriceCache: true,
            cancellationToken: _visibleIdleResumeCancellation.Token);

        if (_quotationItemWindow is { IsVisible: true } existing)
        {
            if (existing.ViewModel.LineId == line.Line.Id)
            {
                existing.Activate();
                if (!string.IsNullOrWhiteSpace(referenceId))
                {
                    existing.ShowReferenceDocuments(referenceId);
                }

                return;
            }

            existing.Close();
        }

        var viewModel = new QuotationItemViewModel(
            this,
            _quotationService,
            _quotationItemSearchService,
            _sweetCodeRepository,
            _internetPriceService,
            _internetEvidenceStore,
            _relevantPageService,
            _pdfPageRasterizer,
            _dataFolder,
            project.Id,
            line.Line.Id);
        var window = new QuotationItemWindow(
            viewModel,
            _windowCaptureService,
            _internetEvidenceStore,
            _columnLayouts)
        {
            Owner = Application.Current.MainWindow
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_quotationItemWindow, window))
            {
                _quotationItemWindow = null;
            }
        };
        _quotationItemWindow = window;
        if (!string.IsNullOrWhiteSpace(referenceId))
        {
            window.ShowReferenceDocuments(referenceId);
        }

        window.Show();
    }

    private void OpenQuotationSearch(PromptMatchLevel level)
    {
        var line = SelectedQuotationLine?.Line;
        if (line is null)
        {
            return;
        }

        var text = line.PromptSet?.GetText(level);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = level == PromptMatchLevel.Restrictive ? line.SearchText : string.Empty;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("Este nível ainda não possui um prompt.", "Pesquisa individual");
            return;
        }

        QueryText = text;
        SelectedResultsWorkspace = ResultsWorkspace.Search;
        StatusText =
            $"Prompt {level switch
            {
                PromptMatchLevel.Restrictive => "restritivo",
                PromptMatchLevel.Intermediate => "intermediário",
                _ => "amplo"
            }} carregado. Edite se desejar e pressione Pesquisar.";
    }

    private async Task RefineQuotationPromptsAsync()
    {
        var project = SelectedQuotationProject;
        if (project is null)
        {
            return;
        }

        var run = await _quotationService.GetLatestAutomationRunAsync(project.Id).ConfigureAwait(true);
        if (run is null || run.Mode != QuotationAutomationMode.TimedRoundRobin)
        {
            MessageBox.Show(
                "A cotação selecionada não possui uma automação com IA retomável.",
                "Retrabalhar prompts");
            return;
        }

        var currentRunAnalyses = (await _quotationService.GetAnalysesAsync(project.Id).ConfigureAwait(true))
            .Where(value => value.Line.AutomationRunId == run.Id)
            .OrderBy(value => value.Line.DisplayOrder)
            .ToArray();
        var currentRunLines = currentRunAnalyses
            .Select(value => value.Line)
            .ToArray();
        AiQuotationDraft? draft;
        if (string.IsNullOrWhiteSpace(run.SourcePdfSha256))
        {
            draft = await _aiDraftCache.FindCompatibleAsync(currentRunLines).ConfigureAwait(true);
            if (draft is null)
            {
                MessageBox.Show(
                    "A execução antiga não pôde ser vinculada de forma única a um rascunho. " +
                    "Selecione o PDF original em Automação com IA para reutilizar seu cache.",
                    "PDF original necessário");
                return;
            }

            await _quotationService.LinkAutomationDraftAsync(
                run.Id,
                draft.Id,
                draft.PdfSha256).ConfigureAwait(true);
            run = run with
            {
                SourceDraftId = draft.Id,
                SourcePdfSha256 = draft.PdfSha256
            };
        }
        else
        {
            draft = await _aiDraftCache.LoadAsync(run.SourcePdfSha256).ConfigureAwait(true);
            if (draft is null)
            {
                MessageBox.Show(
                    "O rascunho deste PDF não foi encontrado. Selecione o PDF original em Automação com IA " +
                    "para reconstruir ou reutilizar o cache pelo SHA-256.",
                    "Rascunho não encontrado");
                return;
            }
        }

        var projects = await _quotationService.GetProjectsAsync().ConfigureAwait(true);
        var existing = new Dictionary<Guid, IReadOnlyList<QuotationLine>>();
        foreach (var value in projects)
        {
            existing[value.Id] = (await _quotationService.GetAnalysesAsync(value.Id).ConfigureAwait(true))
                .Select(analysis => analysis.Line)
                .ToArray();
        }

        var settings = await _settingsService.LoadAsync().ConfigureAwait(true);
        var originalDraftItems = draft.Items
            .Where(value => value.IsSelected)
            .OrderBy(value => value.SourceOrder)
            .ToArray();
        var resolvedSourceOrders = originalDraftItems.Length == currentRunAnalyses.Length
            ? originalDraftItems
                .Zip(currentRunAnalyses)
                .Where(pair => pair.Second.Baskets
                    .Where(value => !value.IsManual)
                    .Any(value =>
                        value.References.Count == pair.Second.Line.RequestedBasketSize &&
                        value.References.All(reference =>
                            reference.State == QuotationReferenceState.Eligible) &&
                        value.MaximumDeviationPercent <= 25m))
                .Select(pair => pair.First.SourceOrder)
                .ToHashSet()
            : [];
        var window = new AiQuotationWindow(
            _aiDraftService,
            _aiCostEstimator,
            _aiCredentialStore,
            _aiDraftCache,
            _aiPromptRefinementService,
            _repository,
            _settingsService,
            settings,
            projects,
            existing,
            project.Id,
            draft,
            refinementOnly: true,
            resolvedSourceOrders: resolvedSourceOrders)
        {
            Owner = Application.Current.MainWindow
        };
        if (window.ShowDialog() != true || window.RefinedDraft is not { } refined)
        {
            return;
        }

        var runLines = currentRunLines;
        var draftItems = refined.Items
            .Where(value => value.IsSelected)
            .OrderBy(value => value.SourceOrder)
            .ToArray();
        if (runLines.Length != draftItems.Length)
        {
            MessageBox.Show(
                $"Não foi possível vincular de forma inequívoca as {runLines.Length:N0} linhas atuais " +
                $"aos {draftItems.Length:N0} itens selecionados do rascunho. Nenhum prompt foi aplicado.",
                "Vínculo ambíguo",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        for (var index = 0; index < runLines.Length; index++)
        {
            var line = runLines[index];
            var item = draftItems[index];
            var current = line.PromptSet ??
                          await _quotationService.GetItemSearchPromptSetAsync(line.Id).ConfigureAwait(true);
            await _quotationService.SaveItemSearchPromptSetAsync(current with
            {
                RestrictiveText = item.SearchText,
                IntermediateText = item.IntermediateSearchText,
                BroadText = item.BroadSearchText,
                Origin = window.UserEditedPromptStableIds.Contains(item.StableId)
                    ? SearchPromptOrigin.User
                    : SearchPromptOrigin.Ai,
                ValidationState = SearchPromptValidationState.Valid,
                UpdatedAt = DateTimeOffset.UtcNow
            }).ConfigureAwait(true);
        }

        var previousGlobal = await _quotationService.GetContractSearchPromptsAsync(run.Id)
            .ConfigureAwait(true);
        foreach (var prompt in previousGlobal)
        {
            await _quotationService.SaveContractSearchPromptAsync(
                prompt with { CandidateSetExhausted = true }).ConfigureAwait(true);
        }

        var nextOrder = previousGlobal.Select(value => value.DisplayOrder).DefaultIfEmpty(-1).Max() + 1;
        foreach (var text in refined.ContractSearchPrompts.Take(10))
        {
            await _quotationService.SaveContractSearchPromptAsync(new ContractSearchPrompt
            {
                RunId = run.Id,
                DisplayOrder = nextOrder++,
                Text = text,
                RandomPivot = Random.Shared.NextInt64(1, long.MaxValue)
            }).ConfigureAwait(true);
        }

        await LoadQuotationProjectAsync(project.Id).ConfigureAwait(true);
        MessageBox.Show(
            run.State == QuotationAutomationRunState.TimeExpired &&
            run.ActiveElapsed >= run.TimeBudget
                ? "A nova versão foi aplicada sem apagar referências, cestas, tempo ou checkpoints. " +
                  "Use Retomar automação para adicionar tempo; as listas já abertas serão reavaliadas primeiro."
                : "A nova versão foi aplicada sem apagar referências, cestas, tempo ou checkpoints. " +
                  "Use Retomar automação; as listas já abertas serão reavaliadas primeiro.",
            "Prompts atualizados",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async Task RefreshQuotationProjectsAsync(Guid? preferredProjectId = null)
    {
        var projects = await _quotationService.GetProjectsAsync().ConfigureAwait(true);
        var selectedId = preferredProjectId ?? SelectedQuotationProject?.Id;
        QuotationProjects.ReplaceAll(projects.Select(project => new QuotationProjectDisplay(project)));

        SelectedQuotationProject = QuotationProjects.FirstOrDefault(project => project.Id == selectedId)
                                   ?? QuotationProjects.FirstOrDefault();
        if (SelectedQuotationProject is null)
        {
            QuotationLines.Clear();
            QuotationBaskets.Clear();
            QuotationReferences.Clear();
            QuotationSummary = "Nenhum projeto de cotação criado.";
        }
    }

    private async Task LoadQuotationProjectAsync(Guid? projectId, Guid? preferredLineId = null)
    {
        if (projectId is null)
        {
            return;
        }

        try
        {
            var analyses = await _quotationService.GetAnalysesAsync(projectId.Value).ConfigureAwait(true);
            QuotationLines.ReplaceAll(analyses.Select(analysis => new QuotationLineDisplay(analysis)));

            SelectedQuotationLine = QuotationLines.FirstOrDefault(line => line.Line.Id == preferredLineId)
                                      ?? QuotationLines.FirstOrDefault();
            var resolved = QuotationLines.Count(line => line.Status == "Resolvido");
            QuotationSummary = $"{QuotationLines.Count:N0} item(ns); {resolved:N0} resolvido(s); " +
                               $"{QuotationLines.Count - resolved:N0} pendente(s).";
            NotifyCommands();
        }
        catch (Exception exception)
        {
            QuotationSummary = $"Não foi possível abrir o projeto: {exception.Message}";
        }
    }

    internal Task RefreshQuotationItemAsync(Guid projectId, Guid lineId) =>
        LoadQuotationProjectAsync(projectId, lineId);

    private async Task NewQuotationAsync()
    {
        var window = new TextPromptWindow(
            "Nova cotação",
            "Informe o nome da nova cotação:",
            $"Cotação {DateTime.Now:dd-MM-yyyy HH-mm}")
        {
            Owner = Application.Current.MainWindow
        };
        if (window.ShowDialog() != true)
        {
            return;
        }

        var project = await _quotationService.CreateProjectAsync(window.Value).ConfigureAwait(true);
        await RefreshQuotationProjectsAsync(project.Id).ConfigureAwait(true);
        StatusText = $"Cotação '{project.Name}' criada.";
    }

    private async Task RenameQuotationAsync()
    {
        var project = SelectedQuotationProject;
        if (project is null)
        {
            return;
        }

        var window = new TextPromptWindow("Renomear cotação", "Novo nome:", project.Name)
        {
            Owner = Application.Current.MainWindow
        };
        if (window.ShowDialog() != true)
        {
            return;
        }

        await _quotationService.RenameProjectAsync(project.Id, window.Value).ConfigureAwait(true);
        await RefreshQuotationProjectsAsync(project.Id).ConfigureAwait(true);
        StatusText = "Cotação renomeada.";
    }

    private async Task RenameQuotationLineAsync()
    {
        var project = SelectedQuotationProject;
        var line = SelectedQuotationLine?.Line;
        if (project is null || line is null) return;
        var window = new TextPromptWindow(
            "Editar nome do item",
            "Nome visível nas telas e exportações (o descritor técnico das pesquisas será preservado):",
            line.EffectiveDisplayName)
        {
            Owner = Application.Current.MainWindow
        };
        if (window.ShowDialog() != true) return;
        await _quotationService.RenameLineDisplayNameAsync(line.Id, window.Value).ConfigureAwait(true);
        await LoadQuotationProjectAsync(project.Id, line.Id).ConfigureAwait(true);
        StatusText = "Nome visível do item atualizado sem recalcular amostra ou cestas.";
    }

    private async Task DeleteQuotationAsync()
    {
        var project = SelectedQuotationProject;
        if (project is null || MessageBox.Show(
                $"Excluir permanentemente a cotação '{project.Name}' e todos os seus itens?",
                "Excluir cotação",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await _quotationService.DeleteProjectAsync(project.Id).ConfigureAwait(true);
        await RefreshQuotationProjectsAsync().ConfigureAwait(true);
        StatusText = "Cotação excluída.";
    }

    private async Task DeleteQuotationLineAsync()
    {
        var project = SelectedQuotationProject;
        var line = SelectedQuotationLine;
        if (project is null || line is null || MessageBox.Show(
                $"Excluir o item '{line.Line.EffectiveDisplayName}' desta cotação?",
                "Excluir item",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await _quotationService.DeleteLineAsync(line.Line.Id).ConfigureAwait(true);
        await LoadQuotationProjectAsync(project.Id).ConfigureAwait(true);
        StatusText = "Item excluído da cotação.";
    }

    private async Task ImportQuotationAsync()
    {
        var open = new OpenFileDialog
        {
            Title = "Importar itens de cotação",
            Filter = "Planilha do Excel (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false
        };
        if (open.ShowDialog() != true)
        {
            return;
        }

        QuotationImportDocument document;
        try
        {
            document = await _quotationWorkbookImportService.ReadAsync(open.FileName).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Importar cotação", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var weightsWindow = new QuotationWeightsWindow(AdequacyWeights.Default)
        {
            Owner = Application.Current.MainWindow,
            Title = "Pesos globais da cotação importada"
        };
        if (weightsWindow.ShowDialog() != true)
        {
            return;
        }

        var (startDate, endDate) = ResolveDateRange();
        var projects = await _quotationService.GetProjectsAsync().ConfigureAwait(true);
        var preview = new QuotationImportWindow(
            document,
            projects,
            SelectedQuotationProject?.Id,
            weightsWindow.Weights,
            $"{SelectedGeoFilter}; {startDate:dd/MM/yyyy} a {endDate:dd/MM/yyyy}",
            _columnLayouts)
        {
            Owner = Application.Current.MainWindow
        };
        if (preview.ShowDialog() != true)
        {
            return;
        }

        var responsibleName = PromptQuotationResponsibleName();
        if (responsibleName is null)
        {
            return;
        }

        var projectId = preview.ExistingProjectId;
        if (projectId is null)
        {
            var created = await _quotationService.CreateProjectAsync(preview.NewProjectName).ConfigureAwait(true);
            projectId = created.Id;
        }

        var run = await _quotationService.CreateAutomationRunAsync(
                projectId.Value,
                preview.OutputPath,
                responsibleName,
                SelectedGeoFilter,
                startDate,
                endDate,
                document.Items,
                weightsWindow.Weights)
            .ConfigureAwait(true);
        await RefreshQuotationProjectsAsync(projectId).ConfigureAwait(true);
        await LoadQuotationProjectAsync(projectId).ConfigureAwait(true);
        SelectedResultsWorkspace = ResultsWorkspace.Quotations;
        await RunQuotationAutomationAsync(run).ConfigureAwait(true);
    }

    private async Task StartAiQuotationAsync()
    {
        var projects = await _quotationService.GetProjectsAsync().ConfigureAwait(true);
        var currentSettings = await _settingsService.LoadAsync().ConfigureAwait(true);
        var existing = new Dictionary<Guid, IReadOnlyList<QuotationLine>>();
        foreach (var project in projects)
        {
            existing[project.Id] = (await _quotationService.GetAnalysesAsync(project.Id).ConfigureAwait(true))
                .Select(analysis => analysis.Line)
                .ToArray();
        }

        var window = new AiQuotationWindow(
            _aiDraftService,
            _aiCostEstimator,
            _aiCredentialStore,
            _aiDraftCache,
            _aiPromptRefinementService,
            _repository,
            _settingsService,
            currentSettings,
            projects,
            existing,
            SelectedQuotationProject?.Id)
        {
            Owner = Application.Current.MainWindow
        };
        if (window.ShowDialog() != true)
        {
            return;
        }

        var projectId = window.ExistingProjectId;
        if (projectId is null)
        {
            var created = await _quotationService.CreateProjectAsync(window.NewProjectName).ConfigureAwait(true);
            projectId = created.Id;
        }

        if (window.AddSelectedPromptsToSweetCodes)
        {
            var library = await _sweetCodeRepository.LoadAsync().ConfigureAwait(true);
            var expressions = library.Expressions.ToList();
            var normalized = expressions
                .Select(SearchText.Normalize)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var prompt in window.SweetCodePrompts)
            {
                if (normalized.Add(SearchText.Normalize(prompt)))
                {
                    expressions.Add(prompt);
                }
            }

            await _sweetCodeRepository.SaveAsync(library.Enabled, expressions).ConfigureAwait(true);
            _sweetCodeLibrary = new SweetCodeLibrary(library.Enabled, expressions);
            SweetCodeEnabled = library.Enabled;
            RefreshSweetCodeSuggestions();
        }

        var run = await _quotationService.CreateTimedAutomationRunAsync(
            projectId.Value,
            window.GeoFilter,
            window.StartDate,
            window.EndDate,
            window.AcceptedItems,
            window.Weights,
            window.TimeBudget,
            window.ContractSearchPrompts,
            window.SourceDraftId,
            window.SourcePdfSha256).ConfigureAwait(true);
        await RefreshQuotationProjectsAsync(projectId).ConfigureAwait(true);
        await LoadQuotationProjectAsync(projectId).ConfigureAwait(true);
        SelectedResultsWorkspace = ResultsWorkspace.Quotations;
        await RunTimedQuotationAutomationAsync(run).ConfigureAwait(true);
    }

    private async Task ResumeQuotationAutomationAsync()
    {
        var project = SelectedQuotationProject;
        if (project is null)
        {
            return;
        }

        var run = await _quotationService.GetLatestAutomationRunAsync(project.Id).ConfigureAwait(true);
        if (run is null)
        {
            MessageBox.Show(
                "Esta cotação não possui uma automação pendente ou com falha.",
                "Retomar automação",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (run.Mode == QuotationAutomationMode.TimedRoundRobin)
        {
            if (run.State == QuotationAutomationRunState.TimeExpired &&
                run.ActiveElapsed >= run.TimeBudget)
            {
                var prompt = new TextPromptWindow(
                    "Adicionar tempo",
                    "Quantos minutos deseja acrescentar ao prazo ativo desta automação?",
                    "30")
                {
                    Owner = Application.Current.MainWindow
                };
                if (prompt.ShowDialog() != true)
                {
                    return;
                }

                if (!int.TryParse(prompt.Value, out var extraMinutes) || extraMinutes <= 0)
                {
                    MessageBox.Show("Informe uma quantidade positiva de minutos.", "Adicionar tempo");
                    return;
                }

                var newBudget = run.TimeBudget + TimeSpan.FromMinutes(extraMinutes);
                if (newBudget > TimeSpan.FromHours(24))
                {
                    MessageBox.Show("O tempo total da execução não pode ultrapassar 24 horas.", "Adicionar tempo");
                    return;
                }

                await _quotationService.UpdateAutomationTimingAsync(
                    run.Id,
                    run.ActiveElapsed,
                    newBudget).ConfigureAwait(true);
                run = run with
                {
                    TimeBudget = newBudget,
                    State = QuotationAutomationRunState.Pending
                };
            }
            else if (run.State == QuotationAutomationRunState.TimeExpired)
            {
                var remaining = run.TimeBudget - run.ActiveElapsed;
                StatusText =
                    $"Retomando do último contrato; ainda restam {remaining:hh\\:mm\\:ss} do prazo ativo.";
                run = run with { State = QuotationAutomationRunState.Pending };
            }

            await RunTimedQuotationAutomationAsync(run).ConfigureAwait(true);
            return;
        }

        run = await EnsureAutomationResponsibleNameAsync(run).ConfigureAwait(true);
        if (run is null)
        {
            return;
        }

        run = await EnsureAutomationOutputPathAsync(run, project.Name).ConfigureAwait(true);
        if (run is null)
        {
            return;
        }

        await RunQuotationAutomationAsync(run).ConfigureAwait(true);
    }

    private async Task<QuotationAutomationRun?> EnsureAutomationOutputPathAsync(
        QuotationAutomationRun run,
        string projectName)
    {
        if (!string.IsNullOrWhiteSpace(run.OutputPath))
        {
            return run;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Escolher destino da automação importada",
            Filter = "Planilha do Excel (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            FileName = SanitizeFileName(projectName) + ".xlsx"
        };
        if (dialog.ShowDialog() != true)
        {
            StatusText = "Retomada cancelada; a automação importada permanece pendente.";
            return null;
        }

        await _quotationService.UpdateAutomationOutputPathAsync(run.Id, dialog.FileName)
            .ConfigureAwait(true);
        return run with { OutputPath = Path.GetFullPath(dialog.FileName) };
    }

    private async Task RunTimedQuotationAutomationAsync(QuotationAutomationRun run)
    {
        _quotationAutomationCancellation?.Dispose();
        _quotationAutomationCancellation = new CancellationTokenSource();
        _quotationAutomationCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        IsFileBusy = true;
        SetPriceBusy(true, usesNetwork: true);
        NotifyCommands();
        try
        {
            var progress = new Progress<TimedQuotationProgress>(async value =>
            {
                LatestTimedQuotationProgress = value;
                TimedQuotationProgressChanged?.Invoke(value);
                OnPropertyChanged(nameof(IsQuotationAutomationRunning));
                var totalSeconds = Math.Max(
                    1d,
                    (value.ActiveElapsed + value.Remaining).TotalSeconds);
                PriceSearchProgress = Math.Clamp(
                    value.ActiveElapsed.TotalSeconds / totalSeconds * 100d,
                    0d,
                    100d);
                ItemSearchSummary =
                    $"Automação por contratações — tempo {value.ActiveElapsed:hh\\:mm\\:ss}; " +
                    $"restante {value.Remaining:hh\\:mm\\:ss}; lote {value.BatchNumber:N0}, " +
                    $"contrato {value.ContractInBatch:N0}/{value.ContractsInBatch:N0}; " +
                    $"{value.UniqueContractsProcessed:N0} únicos; listas cache/API " +
                    $"{value.ItemListsFromCache:N0}/{value.ItemListsFromApi:N0}; " +
                    $"{value.MatchedItems:N0} correspondências; {value.RevealedPrices:N0} preços; " +
                    $"níveis R/I/A {value.RestrictiveItems:N0}/{value.IntermediateItems:N0}/{value.BroadItems:N0}; " +
                    $"resolvidos {value.ResolvedItems:N0}; " +
                    $"chamadas de resultado {value.ItemResultCalls:N0}; falhas {value.FailedCalls:N0}. " +
                    $"sequência sem preço {value.ContractsWithoutResult:N0}. " +
                    $"{value.Message}";
                StatusText = string.IsNullOrWhiteSpace(value.CurrentContractPrompt)
                    ? value.Message
                    : $"Prompt global: {value.CurrentContractPrompt} — {value.CurrentContractId}";
                if (value.UpdatedLineId is { } lineId && lineId != Guid.Empty)
                {
                    try
                    {
                        await LoadQuotationProjectAsync(run.ProjectId).ConfigureAwait(true);
                    }
                    catch (Exception exception)
                    {
                        StatusText = "A pesquisa continua, mas a grade não pôde ser atualizada agora: " +
                                     exception.Message;
                    }
                }
            });
            await _timedQuotationAutomation.RunAsync(
                run,
                progress,
                _quotationAutomationCancellation.Token).ConfigureAwait(true);
            var analyses = await _quotationService.GetAnalysesAsync(run.ProjectId).ConfigureAwait(true);
            var unresolved = analyses.Count(analysis =>
                analysis.Line.AutomationRunId == run.Id &&
                analysis.Line.AutomationState == QuotationAutomationItemState.TimeExpired);
            if (unresolved == 0)
            {
                StatusText = "Automação com IA concluída; todas as cestas válidas ficaram na cotação.";
            }
            else
            {
                var latest = await _quotationService.GetLatestAutomationRunAsync(run.ProjectId)
                    .ConfigureAwait(true);
                var hasRemainingTime = latest is not null &&
                                       latest.ActiveElapsed < latest.TimeBudget;
                StatusText = latest?.Message ??
                             $"A automação terminou com {unresolved:N0} item(ns) parcial(is).";
                StatusText += hasRemainingTime
                    ? " Use Retomar automação para continuar com os critérios de fallback restantes."
                    : " Use Retomar automação para adicionar tempo.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Automação com IA pausada; o tempo parado não foi contabilizado.";
        }
        catch (Exception exception)
        {
            await _quotationService.UpdateAutomationRunStateAsync(
                run.Id,
                QuotationAutomationRunState.Failed,
                exception.Message).ConfigureAwait(true);
            MessageBox.Show(exception.Message, "Automação com IA", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _quotationAutomationCancellation?.Dispose();
            _quotationAutomationCancellation = null;
            OnPropertyChanged(nameof(IsQuotationAutomationRunning));
            _quotationAutomationCompletion?.TrySetResult(true);
            _quotationAutomationCompletion = null;
            SetPriceBusy(false, usesNetwork: false);
            IsFileBusy = false;
            await LoadQuotationProjectAsync(run.ProjectId).ConfigureAwait(true);
            NotifyCommands();
        }
    }

    private async Task RunQuotationAutomationAsync(QuotationAutomationRun run)
    {
        _quotationAutomationCancellation?.Dispose();
        _quotationAutomationCancellation = new CancellationTokenSource();
        _quotationAutomationCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = _quotationAutomationCancellation.Token;
        IsFileBusy = true;
        SetPriceBusy(true, usesNetwork: true);
        NotifyCommands();
        var workbookExported = false;
        try
        {
            await _quotationService.UpdateAutomationRunStateAsync(
                run.Id,
                QuotationAutomationRunState.Running,
                "Cotação automática em andamento.").ConfigureAwait(true);
            var analyses = await _quotationService.GetAnalysesAsync(run.ProjectId).ConfigureAwait(true);
            var pending = analyses
                .Where(analysis => analysis.Line.AutomationRunId == run.Id &&
                                   analysis.Line.AutomationState is QuotationAutomationItemState.Pending or
                                       QuotationAutomationItemState.Failed)
                .OrderBy(analysis => analysis.Line.DisplayOrder)
                .ToArray();
            for (var index = 0; index < pending.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending[index];
                var line = current.Line;
                await _quotationService.UpdateAutomationItemStateAsync(
                    line.Id,
                    QuotationAutomationItemState.Running,
                    "Pesquisa automática em andamento.").ConfigureAwait(true);
                try
                {
                    QueryText = line.SearchText;
                    BatchCount = line.RequestedBatchCount;
                    MinimumPriceText = line.MinimumUnitPrice?.ToString("N4") ?? string.Empty;
                    MaximumPriceText = line.MaximumUnitPrice?.ToString("N4") ?? string.Empty;
                    ItemSearchRows.Clear();
                    PriceSearchProgress = 0;
                    _transientItemSearchService.Stop();
                    var automationQuery = new SearchQuery(
                        line.SearchText,
                        run.GeoFilter,
                        run.StartDate,
                        run.EndDate,
                        SearchSort.Nearest,
                        1,
                        200);
                    _localItemSearchSummary = await _repository.GetItemSearchLocalSummaryAsync(
                        automationQuery,
                        SearchText.Parse(line.SearchText),
                        cancellationToken).ConfigureAwait(true);
                    _searchTelemetryBaseline = _telemetry.GetSnapshot();
                    await _transientItemSearchService.StartAsync(
                        automationQuery,
                        cancellationToken).ConfigureAwait(true);
                    SetItemSearchActive(true);
                    var prefix = $"Item {index + 1:N0}/{pending.Length:N0} — {line.EffectiveDisplayName}. ";
                    var progress = new Progress<PriceBatchProgress>(value =>
                    {
                        UpdateItemSearchProgress(value);
                        ItemSearchSummary = prefix + ItemSearchSummary;
                    });
                    var rowsProgress = new Progress<IReadOnlyList<ItemSearchRow>>(AppendUniqueRows);
                    await _transientItemSearchService.RunContinuousAsync(
                        new PriceBatchRequest(
                            line.RequestedBatchCount,
                            true,
                            PriceBatchBudgetMode.CandidateContracts),
                        line.MinimumUnitPrice,
                        line.MaximumUnitPrice,
                        progress,
                        rowsProgress,
                        cancellationToken).ConfigureAwait(true);
                    var rows = await _transientItemSearchService.GetDiscoveredRowsAsync(
                        line.MinimumUnitPrice,
                        line.MaximumUnitPrice,
                        cancellationToken).ConfigureAwait(true);
                    var input = new QuotationLineInput(
                        line.Description,
                        line.RequestedQuantity,
                        line.RequestedUnit,
                        line.MinimumUnitPrice,
                        line.MaximumUnitPrice)
                    {
                        Weights = line.Weights,
                        RequestedBasketSize = line.RequestedBasketSize
                    };
                    var analysis = await _quotationService.CaptureSampleAsync(
                        run.ProjectId,
                        line.Id,
                        input,
                        rows,
                        cancellationToken).ConfigureAwait(true);
                    var recommended = analysis.Baskets.FirstOrDefault(basket => basket.IsRecommended);
                    if (recommended is not null)
                    {
                        await _quotationService.ConfirmBasketAsync(
                            analysis,
                            recommended.Key,
                            cancellationToken).ConfigureAwait(true);
                    }

                    var state = recommended is null
                        ? QuotationAutomationItemState.Insufficient
                        : recommended.References.Count >= line.RequestedBasketSize
                            ? QuotationAutomationItemState.Completed
                            : QuotationAutomationItemState.CompletedWithWarning;
                    var message = state switch
                    {
                        QuotationAutomationItemState.Completed =>
                            "Cesta recomendada selecionada automaticamente.",
                        QuotationAutomationItemState.CompletedWithWarning =>
                            $"Cesta recomendada reduzida para {recommended!.References.Count:N0} de " +
                            $"{line.RequestedBasketSize:N0} preços.",
                        _ => $"Foram encontradas {analysis.EligibleCount:N0} referência(s) válida(s)."
                    };
                    await _quotationService.UpdateAutomationItemStateAsync(
                        line.Id,
                        state,
                        message,
                        cancellationToken).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    await PreservePartialAutomationSampleAsync(run.ProjectId, line).ConfigureAwait(true);
                    await _quotationService.UpdateAutomationItemStateAsync(
                        line.Id,
                        QuotationAutomationItemState.Pending,
                        "Execução interrompida; pronta para retomar.").ConfigureAwait(true);
                    throw;
                }
                catch (Exception exception)
                {
                    await _quotationService.UpdateAutomationItemStateAsync(
                        line.Id,
                        QuotationAutomationItemState.Failed,
                        exception.Message).ConfigureAwait(true);
                }

                await LoadQuotationProjectAsync(run.ProjectId, line.Id).ConfigureAwait(true);
            }

            var finalAnalyses = await _quotationService.GetAnalysesAsync(run.ProjectId).ConfigureAwait(true);
            var remainingFailures = finalAnalyses.Count(analysis =>
                analysis.Line.AutomationRunId == run.Id &&
                analysis.Line.AutomationState is QuotationAutomationItemState.Pending or
                    QuotationAutomationItemState.Failed);
            var report = await _quotationService.GetReportAsync(run.ProjectId).ConfigureAwait(true);
            await _quotationWorkbookService.ExportAsync(
                run.OutputPath,
                report,
                run.ResponsibleName,
                cancellationToken).ConfigureAwait(true);
            workbookExported = true;
            var evidence = await ExportEvidenceAsync(
                GetEvidencePath(run.OutputPath),
                report,
                cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            var evidenceFolder = Path.GetDirectoryName(evidence.ReportPath) ?? evidence.ReportPath;
            var evidenceSummary =
                $" Evidências: {evidence.ReportPaths.Count:N0} arquivo(s) em {evidenceFolder}" +
                (evidence.Warnings.Count == 0
                    ? "."
                    : $" ({evidence.Warnings.Count:N0} aviso(s)).");
            if (remainingFailures == 0)
            {
                await _quotationService.UpdateAutomationRunStateAsync(
                    run.Id,
                    QuotationAutomationRunState.Completed,
                    $"Concluída e exportada para {run.OutputPath}.{evidenceSummary}").ConfigureAwait(true);
                StatusText = $"Cotação automática concluída: {run.OutputPath}.{evidenceSummary}";
            }
            else
            {
                await _quotationService.UpdateAutomationRunStateAsync(
                    run.Id,
                    QuotationAutomationRunState.Failed,
                    $"{remainingFailures:N0} item(ns) com falha; o arquivo parcial foi exportado.").ConfigureAwait(true);
                StatusText = $"Cotação exportada com {remainingFailures:N0} falha(s); use Retomar automação.";
            }
        }
        catch (OperationCanceledException)
        {
            await _quotationService.UpdateAutomationRunStateAsync(
                run.Id,
                QuotationAutomationRunState.Cancelled,
                "Execução cancelada; itens pendentes podem ser retomados.").ConfigureAwait(true);
            StatusText = "Cotação automática cancelada; os itens concluídos foram preservados.";
        }
        catch (Exception exception)
        {
            var preservedWorkbook = workbookExported
                ? $" A planilha já gerada foi preservada em {run.OutputPath}."
                : string.Empty;
            await _quotationService.UpdateAutomationRunStateAsync(
                run.Id,
                QuotationAutomationRunState.Failed,
                exception.Message + preservedWorkbook).ConfigureAwait(true);
            MessageBox.Show(
                exception.Message + preservedWorkbook,
                "Cotação automática",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _quotationAutomationCancellation.Dispose();
            _quotationAutomationCancellation = null;
            _quotationAutomationCompletion?.TrySetResult(true);
            _quotationAutomationCompletion = null;
            SetPriceBusy(false, usesNetwork: false);
            IsFileBusy = false;
            await LoadQuotationProjectAsync(run.ProjectId).ConfigureAwait(true);
            NotifyCommands();
        }
    }

    private async Task PreservePartialAutomationSampleAsync(Guid projectId, QuotationLine line)
    {
        try
        {
            var rows = await _itemSearchService.GetDiscoveredRowsAsync(
                line.MinimumUnitPrice,
                line.MaximumUnitPrice,
                CancellationToken.None).ConfigureAwait(true);
            if (rows.Count == 0)
            {
                return;
            }

            await _quotationService.CaptureSampleAsync(
                projectId,
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
                rows,
                CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            // A retomada continuará válida mesmo se a amostra parcial não puder ser persistida.
        }
    }

    public async Task CreateOrAppendManualBasketAsync(
        IReadOnlyList<ItemSearchDisplayRow> selectedRows)
    {
        ArgumentNullException.ThrowIfNull(selectedRows);
        var validRows = selectedRows
            .Where(row => row.Source.PriceState == ItemSearchPriceState.Homologated &&
                          row.Source.Result is { IsActive: true, HomologatedUnitValue: > 0 })
            .ToArray();
        if (validRows.Length == 0)
        {
            MessageBox.Show(
                "Selecione pelo menos uma linha com preço homologado ativo e positivo.",
                "Cesta manual",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var projects = await _quotationService.GetProjectsAsync().ConfigureAwait(true);
        var analyses = new Dictionary<Guid, IReadOnlyList<QuotationLineAnalysis>>();
        foreach (var project in projects)
        {
            analyses[project.Id] = await _quotationService.GetAnalysesAsync(project.Id).ConfigureAwait(true);
        }

        var expression = SearchText.Parse(QueryText);
        var description = expression.PositiveText.Length > 0
            ? expression.PositiveText
            : QueryText.Trim();
        var window = new ManualBasketWindow(
            projects,
            analyses,
            SelectedQuotationProject?.Id,
            description,
            MinimumPriceText,
            MaximumPriceText)
        {
            Owner = Application.Current.MainWindow
        };
        if (window.ShowDialog() != true || window.Input is null)
        {
            return;
        }

        IsFileBusy = true;
        try
        {
            var projectId = window.ExistingProjectId;
            if (projectId is null)
            {
                var project = await _quotationService.CreateProjectAsync(window.NewProjectName).ConfigureAwait(true);
                projectId = project.Id;
            }

            var saved = await _quotationService.SaveManualBasketAsync(
                    projectId.Value,
                    window.ExistingLineId,
                    window.Input,
                    window.ExistingBasketId,
                    window.BasketName,
                    validRows.Select(row => row.Source).ToArray())
                .ConfigureAwait(true);
            await RefreshQuotationProjectsAsync(projectId).ConfigureAwait(true);
            await LoadQuotationProjectAsync(projectId, saved.Analysis.Line.Id).ConfigureAwait(true);
            SelectedResultsWorkspace = ResultsWorkspace.Quotations;
            StatusText =
                $"Cesta manual \"{saved.Basket.Name}\" salva com " +
                $"{saved.Basket.ReferenceIds.Count:N0} preço(s).";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Cesta manual",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsFileBusy = false;
            NotifyCommands();
        }
    }

    private async Task UseCurrentSampleAsync()
    {
        var projects = (await _quotationService.GetProjectsAsync().ConfigureAwait(true));
        var expression = SearchText.Parse(QueryText);
        var quotationDescription = expression.PositiveText.Length > 0
            ? expression.PositiveText
            : QueryText.Trim();
        var window = new QuotationSampleWindow(projects, quotationDescription, MinimumPriceText, MaximumPriceText)
        {
            Owner = Application.Current.MainWindow
        };
        if (window.ShowDialog() != true || window.Input is null)
        {
            return;
        }

        IsFileBusy = true;
        try
        {
            var projectId = window.ExistingProjectId;
            if (projectId is null)
            {
                var project = await _quotationService.CreateProjectAsync(window.NewProjectName).ConfigureAwait(true);
                projectId = project.Id;
            }

            var rows = await _itemSearchService.GetDiscoveredRowsAsync(
                    minimumUnitPrice: window.Input.MinimumUnitPrice,
                    maximumUnitPrice: window.Input.MaximumUnitPrice,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(true);
            var analysis = await _quotationService.CaptureSampleAsync(
                    projectId.Value,
                    lineId: null,
                    window.Input,
                    rows)
                .ConfigureAwait(true);
            await RefreshQuotationProjectsAsync(projectId).ConfigureAwait(true);
            await LoadQuotationProjectAsync(projectId, analysis.Line.Id).ConfigureAwait(true);
            SelectedResultsWorkspace = ResultsWorkspace.Quotations;
            StatusText = $"Amostra salva: {analysis.CollectedCount:N0} preço(s), {analysis.Baskets.Count:N0} cesta(s) válida(s).";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Não foi possível usar a amostra na cotação.\n\n{exception.Message}",
                "Cotação",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsFileBusy = false;
        }
    }

    private async Task UpdateCurrentSampleAsync()
    {
        var selectedProject = SelectedQuotationProject;
        var selectedLine = SelectedQuotationLine;
        if (selectedProject is null || selectedLine is null)
        {
            return;
        }

        var sessionText = SearchText.Normalize(
            SearchText.Parse(_itemSearchService.CurrentSession?.Text).PositiveText);
        var lineText = SearchText.Normalize(selectedLine.Description);
        if (sessionText.Length > 0 && lineText.Length > 0 && !lineText.Contains(sessionText, StringComparison.Ordinal) &&
            !sessionText.Contains(lineText, StringComparison.Ordinal))
        {
            var answer = MessageBox.Show(
                "A pesquisa atual tem um texto diferente do item selecionado. Deseja mesmo incorporar esses preços?",
                "Atualizar amostra",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        IsFileBusy = true;
        try
        {
            var line = selectedLine.Line;
            var input = new QuotationLineInput(
                line.Description,
                line.RequestedQuantity,
                line.RequestedUnit,
                line.MinimumUnitPrice,
                line.MaximumUnitPrice)
            {
                Weights = line.Weights,
                RequestedBasketSize = line.RequestedBasketSize
            };
            var rows = await _itemSearchService.GetDiscoveredRowsAsync(
                    minimumUnitPrice: input.MinimumUnitPrice,
                    maximumUnitPrice: input.MaximumUnitPrice,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(true);
            var analysis = await _quotationService.CaptureSampleAsync(
                    selectedProject.Id,
                    line.Id,
                    input,
                    rows)
                .ConfigureAwait(true);
            await LoadQuotationProjectAsync(selectedProject.Id, line.Id).ConfigureAwait(true);
            StatusText = $"Amostra atualizada para a versão {analysis.Line.SampleVersion}; a escolha requer reconfirmação.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Atualizar amostra", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsFileBusy = false;
        }
    }

    private async Task ConfirmSelectedBasketAsync()
    {
        var line = SelectedQuotationLine;
        var basket = SelectedQuotationBasket;
        var project = SelectedQuotationProject;
        if (line is null || basket is null || project is null)
        {
            return;
        }

        if (basket.Source.IsManual &&
            basket.Source.VisualState is QuotationBasketVisualState.ManualIncomplete or
                QuotationBasketVisualState.ManualInvalid)
        {
            var answer = MessageBox.Show(
                $"{basket.Source.ValidationMessage}\n\n" +
                "A cesta poderá ser exportada, e a planilha registrará esta ressalva. Deseja confirmar?",
                "Confirmar cesta manual com ressalva",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            await _quotationService.ConfirmBasketAsync(line.Analysis, basket.Key).ConfigureAwait(true);
            await LoadQuotationProjectAsync(project.Id, line.Line.Id).ConfigureAwait(true);
            StatusText = $"Cesta confirmada para {line.Line.EffectiveDisplayName}.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Confirmar cesta", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RenameSelectedManualBasketAsync()
    {
        var basket = SelectedQuotationBasket?.Source;
        var project = SelectedQuotationProject;
        var line = SelectedQuotationLine;
        if (basket?.ManualBasketId is null || project is null || line is null)
        {
            return;
        }

        var window = new TextPromptWindow(
            "Renomear cesta manual",
            "Novo nome:",
            basket.Name)
        {
            Owner = Application.Current.MainWindow
        };
        if (window.ShowDialog() != true)
        {
            return;
        }

        await _quotationService.RenameManualBasketAsync(basket.ManualBasketId.Value, window.Value)
            .ConfigureAwait(true);
        await LoadQuotationProjectAsync(project.Id, line.Line.Id).ConfigureAwait(true);
        StatusText = "Cesta manual renomeada.";
    }

    private async Task DeleteSelectedManualBasketAsync()
    {
        var basket = SelectedQuotationBasket?.Source;
        var project = SelectedQuotationProject;
        var line = SelectedQuotationLine;
        if (basket?.ManualBasketId is null || project is null || line is null)
        {
            return;
        }

        if (MessageBox.Show(
                $"Excluir a cesta manual \"{basket.Name}\"?",
                "Excluir cesta manual",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await _quotationService.DeleteManualBasketAsync(basket.ManualBasketId.Value).ConfigureAwait(true);
        await LoadQuotationProjectAsync(project.Id, line.Line.Id).ConfigureAwait(true);
        StatusText = "Cesta manual excluída.";
    }

    private async Task RemoveSelectedManualBasketReferenceAsync()
    {
        var basket = SelectedQuotationBasket?.Source;
        var reference = SelectedBasketReference;
        var project = SelectedQuotationProject;
        var line = SelectedQuotationLine;
        if (basket?.ManualBasketId is null || reference is null || project is null || line is null)
        {
            return;
        }

        await _quotationService.RemoveManualBasketReferenceAsync(
                basket.ManualBasketId.Value,
                reference.Id)
            .ConfigureAwait(true);
        await LoadQuotationProjectAsync(project.Id, line.Line.Id).ConfigureAwait(true);
        StatusText = "Referência removida da cesta manual.";
    }

    private async Task AdjustQuotationWeightsAsync()
    {
        var line = SelectedQuotationLine;
        var project = SelectedQuotationProject;
        if (line is null || project is null)
        {
            return;
        }

        var window = new QuotationWeightsWindow(line.Line.Weights)
        {
            Owner = Application.Current.MainWindow
        };
        if (window.ShowDialog() != true || window.Weights == line.Line.Weights)
        {
            return;
        }

        IsFileBusy = true;
        try
        {
            await _quotationService.UpdateWeightsAsync(line.Line.Id, window.Weights).ConfigureAwait(true);
            await LoadQuotationProjectAsync(project.Id, line.Line.Id).ConfigureAwait(true);
            StatusText = "Pesos do índice atualizados; as cestas foram recalculadas localmente e a escolha requer confirmação.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Pesos do índice", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsFileBusy = false;
        }
    }

    private async Task ExportQuotationAsync()
    {
        var project = SelectedQuotationProject;
        if (project is null)
        {
            return;
        }

        var responsibleName = PromptQuotationResponsibleName();
        if (responsibleName is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Exportar cotação",
            Filter = "Planilha do Excel (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            FileName = SanitizeFileName(project.Name) + ".xlsx"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsFileBusy = true;
        var workbookExported = false;
        try
        {
            var report = await _quotationService.GetReportAsync(project.Id).ConfigureAwait(true);
            await _quotationWorkbookService.ExportAsync(
                dialog.FileName,
                report,
                responsibleName).ConfigureAwait(true);
            workbookExported = true;
            var evidence = await ExportEvidenceAsync(
                GetEvidencePath(dialog.FileName),
                report,
                CancellationToken.None).ConfigureAwait(true);
            StatusText = $"Cotação exportada; {evidence.ReportPaths.Count:N0} PDF(s) de evidências em " +
                         $"{Path.GetDirectoryName(evidence.ReportPath)}" +
                         (evidence.Warnings.Count == 0
                             ? "."
                             : $", com {evidence.Warnings.Count:N0} aviso(s).");
            if (evidence.Warnings.Count > 0)
            {
                MessageBox.Show(
                    $"A planilha foi preservada e o relatório disponível foi gerado.\n\n" +
                    string.Join("\n", evidence.Warnings.Take(10)),
                    "Evidências exportadas com avisos",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            var message = workbookExported
                ? $"A planilha foi salva em:\n{dialog.FileName}\n\n" +
                  $"Não foi possível concluir o relatório de evidências: {exception.Message}"
                : exception.Message;
            MessageBox.Show(message, "Exportar cotação", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsFileBusy = false;
        }
    }

    private static string? PromptQuotationResponsibleName()
    {
        var window = new TextPromptWindow(
            "Responsável pela cotação",
            "Informe o nome completo de quem realizou a cotação:")
        {
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? window.Value : null;
    }

    private async Task<QuotationAutomationRun?> EnsureAutomationResponsibleNameAsync(
        QuotationAutomationRun run)
    {
        if (!string.IsNullOrWhiteSpace(run.ResponsibleName))
        {
            return run;
        }

        var responsibleName = PromptQuotationResponsibleName();
        if (responsibleName is null)
        {
            StatusText = "Retomada cancelada; informe o responsável antes de continuar.";
            return null;
        }

        await _quotationService.UpdateAutomationResponsibleNameAsync(run.Id, responsibleName)
            .ConfigureAwait(true);
        return run with { ResponsibleName = responsibleName };
    }

    private async Task ExportQuotationPackageAsync()
    {
        var project = SelectedQuotationProject;
        if (project is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Exportar pacote portátil de cotação",
            Filter = "Pacote de cotação PNCP King (*.pncpcotacao)|*.pncpcotacao",
            DefaultExt = ".pncpcotacao",
            AddExtension = true,
            FileName =
                $"{SanitizeFileName(project.Name)}-{DateTime.Today:yyyyMMdd}.pncpcotacao"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await RunFileOperationAsync(async cancellationToken =>
        {
            StatusText = "Validando dados e prints da cotação…";
            await _quotationPackageService.ExportAsync(
                    dialog.FileName,
                    project.Id,
                    cancellationToken)
                .ConfigureAwait(true);
            StatusText = $"Pacote da cotação criado em {dialog.FileName}";
        }).ConfigureAwait(true);
    }

    private async Task ImportQuotationPackageAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importar pacote portátil de cotação",
            Filter = "Pacote de cotação PNCP King (*.pncpcotacao)|*.pncpcotacao",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await RunFileOperationAsync(async cancellationToken =>
        {
            StatusText = "Validando pacote de cotação…";
            var preview = await _quotationPackageService
                .InspectAsync(dialog.FileName, cancellationToken)
                .ConfigureAwait(true);
            var summary =
                $"Cotação: {preview.ProjectName}\n" +
                $"Exportada em: {preview.ExportedAt.LocalDateTime:dd/MM/yyyy HH:mm}\n" +
                $"Itens: {preview.ItemCount:N0}\n" +
                $"Preços coletados: {preview.ReferenceCount:N0}\n" +
                $"Cestas manuais: {preview.ManualBasketCount:N0}\n" +
                $"Prints: {preview.EvidenceCount:N0}\n" +
                (preview.HasIncompleteAutomation
                    ? "Há uma automação incompleta que poderá ser retomada.\n"
                    : string.Empty);

            QuotationPackageImportMode mode;
            if (preview.HasProjectConflict)
            {
                var choice = MessageBox.Show(
                    summary +
                    "\nEsta cotação já existe neste banco.\n\n" +
                    "Sim: substituir a existente (uma cópia recuperável será criada).\n" +
                    "Não: importar como uma nova cópia.\n" +
                    "Cancelar: não alterar nada.",
                    "Conflito ao importar cotação",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);
                mode = choice switch
                {
                    MessageBoxResult.Yes => QuotationPackageImportMode.Replace,
                    MessageBoxResult.No => QuotationPackageImportMode.Copy,
                    _ => (QuotationPackageImportMode)(-1)
                };
                if ((int)mode < 0)
                {
                    StatusText = "Importação do pacote cancelada.";
                    return;
                }
            }
            else
            {
                if (MessageBox.Show(
                        summary + "\nImportar esta cotação?",
                        "Importar pacote de cotação",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    StatusText = "Importação do pacote cancelada.";
                    return;
                }

                mode = QuotationPackageImportMode.PreserveIdentity;
            }

            if (mode == QuotationPackageImportMode.Replace)
            {
                await CloseQuotationItemWindowForProjectAsync(preview.ProjectId)
                    .ConfigureAwait(true);
            }

            StatusText = "Importando cotação e restaurando prints…";
            var result = await _quotationPackageService.ImportAsync(
                    dialog.FileName,
                    mode,
                    cancellationToken)
                .ConfigureAwait(true);
            await RefreshQuotationProjectsAsync(result.ProjectId).ConfigureAwait(true);
            await LoadQuotationProjectAsync(result.ProjectId).ConfigureAwait(true);
            SelectedResultsWorkspace = ResultsWorkspace.Quotations;

            var recovery = string.IsNullOrWhiteSpace(result.RecoveryPackagePath)
                ? string.Empty
                : $"\n\nVersão anterior preservada em:\n{result.RecoveryPackagePath}";
            if (result.Warnings.Count > 0)
            {
                MessageBox.Show(
                    $"A cotação '{result.ProjectName}' foi importada com " +
                    $"{result.Warnings.Count:N0} aviso(s):\n\n" +
                    string.Join("\n", result.Warnings) +
                    recovery,
                    "Cotação importada com avisos",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else if (recovery.Length > 0)
            {
                MessageBox.Show(
                    $"A cotação '{result.ProjectName}' foi importada." + recovery,
                    "Cotação importada",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            StatusText =
                $"Cotação '{result.ProjectName}' importada: " +
                $"{preview.ItemCount:N0} item(ns), {preview.ReferenceCount:N0} preço(s) e " +
                $"{preview.EvidenceCount:N0} print(s).";
        }).ConfigureAwait(true);
    }

    private async Task CloseQuotationItemWindowForProjectAsync(Guid projectId)
    {
        var window = _quotationItemWindow;
        if (window is null || window.ViewModel.ProjectId != projectId)
        {
            return;
        }

        if (!window.IsLoaded)
        {
            window.Close();
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? closed = null;
        closed = (_, _) =>
        {
            window.Closed -= closed;
            completion.TrySetResult();
        };
        window.Closed += closed;
        window.Close();
        await completion.Task.ConfigureAwait(true);
    }

    private void BindSelectedQuotationLine()
    {
        QuotationReferences.ReplaceAll(
            SelectedQuotationLine?.Analysis.References
                .OrderBy(reference => reference.State)
                .ThenByDescending(reference => reference.Adequacy.Total)
                .Select(reference => new QuotationReferenceDisplay(reference)) ?? []);

        BindQuotationBasketPage();
        RebuildVisibleQuotationReferences();
        OnPropertyChanged(nameof(QuotationBasketPageSummary));
    }

    public async Task SetQuotationReferenceMembershipAsync(
        QuotationPriceDisplayRow row,
        bool include)
    {
        var project = SelectedQuotationProject;
        var line = SelectedQuotationLine;
        if (project is null || line is null)
        {
            return;
        }

        string? preferredBasketKey;
        if (!include)
        {
            if (SelectedQuotationBasket?.Source is { IsManual: true, ManualBasketId: not null } manual)
            {
                await _quotationService.RemoveManualBasketReferenceAsync(
                    manual.ManualBasketId.Value,
                    row.Id).ConfigureAwait(true);
                preferredBasketKey = SelectedQuotationBasket.Key;
            }
            else if (SelectedQuotationBasket is not null)
            {
                var copied = await _quotationService.CreateManualCopyAsync(
                    line.Analysis,
                    SelectedQuotationBasket.Source,
                    excludedReferenceId: row.Id).ConfigureAwait(true);
                preferredBasketKey = copied.Key;
            }
            else
            {
                return;
            }
        }
        else
        {
            QuotationManualBasket basket;
            if (SelectedQuotationBasket?.Source is { IsManual: true, ManualBasketId: not null } manual)
            {
                basket = await _quotationService.AddManualBasketReferenceAsync(
                    line.Line.Id,
                    manual.ManualBasketId.Value,
                    row.Id).ConfigureAwait(true);
            }
            else if (SelectedQuotationBasket is not null)
            {
                basket = await _quotationService.CreateManualCopyAsync(
                    line.Analysis,
                    SelectedQuotationBasket.Source).ConfigureAwait(true);
                basket = await _quotationService.AddManualBasketReferenceAsync(
                    line.Line.Id,
                    basket.Id,
                    row.Id).ConfigureAwait(true);
            }
            else
            {
                var names = line.Analysis.Baskets
                    .Where(value => value.IsManual)
                    .Select(value => value.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var number = 1;
                while (names.Contains($"Manual {number:N0}"))
                {
                    number++;
                }

                basket = await _quotationService.CreateManualBasketAsync(
                    line.Line.Id,
                    $"Manual {number:N0}",
                    [row.Id]).ConfigureAwait(true);
            }

            preferredBasketKey = basket.Key;
        }

        await LoadQuotationProjectAsync(project.Id, line.Line.Id).ConfigureAwait(true);
        SelectedQuotationBasket = QuotationBaskets.FirstOrDefault(value =>
                                      value.Key == preferredBasketKey) ??
                                  SelectedQuotationBasket;
    }

    private void RebuildVisibleQuotationReferences()
    {
        var selectedId = SelectedVisibleQuotationReference?.Id;
        var selectedIds = SelectedQuotationBasket?.Source.References
            .Select(reference => reference.Id)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var visibleReferences = SelectedQuotationLine?.Analysis.References
            .Select(reference =>
            {
                var inBasket = selectedIds.Contains(reference.Id);
                var visible = QuotationReferenceScope switch
                {
                    ReferenceViewScope.InBasket => inBasket,
                    ReferenceViewScope.EligibleOutsideBasket =>
                        !inBasket && reference.State == QuotationReferenceState.Eligible,
                    ReferenceViewScope.RejectedOrDuplicate =>
                        reference.State != QuotationReferenceState.Eligible,
                    _ => true
                };
                return (Reference: reference, InBasket: inBasket, Visible: visible);
            })
            .Where(value => value.Visible)
            .Select(value => new QuotationPriceDisplayRow(value.Reference, value.InBasket)) ?? [];
        VisibleQuotationReferences.ReplaceAll(visibleReferences);

        SelectedVisibleQuotationReference =
            VisibleQuotationReferences.FirstOrDefault(value => value.Id == selectedId) ??
            VisibleQuotationReferences.FirstOrDefault();
    }

    private void ChangeQuotationBasketPage(int page)
    {
        var total = SelectedQuotationLine?.BasketCount ?? 0;
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)QuotationBasketPageSize));
        QuotationBasketPage = Math.Clamp(page, 1, pages);
        BindQuotationBasketPage();
        NotifyCommands();
    }

    private void BindQuotationBasketPage()
    {
        var previouslySelectedKey = SelectedQuotationBasket?.Key;
        QuotationBaskets.ReplaceAll(
            SelectedQuotationLine?.Analysis.Baskets
                .Skip((QuotationBasketPage - 1) * QuotationBasketPageSize)
                .Take(QuotationBasketPageSize)
                .Select(basket => new QuotationBasketDisplay(
                    basket,
                    basket.Key == SelectedQuotationLine.Line.SelectedBasketKey)) ?? []);

        SelectedQuotationBasket = QuotationBaskets.FirstOrDefault(basket => basket.Key == previouslySelectedKey)
                                    ?? QuotationBaskets.FirstOrDefault(basket => basket.WasPreviouslySelected)
                                    ?? QuotationBaskets.FirstOrDefault(basket => basket.Source.IsRecommended)
                                    ?? QuotationBaskets.FirstOrDefault();
    }

    private static void OpenQuotationReference(QuotationReferenceDisplay? reference)
    {
        if (reference is null || !Uri.TryCreate(reference.Source.PortalUrl, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Abrir PNCP", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private Task AccessQuotationDocumentsAsync(QuotationReferenceDisplay? reference)
    {
        if (reference is null ||
            !PncpContractKey.TryParse(
                reference.Source.ContractId,
                reference.Source.PortalUrl,
                out var contract) ||
            contract is null)
        {
            return Task.CompletedTask;
        }

        var suggestedReference = SelectedQuotationLine?.Line.Description;
        if (string.IsNullOrWhiteSpace(suggestedReference))
        {
            suggestedReference = reference.ItemDescription;
        }

        return AccessDocumentsAsync(contract, suggestedReference);
    }

    private async Task<QuotationEvidenceResult> ExportEvidenceAsync(
        string destinationPath,
        QuotationProjectReport report,
        CancellationToken cancellationToken)
    {
        if (IsDocumentBusy)
        {
            throw new InvalidOperationException("Aguarde a operação de documentos em andamento.");
        }

        IsDocumentBusy = true;
        _documentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        DocumentProgress = 0;
        DocumentProgressText = "Documentos: preparando relatório de evidências…";
        try
        {
            var result = await _evidenceService.ExportAsync(
                destinationPath,
                report,
                CreateDocumentProgress(),
                _documentCancellation.Token).ConfigureAwait(true);
            DocumentProgress = 100;
            DocumentProgressText =
                $"Documentos: {result.ReportPaths.Count:N0} arquivo(s) concluído(s), " +
                $"{result.Occurrences:N0} ocorrência(s), " +
                $"{result.Warnings.Count:N0} aviso(s)";
            return result;
        }
        finally
        {
            _documentCancellation.Dispose();
            _documentCancellation = null;
            IsDocumentBusy = false;
            NotifyCommands();
        }
    }

    private static string GetEvidencePath(string workbookPath) =>
        Path.Combine(
            Path.GetDirectoryName(workbookPath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(workbookPath) + "_evidencias.pdf");

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return sanitized.Length == 0 ? "Cotação PNCP King" : sanitized;
    }
}
