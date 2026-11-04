using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using PNCPKing.App.Views;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.ViewModels;

public sealed partial class MainViewModel
{
    private SqliteQuotationRepository? _externalAiRepository;
    private QuotationAiCollectionService? _externalAiCollection;
    private QuotationAiGuideService? _externalAiGuide;
    private bool _showExternalAiProgress;
    private bool _isExternalAiWorking;
    private bool _isExternalAiIndeterminate;
    private double _externalAiProgress;
    private string _externalAiStatus = string.Empty;
    private bool _externalAiCanResume;

    public ICommand CreateExternalAiTemplateCommand { get; private set; } = null!;
    public ICommand CreateExternalAiGuideTemplatesCommand { get; private set; } = null!;
    public ICommand FilterExternalAiCandidatesCommand { get; private set; } = null!;
    public ICommand ImportExternalAiGuideCommand { get; private set; } = null!;
    public string ExternalAiStopText => _externalAiCanResume ? "Pausar coleta" : "Cancelar etapa";
    public bool CanChangeQuotationProject => !IsExternalAiWorking;

    public bool ShowExternalAiProgress
    {
        get => _showExternalAiProgress;
        private set => SetProperty(ref _showExternalAiProgress, value);
    }

    public bool IsExternalAiWorking
    {
        get => _isExternalAiWorking;
        private set
        {
            if (SetProperty(ref _isExternalAiWorking, value))
                OnPropertyChanged(nameof(CanChangeQuotationProject));
        }
    }

    public bool IsExternalAiIndeterminate
    {
        get => _isExternalAiIndeterminate;
        private set => SetProperty(ref _isExternalAiIndeterminate, value);
    }

    public double ExternalAiProgress
    {
        get => _externalAiProgress;
        private set => SetProperty(ref _externalAiProgress, Math.Clamp(value, 0d, 100d));
    }

    public string ExternalAiStatus
    {
        get => _externalAiStatus;
        private set => SetProperty(ref _externalAiStatus, value);
    }

    public void InitializeExternalAi(SqliteQuotationRepository repository, string databasePath)
    {
        _externalAiRepository = repository;
        _externalAiCollection = new QuotationAiCollectionService(_priceCacheRepository, repository, databasePath);
        _externalAiGuide = new QuotationAiGuideService(repository);
        CreateExternalAiTemplateCommand = new AsyncRelayCommand(CreateExternalAiTemplateAsync, CanRunExternalAi);
        CreateExternalAiGuideTemplatesCommand = new AsyncRelayCommand(CreateExternalAiGuideTemplatesAsync, CanRunExternalAi);
        FilterExternalAiCandidatesCommand = new AsyncRelayCommand(FilterExternalAiCandidatesAsync, CanRunExternalAi);
        ImportExternalAiGuideCommand = new AsyncRelayCommand(ImportExternalAiGuideAsync, CanRunExternalAi);
        NotifyCommands();
    }

    private bool CanRunExternalAi() =>
        _externalAiCollection is not null && _externalAiGuide is not null && !_disposed &&
        !IsFileBusy && !IsPriceBusy && !IsDocumentBusy && !IsForegroundBusy &&
        !IsIndexBusy && !IsCatalogBusy && !IsPriceCacheBusy && !IsNationalPriceIndexBusy &&
        !_automaticMaintenanceRunning && !IsAnyAggressivePncpMode &&
        !IsQuotationAutomationRunning &&
        _quotationItemWindow?.ViewModel.IsBusy != true &&
        _quotationItemWindow?.ViewModel.IsSearchBusy != true;

    private bool EnsureExternalAiAvailable()
    {
        if (CanRunExternalAi()) return true;
        MessageBox.Show(
            "Conclua ou encerre as atualizações, pesquisas e operações de arquivos em andamento antes de iniciar esta etapa. " +
            "A coleta será executada sozinha para reduzir a disputa pelo HD.",
            "Automação com IA externa", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private async Task<T?> RunExternalAiOperationAsync<T>(string message,
        Func<CancellationToken, IProgress<QuotationAiProgress>, Task<T>> action,
        bool resumableCollection = false) where T : class
    {
        if (!EnsureExternalAiAvailable()) return null;
        _quotationAutomationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_startupCancellation.Token);
        _quotationAutomationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = _quotationAutomationCancellation;
        var completion = _quotationAutomationCompletion;
        OnPropertyChanged(nameof(IsQuotationAutomationRunning));
        ShowExternalAiProgress = true;
        IsExternalAiWorking = true;
        _externalAiCanResume = resumableCollection;
        OnPropertyChanged(nameof(ExternalAiStopText));
        IsExternalAiIndeterminate = true;
        ExternalAiProgress = 0;
        ExternalAiStatus = message;
        StatusText = message;
        SelectedResultsWorkspace = ResultsWorkspace.Quotations;
        IsFileBusy = true;
        SetPriceBusy(true, usesNetwork: false);
        NotifyCommands();
        // Only counters and messages are marshalled to the dispatcher. Parsing, SQLite,
        // ZIP and basket analysis all stay on the worker, including synchronous API work.
        using var progress = new LatestQuotationAiProgress(value =>
        {
            if (!ReferenceEquals(_quotationAutomationCancellation, cancellation)) return;
            IsExternalAiIndeterminate = value.TotalItems <= 0;
            ExternalAiProgress = value.Percentage;
            ExternalAiStatus = value.TotalItems > 0
                ? $"{value.CompletedItems:N0}/{value.TotalItems:N0} itens · {value.CandidateCount:N0} candidatos — {value.Message}"
                : value.Message;
            StatusText = ExternalAiStatus;
        });
        try
        {
            var result = await Task.Run(() => action(cancellation.Token, progress), cancellation.Token).ConfigureAwait(true);
            progress.Flush();
            IsExternalAiIndeterminate = false;
            ExternalAiProgress = 100;
            return result;
        }
        catch (OperationCanceledException)
        {
            ExternalAiStatus = resumableCollection
                ? "Coleta interrompida. Use Retomar automação para continuar a partir do progresso salvo. Se a inserção inicial foi cancelada antes da conclusão, importe novamente o Excel."
                : "Etapa cancelada. As alterações já concluídas foram preservadas; execute esta etapa novamente quando desejar.";
            StatusText = ExternalAiStatus;
            return null;
        }
        catch (Exception exception) when (!AsyncCommandRuntime.IsCritical(exception))
        {
            ExternalAiStatus = "A etapa não foi concluída: " + exception.Message;
            StatusText = ExternalAiStatus;
            if (!_disposed)
                MessageBox.Show(exception.Message, "Automação com IA externa", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            progress.Dispose();
            IsExternalAiWorking = false;
            IsExternalAiIndeterminate = false;
            cancellation.Dispose();
            _quotationAutomationCancellation = null;
            OnPropertyChanged(nameof(IsQuotationAutomationRunning));
            SetPriceBusy(false, usesNetwork: false);
            IsFileBusy = false;
            _quotationAutomationCompletion = null;
            completion.TrySetResult(true);
            NotifyCommands();
        }
    }

    private async Task CreateExternalAiTemplateAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Salvar modelo Excel para IA externa",
            Filter = "Planilha do Excel (*.xlsx)|*.xlsx", DefaultExt = ".xlsx", AddExtension = true,
            FileName = "PNCPKing-Entrada-IA.xlsx"
        };
        if (dialog.ShowDialog() != true) return;
        var outputPath = dialog.FileName;
        var saved = await RunExternalAiOperationAsync("Preparando o modelo de entrada…", async (token, _) =>
        {
            await _quotationWorkbookImportService.CreateExternalAiTemplateAsync(outputPath, token).ConfigureAwait(false);
            return outputPath;
        }).ConfigureAwait(true);
        if (saved is not null)
            StatusText = ExternalAiStatus = $"Modelo salvo em {saved}. Preencha os itens e use Importar XLSX.";
    }

    private async Task<QuotationImportDocument?> ReadQuotationImportWithProgressAsync(string path)
    {
        // Also keeps large legacy input workbooks responsive, without changing their interpretation.
        var document = await RunExternalAiOperationAsync("Lendo os itens do Excel…",
            (token, _) => _quotationWorkbookImportService.ReadAsync(path, token)).ConfigureAwait(true);
        if (document is not null)
            StatusText = ExternalAiStatus = $"Excel lido: {document.Items.Count:N0} item(ns). Confira os parâmetros e o destino na prévia.";
        return document;
    }

    private async Task ImportExternalAiQuotationAsync(QuotationImportDocument document,
        QuotationImportWindow preview, AdequacyWeights weights, DateOnly startDate, DateOnly endDate)
    {
        var projectId = preview.ExistingProjectId;
        var geo = SelectedGeoFilter;
        var outputPath = preview.OutputPath;
        var newProjectName = preview.NewProjectName;
        var result = await RunExternalAiOperationAsync("Inserindo os itens da cotação…", async (token, progress) =>
        {
            if (projectId is null)
                projectId = (await _externalAiRepository!.CreateProjectAsync(newProjectName, token).ConfigureAwait(false)).Id;
            var run = await _externalAiRepository!.CreateExternalAiRunAsync(projectId.Value, outputPath,
                geo, startDate, endDate, document.Items, weights, progress, token).ConfigureAwait(false);
            return await _externalAiCollection!.CollectAsync(run, progress, token).ConfigureAwait(false);
        }, resumableCollection: true).ConfigureAwait(true);
        if (!_disposed && projectId is not null)
            await RefreshQuotationProjectsAsync(projectId).ConfigureAwait(true);
        if (result is not null)
            ReportExternalAiCollection(result, outputPath);
    }

    private async Task RunExternalAiCollectionAsync(QuotationAutomationRun run)
    {
        if (string.IsNullOrWhiteSpace(run.OutputPath))
        {
            MessageBox.Show("Esta coleta não possui destino. Importe novamente o Excel no modo IA externa.",
                "Retomar coleta", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var result = await RunExternalAiOperationAsync("Retomando a coleta de candidatos…",
            (token, progress) => _externalAiCollection!.CollectAsync(run, progress, token),
            resumableCollection: true).ConfigureAwait(true);
        if (!_disposed) await RefreshQuotationProjectsAsync(run.ProjectId).ConfigureAwait(true);
        if (result is not null) ReportExternalAiCollection(result, run.OutputPath);
    }

    private void ReportExternalAiCollection(QuotationAiPackageManifest result, string outputPath)
    {
        var partial = result.Items.Count(item => !item.Complete);
        ExternalAiStatus = $"Resultado salvo em {outputPath}. {result.Items.Count:N0} itens; " +
            $"{result.Items.Sum(item => item.CandidateCount):N0} candidatos; {partial:N0} item(ns) com coleta parcial. " +
            "Use Gerar modelos de triagem e guia para a próxima etapa.";
        StatusText = ExternalAiStatus;
    }

    private static string? ChooseExternalAiPackage(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title, Filter = "Resultado para IA (*.zip)|*.zip", CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private async Task CreateExternalAiGuideTemplatesAsync()
    {
        var package = ChooseExternalAiPackage("Escolher o resultado para gerar os modelos da IA");
        if (package is null) return;
        var folder = new OpenFolderDialog { Title = "Pasta dos modelos de triagem e guia" };
        if (folder.ShowDialog() != true) return;
        var output = folder.FolderName;
        var paths = await RunExternalAiOperationAsync("Preparando os modelos de triagem e guia…",
            (token, _) => _externalAiGuide!.CreateTemplatesAsync(package, output, token)).ConfigureAwait(true);
        if (paths is not null)
            StatusText = ExternalAiStatus = $"Modelos salvos: {paths.TriagePath} e {paths.GuidePath}.";
    }

    private async Task FilterExternalAiCandidatesAsync()
    {
        var package = ChooseExternalAiPackage("Escolher o resultado original da coleta");
        if (package is null) return;
        var triage = new OpenFileDialog
        {
            Title = "Escolher a triagem preenchida pela IA", Filter = "Planilha do Excel (*.xlsx)|*.xlsx", CheckFileExists = true
        };
        if (triage.ShowDialog() != true) return;
        var save = new SaveFileDialog
        {
            Title = "Salvar resultado após a triagem", Filter = "Resultado para IA (*.zip)|*.zip",
            DefaultExt = ".zip", AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(package) + "-triado.zip"
        };
        if (save.ShowDialog() != true) return;
        var triagePath = triage.FileName;
        var outputPath = save.FileName;
        var result = await RunExternalAiOperationAsync("Aplicando a triagem dos candidatos…",
            (token, progress) => _externalAiGuide!.FilterAsync(package, triagePath,
                outputPath, progress, token)).ConfigureAwait(true);
        if (result is not null)
            StatusText = ExternalAiStatus = $"Resultado triado salvo em {outputPath}. " +
                "Gere o modelo de guia a partir deste resultado para a seleção final.";
    }

    private async Task ImportExternalAiGuideAsync()
    {
        var package = ChooseExternalAiPackage("Escolher o resultado usado pela IA na seleção final");
        if (package is null) return;
        var guide = new OpenFileDialog
        {
            Title = "Escolher o guia final preenchido pela IA", Filter = "Planilha do Excel (*.xlsx)|*.xlsx", CheckFileExists = true
        };
        if (guide.ShowDialog() != true) return;
        var guidePath = guide.FileName;
        var preview = await RunExternalAiOperationAsync("Validando o guia e as cestas…",
            (token, progress) => _externalAiGuide!.PreviewGuideAsync(package, guidePath, progress, token)).ConfigureAwait(true);
        if (preview is null || _disposed) return;
        StatusText = ExternalAiStatus = $"Guia validado: {preview.ReadyCount:N0} item(ns) pronto(s), {preview.PendingCount:N0} pendente(s).";
        var window = new QuotationAiGuidePreviewWindow(preview) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;
        var result = await RunExternalAiOperationAsync("Aplicando as cestas válidas…",
            (token, progress) => _externalAiGuide!.ApplyGuideAsync(package, guidePath, progress, token)).ConfigureAwait(true);
        if (!_disposed) await RefreshQuotationProjectsAsync(preview.ProjectId).ConfigureAwait(true);
        if (result is not null)
        {
            StatusText = ExternalAiStatus = $"Guia processado: {result.AppliedCount:N0} cesta(s) aplicada(s), " +
                $"{result.UnchangedCount:N0} já aplicada(s), {result.PendingCount:N0} item(ns) pendente(s).";
            if (result.PendingCount > 0 && !_disposed)
                new QuotationAiGuidePreviewWindow(new QuotationAiGuidePreview(preview.PackageId,
                    preview.ProjectId, result.Items), allowApply: false)
                    { Owner = Application.Current.MainWindow }.ShowDialog();
        }
    }

    // A producer may report thousands of fast local pages. Keep one latest value
    // instead of queuing one dispatcher callback per page on older PCs.
    private sealed class LatestQuotationAiProgress : IProgress<QuotationAiProgress>, IDisposable
    {
        private readonly Action<QuotationAiProgress> _apply;
        private readonly DispatcherTimer _timer;
        private QuotationAiProgress? _latest;

        public LatestQuotationAiProgress(Action<QuotationAiProgress> apply)
        {
            _apply = apply;
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _timer.Tick += Tick;
            _timer.Start();
        }

        public void Report(QuotationAiProgress value) => Interlocked.Exchange(ref _latest, value);
        private void Tick(object? sender, EventArgs args) => Flush();
        public void Flush()
        {
            var value = Interlocked.Exchange(ref _latest, null);
            if (value is not null) _apply(value);
        }
        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= Tick;
            Interlocked.Exchange(ref _latest, null);
        }
    }
}
