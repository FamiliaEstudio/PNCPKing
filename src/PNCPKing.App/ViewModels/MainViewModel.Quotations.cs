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
    private CancellationTokenSource? _quotationAutomationCancellation;
    private QuotationProjectDisplay? _selectedQuotationProject;
    private QuotationLineDisplay? _selectedQuotationLine;
    private QuotationBasketDisplay? _selectedQuotationBasket;
    private QuotationReferenceDisplay? _selectedBasketReference;
    private int _quotationBasketPage = 1;
    private string _quotationSummary = "Nenhum projeto de cotação selecionado.";

    public ObservableCollection<QuotationProjectDisplay> QuotationProjects { get; } = [];
    public ObservableCollection<QuotationLineDisplay> QuotationLines { get; } = [];
    public ObservableCollection<QuotationBasketDisplay> QuotationBaskets { get; } = [];
    public ObservableCollection<QuotationReferenceDisplay> QuotationReferences { get; } = [];
    public ObservableCollection<QuotationReferenceDisplay> SelectedBasketReferences { get; } = [];

    public ICommand UseQuotationSampleCommand { get; private set; } = null!;
    public ICommand UpdateQuotationSampleCommand { get; private set; } = null!;
    public ICommand AdjustQuotationWeightsCommand { get; private set; } = null!;
    public ICommand ConfirmQuotationBasketCommand { get; private set; } = null!;
    public ICommand ExportQuotationCommand { get; private set; } = null!;
    public ICommand PreviousQuotationBasketPageCommand { get; private set; } = null!;
    public ICommand NextQuotationBasketPageCommand { get; private set; } = null!;
    public ICommand OpenQuotationReferenceCommand { get; private set; } = null!;
    public ICommand AccessQuotationDocumentsCommand { get; private set; } = null!;
    public ICommand NewQuotationCommand { get; private set; } = null!;
    public ICommand RenameQuotationCommand { get; private set; } = null!;
    public ICommand DeleteQuotationCommand { get; private set; } = null!;
    public ICommand DeleteQuotationLineCommand { get; private set; } = null!;
    public ICommand ImportQuotationCommand { get; private set; } = null!;
    public ICommand ResumeQuotationAutomationCommand { get; private set; } = null!;
    public ICommand CancelQuotationAutomationCommand { get; private set; } = null!;
    public ICommand RenameManualBasketCommand { get; private set; } = null!;
    public ICommand DeleteManualBasketCommand { get; private set; } = null!;
    public ICommand RemoveManualBasketReferenceCommand { get; private set; } = null!;

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

                NotifyCommands();
            }
        }
    }

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
        IQuotationWorkbookImportService quotationWorkbookImportService)
    {
        _quotationService = quotationService;
        _quotationWorkbookService = quotationWorkbookService;
        _quotationWorkbookImportService = quotationWorkbookImportService;
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
        ImportQuotationCommand = new AsyncRelayCommand(
            ImportQuotationAsync,
            () => !IsFileBusy && !IsPriceBusy && !IsDocumentBusy);
        ResumeQuotationAutomationCommand = new AsyncRelayCommand(
            ResumeQuotationAutomationAsync,
            () => !IsFileBusy && !IsPriceBusy && !IsDocumentBusy &&
                  SelectedQuotationProject is not null);
        CancelQuotationAutomationCommand = new RelayCommand(
            () => _quotationAutomationCancellation?.Cancel(),
            () => _quotationAutomationCancellation is not null);
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
    }

    private async Task RefreshQuotationProjectsAsync(Guid? preferredProjectId = null)
    {
        var projects = await _quotationService.GetProjectsAsync().ConfigureAwait(true);
        var selectedId = preferredProjectId ?? SelectedQuotationProject?.Id;
        QuotationProjects.Clear();
        foreach (var project in projects)
        {
            QuotationProjects.Add(new QuotationProjectDisplay(project));
        }

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
            QuotationLines.Clear();
            foreach (var analysis in analyses)
            {
                QuotationLines.Add(new QuotationLineDisplay(analysis));
            }

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
                $"Excluir o item '{line.Description}' desta cotação?",
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

        var projectId = preview.ExistingProjectId;
        if (projectId is null)
        {
            var created = await _quotationService.CreateProjectAsync(preview.NewProjectName).ConfigureAwait(true);
            projectId = created.Id;
        }

        var run = await _quotationService.CreateAutomationRunAsync(
                projectId.Value,
                preview.OutputPath,
                SelectedGeoFilter,
                startDate,
                endDate,
                document.Items,
                weightsWindow.Weights)
            .ConfigureAwait(true);
        await RefreshQuotationProjectsAsync(projectId).ConfigureAwait(true);
        await LoadQuotationProjectAsync(projectId).ConfigureAwait(true);
        SelectedResultsTabIndex = 2;
        await RunQuotationAutomationAsync(run).ConfigureAwait(true);
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

        await RunQuotationAutomationAsync(run).ConfigureAwait(true);
    }

    private async Task RunQuotationAutomationAsync(QuotationAutomationRun run)
    {
        _quotationAutomationCancellation?.Dispose();
        _quotationAutomationCancellation = new CancellationTokenSource();
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
                    _itemSearchService.Stop();
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
                    await _itemSearchService.StartAsync(
                        automationQuery,
                        cancellationToken).ConfigureAwait(true);
                    SetItemSearchActive(true);
                    var prefix = $"Item {index + 1:N0}/{pending.Length:N0} — {line.Description}. ";
                    var progress = new Progress<PriceBatchProgress>(value =>
                    {
                        UpdateItemSearchProgress(value);
                        ItemSearchSummary = prefix + ItemSearchSummary;
                    });
                    var rowsProgress = new Progress<IReadOnlyList<ItemSearchRow>>(AppendUniqueRows);
                    await _itemSearchService.RunContinuousAsync(
                        new PriceBatchRequest(line.RequestedBatchCount, true),
                        line.MinimumUnitPrice,
                        line.MaximumUnitPrice,
                        progress,
                        rowsProgress,
                        cancellationToken).ConfigureAwait(true);
                    var rows = await _itemSearchService.GetDiscoveredRowsAsync(
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
            await _quotationWorkbookService.ExportAsync(run.OutputPath, report, cancellationToken).ConfigureAwait(true);
            workbookExported = true;
            var evidence = await ExportEvidenceAsync(
                GetEvidencePath(run.OutputPath),
                report,
                cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            var evidenceSummary = evidence.Warnings.Count == 0
                ? $" Evidências: {evidence.ReportPath}."
                : $" Evidências parciais: {evidence.ReportPath} ({evidence.Warnings.Count:N0} aviso(s)).";
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
            SelectedResultsTabIndex = 2;
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
            SelectedResultsTabIndex = 2;
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
            StatusText = $"Cesta confirmada para {line.Description}.";
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
            await _quotationWorkbookService.ExportAsync(dialog.FileName, report).ConfigureAwait(true);
            workbookExported = true;
            var evidence = await ExportEvidenceAsync(
                GetEvidencePath(dialog.FileName),
                report,
                CancellationToken.None).ConfigureAwait(true);
            StatusText = evidence.Warnings.Count == 0
                ? $"Cotação e evidências exportadas para {Path.GetDirectoryName(dialog.FileName)}."
                : $"Cotação e evidências exportadas com {evidence.Warnings.Count:N0} aviso(s).";
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

    private void BindSelectedQuotationLine()
    {
        QuotationReferences.Clear();
        if (SelectedQuotationLine is not null)
        {
            foreach (var reference in SelectedQuotationLine.Analysis.References
                         .OrderBy(reference => reference.State)
                         .ThenByDescending(reference => reference.Adequacy.Total))
            {
                QuotationReferences.Add(new QuotationReferenceDisplay(reference));
            }
        }

        BindQuotationBasketPage();
        OnPropertyChanged(nameof(QuotationBasketPageSummary));
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
        QuotationBaskets.Clear();
        if (SelectedQuotationLine is not null)
        {
            foreach (var basket in SelectedQuotationLine.Analysis.Baskets
                         .Skip((QuotationBasketPage - 1) * QuotationBasketPageSize)
                         .Take(QuotationBasketPageSize))
            {
                QuotationBaskets.Add(new QuotationBasketDisplay(
                    basket,
                    basket.Key == SelectedQuotationLine.Line.SelectedBasketKey));
            }
        }

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
                $"Documentos: relatório concluído, {result.Occurrences:N0} ocorrência(s), " +
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
