using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PNCPKing.App.Services;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.Views;

public partial class AiQuotationWindow : Window
{
    private readonly IAiQuotationDraftService _draftService;
    private readonly IAiCostEstimator _costEstimator;
    private readonly IAiCredentialStore _credentialStore;
    private readonly IAiDraftCache _draftCache;
    private readonly IAiPromptRefinementService _promptRefinementService;
    private readonly IContractRepository _contracts;
    private readonly AppSettingsService _settingsService;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<QuotationLine>> _existingLines;
    private AppSettings _settings;
    private AiMarkdownPreparation? _preparation;
    private AiCostEstimate? _cost;
    private AiQuotationDraft? _draft;
    private CancellationTokenSource? _analysisCancellation;
    private readonly bool _refinementOnly;
    private readonly AiQuotationDraft? _existingDraft;
    private readonly IReadOnlySet<int> _resolvedSourceOrders;
    private readonly HashSet<string> _originalSelectedStableIds;

    public AiQuotationWindow(
        IAiQuotationDraftService draftService,
        IAiCostEstimator costEstimator,
        IAiCredentialStore credentialStore,
        IAiDraftCache draftCache,
        IAiPromptRefinementService promptRefinementService,
        IContractRepository contracts,
        AppSettingsService settingsService,
        AppSettings settings,
        IReadOnlyList<QuotationProject> projects,
        IReadOnlyDictionary<Guid, IReadOnlyList<QuotationLine>> existingLines,
        Guid? selectedProjectId,
        AiQuotationDraft? existingDraft = null,
        bool refinementOnly = false,
        IReadOnlySet<int>? resolvedSourceOrders = null)
    {
        _draftService = draftService;
        _costEstimator = costEstimator;
        _credentialStore = credentialStore;
        _draftCache = draftCache;
        _promptRefinementService = promptRefinementService;
        _contracts = contracts;
        _settingsService = settingsService;
        _settings = settings;
        _existingLines = existingLines;
        _existingDraft = existingDraft;
        _refinementOnly = refinementOnly;
        _resolvedSourceOrders = resolvedSourceOrders ?? new HashSet<int>();
        _originalSelectedStableIds = existingDraft?.Items
            .Where(value => value.IsSelected)
            .Select(value => value.StableId)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        DraftItems = [];
        InitializeComponent();
        CredentialModes =
        [
            new CredentialModeChoice(AiCredentialPersistence.Saved, "Salvar no Gerenciador de Credenciais do Windows"),
            new CredentialModeChoice(AiCredentialPersistence.Section, "Usar nesta seção"),
            new CredentialModeChoice(AiCredentialPersistence.OneTime, "Usar uma vez e apagar automaticamente")
        ];
        ProtocolOptions =
        [
            new ProtocolChoice(AiProviderProtocol.Responses, "Responses"),
            new ProtocolChoice(AiProviderProtocol.ChatCompletions, "Chat Completions")
        ];
        OutputModeOptions =
        [
            new OutputModeChoice(AiStructuredOutputMode.JsonSchema, "JSON Schema"),
            new OutputModeChoice(AiStructuredOutputMode.PromptJson, "JSON solicitado no prompt")
        ];
        DataContext = this;
        ProviderComboBox.ItemsSource = BuildProviderChoices();
        ProviderComboBox.SelectedItem =
            ProviderComboBox.Items.Cast<ProviderChoice>()
                .FirstOrDefault(choice => choice.Id == settings.LastAiProviderId)
            ?? ProviderComboBox.Items[1];
        ProtocolComboBox.SelectedIndex = 0;
        OutputModeComboBox.SelectedIndex = 0;
        CredentialModeComboBox.SelectedIndex = 1;
        CatalogTextBlock.Text = $"Catálogo {AiModelCatalog.CatalogVersion}";
        ProjectComboBox.ItemsSource = projects;
        ProjectComboBox.SelectedItem = projects.FirstOrDefault(project => project.Id == selectedProjectId)
                                       ?? projects.FirstOrDefault();
        var geoChoices = BuildGeoChoices();
        GeoFilterComboBox.ItemsSource = geoChoices;
        GeoFilterComboBox.SelectedIndex = geoChoices.Count - 1;
        StartDatePicker.SelectedDate = DateTime.Today.AddDays(-364);
        EndDatePicker.SelectedDate = DateTime.Today;
        EstimateChoiceComboBox.SelectedIndex = 0;
        SafetyMarginTextBox.Text = settings.AiSafetyMarginPercent.ToString("N0", CultureInfo.CurrentCulture);
        Closed += (_, _) =>
        {
            _analysisCancellation?.Cancel();
            ApiKeyPasswordBox.Clear();
        };
        if (existingDraft is not null)
        {
            Loaded += LoadExistingDraft_OnLoaded;
        }
    }

    public ObservableCollection<AiQuotationReviewItem> DraftItems { get; }
    public IReadOnlyList<CredentialModeChoice> CredentialModes { get; }
    public IReadOnlyList<ProtocolChoice> ProtocolOptions { get; }
    public IReadOnlyList<OutputModeChoice> OutputModeOptions { get; }

    public Guid? ExistingProjectId { get; private set; }
    public string NewProjectName { get; private set; } = string.Empty;
    public IReadOnlyList<QuotationImportItem> AcceptedItems { get; private set; } = [];
    public SearchGeoFilter GeoFilter { get; private set; } = SearchGeoFilter.All;
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public AdequacyWeights Weights { get; private set; } = AdequacyWeights.Default;
    public TimeSpan TimeBudget { get; private set; } = TimeSpan.FromMinutes(30);
    public bool AddSelectedPromptsToSweetCodes { get; private set; }
    public IReadOnlyList<string> SweetCodePrompts { get; private set; } = [];
    public IReadOnlyList<string> ContractSearchPrompts { get; private set; } = [];
    public Guid? SourceDraftId => _draft?.Id;
    public string SourcePdfSha256 => _draft?.PdfSha256 ?? string.Empty;
    public AiQuotationDraft? RefinedDraft => _draft;
    public IReadOnlySet<string> UserEditedPromptStableIds { get; private set; } = new HashSet<string>();

    private async void LoadExistingDraft_OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= LoadExistingDraft_OnLoaded;
        var draft = _existingDraft!;
        if (!File.Exists(draft.SourcePath))
        {
            MessageBox.Show(
                "O PDF original deste rascunho não está mais no caminho salvo. " +
                "Selecione o PDF original para reutilizar o cache pelo SHA-256.",
                "PDF original necessário",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            SetAnalysisBusy(true);
            PdfPathTextBox.Text = draft.SourcePath;
            _preparation = await _draftService.PrepareAsync(
                draft.SourcePath,
                CreateProgress(),
                _analysisCancellation!.Token);
            _draft = draft;
            PopulateDraftItems(draft);
            if (_refinementOnly)
            {
                foreach (var item in DraftItems)
                {
                    item.IsSelected = !_resolvedSourceOrders.Contains(item.SourceOrder);
                }

                foreach (var column in DraftItemsGrid.Columns)
                {
                    var header = column.Header?.ToString() ?? string.Empty;
                    column.IsReadOnly = header is not ("Usar" or "Intermediário" or "Amplo");
                }

                DraftItemsGrid.Items.Refresh();
            }
            SaveMarkdownButton.IsEnabled = true;
            AnalyzeButton.IsEnabled = false;
            StepsTab.SelectedIndex = 1;
            if (_refinementOnly)
            {
                StartAutomationButton.Content = "Aplicar nova versão dos prompts";
                BottomStatusTextBlock.Text =
                    "Retrabalhe os prompts e aplique a nova versão; referências e checkpoints não serão apagados.";
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Carregar rascunho", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetAnalysisBusy(false);
        }
    }

    private async void BrowsePdf_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecionar documento da demanda",
            Filter = "Documento PDF (*.pdf)|*.pdf",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        PdfPathTextBox.Text = dialog.FileName;
        _preparation = null;
        _cost = null;
        _draft = null;
        DraftItems.Clear();
        AnalyzeButton.IsEnabled = false;
        SaveMarkdownButton.IsEnabled = false;
        CostTextBlock.Text = "Prepare o documento para calcular tokens, câmbio e custo.";
        BottomStatusTextBlock.Text = $"PDF selecionado: {Path.GetFileName(dialog.FileName)}";
        NewProjectNameTextBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        await Task.CompletedTask;
    }

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(PdfPathTextBox.Text))
        {
            MessageBox.Show("Selecione um arquivo PDF existente.", "Automação com IA");
            return;
        }

        try
        {
            SetAnalysisBusy(true);
            var progress = CreateProgress();
            _preparation = await _draftService.PrepareAsync(
                PdfPathTextBox.Text,
                progress,
                _analysisCancellation!.Token);
            var cachedDraft = ForceRefreshCheckBox.IsChecked == true
                ? null
                : await _draftCache.LoadAsync(
                    _preparation.PdfSha256,
                    _analysisCancellation.Token);
            if (cachedDraft is not null)
            {
                _draft = cachedDraft;
                PopulateDraftItems(cachedDraft);
                SaveMarkdownButton.IsEnabled = true;
                AnalyzeButton.IsEnabled = false;
                CostTextBlock.Text =
                    "Rascunho retomado do cache; nenhuma chave, rede ou nova geração foi usada. " +
                    "Para analisar novamente, marque a opção de ignorar o resultado estruturado em cache e prepare outra vez.";
                AnalysisStatusTextBlock.Text = $"{cachedDraft.Items.Count:N0} item(ns) retomado(s).";
                BottomStatusTextBlock.Text = "Rascunho retomado; revise os itens antes de pesquisar.";
                StepsTab.SelectedIndex = 1;
                return;
            }

            var provider = ReadProviderConfiguration();
            var margin = ReadDecimal(SafetyMarginTextBox.Text, "margem de segurança");
            try
            {
                _cost = await _costEstimator.EstimateAsync(
                    _preparation.Markdown,
                    provider,
                    _preparation.ProbableItemCount,
                    margin,
                    _analysisCancellation.Token);
            }
            catch (InvalidOperationException) when (provider.IsOpenAi)
            {
                var manualRate = new TextPromptWindow(
                    "Câmbio manual",
                    "A PTAX está indisponível e não há valor em cache. Informe quantos reais equivalem a US$ 1:",
                    "5,50")
                {
                    Owner = this
                };
                if (manualRate.ShowDialog() != true)
                {
                    throw;
                }

                var rate = ReadDecimal(manualRate.Value, "câmbio manual");
                await _costEstimator.SaveManualUsdSellRateAsync(
                    rate,
                    DateOnly.FromDateTime(DateTime.Today),
                    _analysisCancellation.Token);
                _cost = await _costEstimator.EstimateAsync(
                    _preparation.Markdown,
                    provider,
                    _preparation.ProbableItemCount,
                    margin,
                    _analysisCancellation.Token);
            }
            var exchange = provider.IsOpenAi
                ? $" PTAX de venda: R$ {_cost.ExchangeRate:N4} ({_cost.ExchangeRateDate:dd/MM/yyyy})."
                : string.Empty;
            CostTextBlock.Text =
                $"Markdown: {_preparation.Markdown.Length:N0} caracteres; " +
                $"{_preparation.ProbableItemCount:N0} item(ns) provável(is). " +
                $"Entrada estimada/máxima: {_cost.ExpectedInputTokens:N0}/{_cost.MaximumInputTokens:N0} tokens. " +
                $"Saída estimada/máxima: {_cost.ExpectedOutputTokens:N0}/{_cost.MaximumOutputTokens:N0} tokens. " +
                $"Custo estimado/máximo: {_cost.ExpectedCostBrl:C2}/{_cost.MaximumCostBrl:C2}.{exchange} " +
                string.Join(' ', _cost.Warnings);
            CostCeilingTextBox.Text = _cost.MaximumCostBrl.ToString("N2", CultureInfo.CurrentCulture);
            SaveMarkdownButton.IsEnabled = true;
            AnalyzeButton.IsEnabled = true;
            AnalysisStatusTextBlock.Text = "Markdown preparado localmente; revise o teto antes da geração.";
        }
        catch (OperationCanceledException)
        {
            AnalysisStatusTextBlock.Text = "Preparação cancelada.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Preparar PDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetAnalysisBusy(false);
        }
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (_preparation is null || _cost is null)
        {
            MessageBox.Show("Prepare o Markdown e a estimativa de custo primeiro.", "Automação com IA");
            return;
        }

        var provider = ReadProviderConfiguration();
        var ceiling = ReadDecimal(CostCeilingTextBox.Text, "teto de custo");
        if (ceiling < _cost.ExpectedCostBrl)
        {
            MessageBox.Show(
                $"O teto de {ceiling:C2} é insuficiente para a saída provável " +
                $"({_cost.ExpectedCostBrl:C2}). A geração não foi iniciada.",
                "Teto insuficiente",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var parts = 1;
        if (!_cost.FitsContext)
        {
            var dividedMaximum = _cost.MaximumCostBrl * _cost.SuggestedPartCount;
            var answer = MessageBox.Show(
                $"O documento não cabe no limite informado. São necessárias aproximadamente " +
                $"{_cost.SuggestedPartCount:N0} partes e o teto conservador passa a " +
                $"{dividedMaximum:C2}. Autoriza explicitamente as gerações separadas? " +
                "A reconciliação será local, sem chamada adicional.",
                "Autorizar divisão",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            if (ceiling < dividedMaximum)
            {
                MessageBox.Show(
                    $"Aumente o teto para pelo menos {dividedMaximum:C2} antes de autorizar a divisão.",
                    "Teto insuficiente");
                return;
            }

            parts = _cost.SuggestedPartCount;
        }

        var mode = ((CredentialModeChoice)CredentialModeComboBox.SelectedItem).Mode;
        var key = ApiKeyPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(key) && mode == AiCredentialPersistence.Saved)
        {
            key = await _credentialStore.ReadAsync(CredentialTarget(provider.Id)) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            MessageBox.Show("Informe a chave de API para o provedor selecionado.", "Automação com IA");
            return;
        }

        try
        {
            SetAnalysisBusy(true);
            if (mode == AiCredentialPersistence.Saved)
            {
                await _credentialStore.SaveAsync(CredentialTarget(provider.Id), key);
            }

            var outputLimit = CalculateOutputLimit(ceiling, _cost, provider.MaximumOutputTokens);
            _draft = await _draftService.CreateAsync(
                new AiDraftAnalysisRequest
                {
                    PdfPath = _preparation.SourcePath,
                    Provider = provider,
                    ApiKey = key,
                    MaximumOutputTokens = outputLimit,
                    ForceRefresh = ForceRefreshCheckBox.IsChecked == true,
                    ApprovedPartCount = parts
                },
                CreateProgress(),
                _analysisCancellation!.Token);
            PopulateDraftItems(_draft);
            await PersistProviderSettingsAsync(provider);
            StepsTab.SelectedIndex = 1;
            BottomStatusTextBlock.Text = $"{_draft.Items.Count:N0} item(ns) aguardando revisão.";
        }
        catch (OperationCanceledException)
        {
            AnalysisStatusTextBlock.Text = "Análise cancelada; nenhuma segunda geração foi iniciada.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message + "\n\nA resposta não será corrigida por uma segunda chamada automática.",
                "Análise com IA",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (mode == AiCredentialPersistence.OneTime)
            {
                ApiKeyPasswordBox.Clear();
                key = string.Empty;
            }

            SetAnalysisBusy(false);
        }
    }

    private async void SaveMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (_preparation is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Salvar cópia do Markdown",
            Filter = "Markdown (*.md)|*.md",
            DefaultExt = ".md",
            AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(_preparation.SourcePath) + ".md"
        };
        if (dialog.ShowDialog() == true)
        {
            await File.WriteAllTextAsync(dialog.FileName, _preparation.Markdown);
        }
    }

    private async void DeleteCredential_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var provider = ReadProviderConfiguration();
            await _credentialStore.DeleteAsync(CredentialTarget(provider.Id));
            ApiKeyPasswordBox.Clear();
            MessageBox.Show("A chave salva desse provedor foi excluída.", "Credencial de IA");
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Credencial de IA", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_preparation is null)
        {
            MessageBox.Show("Selecione e prepare um PDF primeiro.", "Rascunhos de IA");
            return;
        }

        await _draftCache.DeleteAsync(_preparation.PdfSha256);
        _draft = null;
        DraftItems.Clear();
        ReviewSummaryTextBlock.Text = "O rascunho estruturado deste PDF foi excluído.";
    }

    private async void ClearDrafts_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Excluir todos os rascunhos, Markdown e resultados estruturados da automação com IA?",
                "Limpar rascunhos de IA",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var bytes = await _draftCache.ClearAsync();
        _preparation = null;
        _draft = null;
        DraftItems.Clear();
        MessageBox.Show($"{bytes:N0} byte(s) removido(s). As chaves salvas não foram afetadas.", "Rascunhos de IA");
    }

    private async void ValidatePrompts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = DraftItems.Where(item => item.IsSelected).ToArray();
            if (selected.Length == 0)
            {
                MessageBox.Show("Selecione ao menos um item.", "Validação local");
                return;
            }

            var geo = ((GeoChoice)GeoFilterComboBox.SelectedItem).Filter;
            var start = DateOnly.FromDateTime(StartDatePicker.SelectedDate ?? DateTime.Today.AddDays(-364));
            var end = DateOnly.FromDateTime(EndDatePicker.SelectedDate ?? DateTime.Today);
            long candidates = 0;
            long cachedItems = 0;
            long cachedPrices = 0;
            foreach (var item in selected)
            {
                var expression = SearchText.Parse(item.SearchText);
                var summary = await _contracts.GetItemSearchLocalSummaryAsync(
                    new SearchQuery(item.SearchText, geo, start, end, SearchSort.Nearest),
                    expression);
                candidates += summary.CandidateContracts;
                cachedItems += summary.CachedMatchingItems;
                cachedPrices += summary.CachedItemsWithActivePrices;
                item.Status =
                    $"{summary.CandidateContracts:N0} contratações; " +
                    $"{summary.CachedMatchingItems:N0} itens parciais; " +
                    $"{summary.CachedItemsWithActivePrices:N0} preços parciais";
            }

            DraftItemsGrid.Items.Refresh();
            ReviewSummaryTextBlock.Text =
                $"Validação local por item concluída. Soma dos candidatos (pode haver contratos repetidos entre itens): " +
                $"{candidates:N0}; itens compatíveis em cache: {cachedItems:N0} (parcial); " +
                $"preços ativos em cache: {cachedPrices:N0} (parcial). Nenhuma chamada de rede foi feita.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Validação local", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RefinePrompts_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null || _preparation is null || DraftItems.Count == 0)
        {
            MessageBox.Show("Analise um PDF antes de retrabalhar os prompts.", "Retrabalhar prompts");
            return;
        }

        DraftItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        DraftItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var choice = MessageBox.Show(
            "Retrabalhar somente os itens marcados?\n\n" +
            "Sim: itens marcados\nNão: todos os itens\nCancelar: não gerar",
            "Escopo do retrabalho",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel)
        {
            return;
        }

        var reviewItems = (choice == MessageBoxResult.Yes
                ? DraftItems.Where(value => value.IsSelected)
                : DraftItems)
            .ToArray();
        if (reviewItems.Length == 0)
        {
            MessageBox.Show("Nenhum item foi selecionado.", "Retrabalhar prompts");
            return;
        }

        foreach (var item in reviewItems)
        {
            item.Validate();
            if (!item.IsValid)
            {
                MessageBox.Show(
                    $"Corrija primeiro o item {item.SourceNumber}: {item.Status}",
                    "Retrabalhar prompts");
                return;
            }
        }

        var provider = ReadProviderConfiguration();
        var mode = ((CredentialModeChoice)CredentialModeComboBox.SelectedItem).Mode;
        var key = ApiKeyPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(key) && mode == AiCredentialPersistence.Saved)
        {
            key = await _credentialStore.ReadAsync(CredentialTarget(provider.Id)) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            MessageBox.Show("Informe a chave da API para autorizar o retrabalho.", "Retrabalhar prompts");
            StepsTab.SelectedIndex = 0;
            return;
        }

        var estimate = await _costEstimator.EstimateAsync(
            _preparation.Markdown,
            provider,
            reviewItems.Length,
            ReadDecimal(SafetyMarginTextBox.Text, "margem de segurança"));
        if (MessageBox.Show(
                $"Será feita exatamente uma nova geração para {reviewItems.Length:N0} item(ns).\n" +
                $"Entrada máxima estimada: {estimate.MaximumInputTokens:N0} tokens.\n" +
                $"Saída máxima estimada: {estimate.MaximumOutputTokens:N0} tokens.\n" +
                $"Custo máximo estimado: {estimate.MaximumCostBrl:C2}.\n\nAutoriza a geração?",
                "Confirmar custo do retrabalho",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetAnalysisBusy(true);
            if (mode == AiCredentialPersistence.Saved)
            {
                await _credentialStore.SaveAsync(CredentialTarget(provider.Id), key);
            }

            var sources = reviewItems.Select(value => value.ToDraftItem()).ToArray();
            var result = await _promptRefinementService.RefineAsync(
                new AiPromptRefinementRequest
                {
                    Provider = provider,
                    ApiKey = key,
                    Markdown = _preparation.Markdown,
                    Items = sources,
                    MaximumOutputTokens = Math.Min(
                        provider.MaximumOutputTokens,
                        Math.Max(2_000, reviewItems.Length * 160))
                },
                CreateProgress(),
                _analysisCancellation!.Token);
            var byId = result.Items.ToDictionary(value => value.StableId, StringComparer.Ordinal);
            foreach (var item in reviewItems)
            {
                var refined = byId[item.Source.StableId];
                item.ApplyRefinement(
                    refined.RestrictiveText,
                    refined.IntermediateText,
                    refined.BroadText);
                item.Status = "Prompts retrabalhados; revisão pendente.";
            }

            ContractPromptsTextBox.Text = string.Join(Environment.NewLine, result.ContractSearchPrompts);
            _draft = _draft with
            {
                Items = DraftItems.Select(value => value.ToDraftItem()).ToArray(),
                ContractSearchPrompts = result.ContractSearchPrompts,
                Warnings = _draft.Warnings.Concat(result.Warnings).Distinct().ToArray()
            };
            await _draftCache.SaveAsync(_draft, _preparation.Markdown);
            DraftItemsGrid.Items.Refresh();
            ReviewSummaryTextBlock.Text =
                $"Retrabalho aplicado após uma geração: {result.InputTokens:N0} tokens de entrada e " +
                $"{result.OutputTokens:N0} de saída. Descrições, quantidades, unidades, estimativas e " +
                "prompts restritivos foram preservados.";
        }
        catch (OperationCanceledException)
        {
            AnalysisStatusTextBlock.Text = "Retrabalho cancelado; nenhum prompt foi aplicado.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message + "\n\nNenhuma alteração parcial foi aplicada.",
                "Retrabalhar prompts",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (mode == AiCredentialPersistence.OneTime)
            {
                ApiKeyPasswordBox.Clear();
                key = string.Empty;
            }

            SetAnalysisBusy(false);
        }
    }

    private void EstimateChoiceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || EstimateChoiceComboBox.SelectedIndex == 0)
        {
            return;
        }

        var use = EstimateChoiceComboBox.SelectedIndex == 1;
        foreach (var item in DraftItems)
        {
            item.UseEstimate = use && item.EstimatedUnitPrice is > 0;
        }

        DraftItemsGrid.Items.Refresh();
    }

    private void DefaultBasketSizeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || !int.TryParse(DefaultBasketSizeTextBox.Text, out var size) || size is < 3 or > 10)
        {
            return;
        }

        foreach (var item in DraftItems)
        {
            item.RequestedBasketSize = size;
        }

        DraftItemsGrid.Items.Refresh();
    }

    private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderComboBox.SelectedItem is not ProviderChoice choice)
        {
            return;
        }

        var provider = choice.Configuration;
        EndpointTextBox.Text = provider.Endpoint.AbsoluteUri;
        ModelTextBox.Text = provider.Model;
        ProtocolComboBox.SelectedItem = ProtocolOptions.First(option => option.Value == provider.Protocol);
        OutputModeComboBox.SelectedItem = OutputModeOptions.First(option => option.Value == provider.OutputMode);
        FreeProviderCheckBox.IsChecked = provider.IsFree;
        InputCostTextBox.Text = provider.InputCostBrlPerMillion.ToString(CultureInfo.CurrentCulture);
        OutputCostTextBox.Text = provider.OutputCostBrlPerMillion.ToString(CultureInfo.CurrentCulture);
        ContextTextBox.Text = provider.ContextWindow.ToString(CultureInfo.CurrentCulture);
        ProviderMaxOutputTextBox.Text = provider.MaximumOutputTokens.ToString(CultureInfo.CurrentCulture);
        ApiKeyPasswordBox.Clear();
    }

    private void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyDuplicateSelection();

    private void NewProjectNameTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyDuplicateSelection();

    private void CancelAnalysis_Click(object sender, RoutedEventArgs e) =>
        _analysisCancellation?.Cancel();

    private async void StartAutomation_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null || DraftItems.Count == 0)
        {
            MessageBox.Show("Analise e revise um PDF antes de iniciar.", "Automação com IA");
            StepsTab.SelectedIndex = 0;
            return;
        }

        if (_refinementOnly)
        {
            await CompleteRefinementOnlyAsync();
            return;
        }

        if (EstimateChoiceComboBox.SelectedIndex == 0)
        {
            MessageBox.Show("Escolha explicitamente se as estimativas serão usadas.", "Revisão");
            StepsTab.SelectedIndex = 1;
            return;
        }

        DraftItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        DraftItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = DraftItems.Where(item => item.IsSelected).OrderBy(item => item.SourceOrder).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show("Selecione ao menos um item válido.", "Revisão");
            return;
        }

        IReadOnlyList<string> contractPrompts;
        try
        {
            contractPrompts = ReadContractPrompts(requireExactlyTen: true);
            SynchronizeContractPrompts(contractPrompts);
        }
        catch (SearchQueryException exception)
        {
            MessageBox.Show(exception.Message, "Crivos de contratações");
            StepsTab.SelectedIndex = 1;
            return;
        }

        var inferred = selected.Any(item => item.HasInference);
        if (inferred && AcceptInferencesCheckBox.IsChecked != true)
        {
            MessageBox.Show("Há campos inferidos. Marque o aceite geral ou corrija/exclua essas linhas.", "Revisão");
            StepsTab.SelectedIndex = 1;
            return;
        }

        var invalid = new List<string>();
        foreach (var item in selected)
        {
            item.Validate();
            if (!item.IsValid)
            {
                invalid.Add($"Item {item.SourceNumber}: {item.Status}");
            }
        }

        if (invalid.Count > 0)
        {
            DraftItemsGrid.Items.Refresh();
            MessageBox.Show(
                string.Join(Environment.NewLine, invalid.Take(20)),
                "Linhas inválidas",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            StepsTab.SelectedIndex = 1;
            return;
        }

        NewProjectName = NewProjectNameTextBox.Text.Trim();
        ExistingProjectId = NewProjectName.Length == 0
            ? (ProjectComboBox.SelectedItem as QuotationProject)?.Id
            : null;
        if (ExistingProjectId is null && NewProjectName.Length == 0)
        {
            MessageBox.Show("Selecione uma cotação existente ou informe o nome de uma nova.", "Cotação");
            StepsTab.SelectedIndex = 2;
            return;
        }

        if (!int.TryParse(TimeMinutesTextBox.Text, out var minutes) || minutes is < 5 or > 1440)
        {
            MessageBox.Show("O tempo deve ficar entre 5 e 1.440 minutos.", "Pesquisa temporal");
            StepsTab.SelectedIndex = 2;
            return;
        }

        if (StartDatePicker.SelectedDate is null || EndDatePicker.SelectedDate is null ||
            StartDatePicker.SelectedDate > EndDatePicker.SelectedDate)
        {
            MessageBox.Show("Informe um período válido.", "Pesquisa temporal");
            StepsTab.SelectedIndex = 2;
            return;
        }

        Weights = new AdequacyWeights(
            ReadInteger(DescriptionWeightTextBox.Text, "peso da descrição"),
            ReadInteger(UnitWeightTextBox.Text, "peso da unidade"),
            ReadInteger(QuantityWeightTextBox.Text, "peso da quantidade"),
            ReadInteger(ProximityWeightTextBox.Text, "peso da proximidade"),
            ReadInteger(RecencyWeightTextBox.Text, "peso da atualidade"));
        try
        {
            Weights.Validate();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Pesos do índice");
            StepsTab.SelectedIndex = 2;
            return;
        }

        GeoFilter = ((GeoChoice)GeoFilterComboBox.SelectedItem).Filter;
        StartDate = DateOnly.FromDateTime(StartDatePicker.SelectedDate.Value);
        EndDate = DateOnly.FromDateTime(EndDatePicker.SelectedDate.Value);
        TimeBudget = TimeSpan.FromMinutes(minutes);
        AcceptedItems = selected.Select(item => item.ToImportItem()).ToArray();
        ContractSearchPrompts = contractPrompts;

        AddSelectedPromptsToSweetCodes = AddSweetCodesCheckBox.IsChecked == true;
        SweetCodePrompts = selected.Select(item => item.SearchText).ToArray();
        try
        {
            _draft = _draft with
            {
                Items = DraftItems.Select(item => item.ToDraftItem()).ToArray(),
                ContractSearchPrompts = ContractSearchPrompts
            };
            await _draftCache.SaveAsync(_draft, _preparation!.Markdown);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "Não foi possível salvar as correções do rascunho: " + exception.Message,
                "Automação com IA",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        DialogResult = true;
    }

    private async Task CompleteRefinementOnlyAsync()
    {
        DraftItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        DraftItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        IReadOnlyList<string> contractPrompts;
        try
        {
            contractPrompts = ReadContractPrompts(requireExactlyTen: true);
            SynchronizeContractPrompts(contractPrompts);
        }
        catch (SearchQueryException exception)
        {
            MessageBox.Show(exception.Message, "Crivos de contratações");
            return;
        }

        var invalid = DraftItems
            .Where(value =>
            {
                value.Validate();
                return !value.IsValid;
            })
            .Take(20)
            .Select(value => $"Item {value.SourceNumber}: {value.Status}")
            .ToArray();
        if (invalid.Length > 0)
        {
            DraftItemsGrid.Items.Refresh();
            MessageBox.Show(string.Join(Environment.NewLine, invalid), "Prompts inválidos");
            return;
        }

        ContractSearchPrompts = contractPrompts;
        UserEditedPromptStableIds = DraftItems
            .Where(value => value.PromptOrigin == SearchPromptOrigin.User)
            .Select(value => value.Source.StableId)
            .ToHashSet(StringComparer.Ordinal);
        _draft = _draft! with
        {
            Items = DraftItems.Select(value =>
                value.ToDraftItem() with
                {
                    IsSelected = _originalSelectedStableIds.Contains(value.Source.StableId)
                }).ToArray(),
            ContractSearchPrompts = contractPrompts
        };
        await _draftCache.SaveAsync(_draft, _preparation!.Markdown);
        DialogResult = true;
    }

    private void PopulateDraftItems(AiQuotationDraft draft)
    {
        DraftItems.Clear();
        foreach (var item in draft.Items)
        {
            DraftItems.Add(new AiQuotationReviewItem(item));
        }

        ContractPromptsTextBox.Text = string.Join(Environment.NewLine, draft.ContractSearchPrompts);

        ApplyDuplicateSelection();
        var warning = draft.Warnings.Count == 0 ? string.Empty : " " + string.Join(' ', draft.Warnings);
        ReviewSummaryTextBlock.Text =
            $"{draft.Items.Count:N0} item(ns); declarado: {draft.DeclaredItemCount:N0}. " +
            "Edite campos e prompt diretamente; uma linha inválida bloqueia somente ela." + warning;
        DraftItemsGrid.Items.Refresh();
    }

    private void ContractPromptsTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DraftItems.Count == 0)
        {
            return;
        }

        try
        {
            SynchronizeContractPrompts(ReadContractPrompts(requireExactlyTen: false));
        }
        catch (SearchQueryException)
        {
            // Durante a digitação a lista pode ficar momentaneamente incompleta.
            // A validação final apresenta a mensagem sem apagar as edições do usuário.
        }
    }

    private IReadOnlyList<string> ReadContractPrompts(bool requireExactlyTen)
    {
        var rawPrompts = ContractPromptsTextBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToArray();
        if (rawPrompts.Length > 10 || requireExactlyTen && rawPrompts.Length != 10)
        {
            throw new SearchQueryException(
                $"Informe exatamente 10 crivos globais de contratação, um por linha; foram informados {rawPrompts.Length:N0}.");
        }

        var prompts = new List<string>(rawPrompts.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawPrompt in rawPrompts)
        {
            var normalized = SearchText.NormalizeContractCandidatePrompt(rawPrompt);
            if (seen.Add(normalized))
            {
                prompts.Add(normalized);
            }
        }

        if (requireExactlyTen && prompts.Count != 10)
        {
            throw new SearchQueryException(
                $"Os crivos precisam ser distintos; após remover repetições restaram {prompts.Count:N0} de 10.");
        }

        return prompts;
    }

    private void SynchronizeContractPrompts(IReadOnlyList<string> contractPrompts)
    {
        // Os crivos continuam armazenados para a ampliação explícita e para
        // compatibilidade, mas novas expressões de item não recebem bloco C:.
    }

    private void ApplyDuplicateSelection()
    {
        if (!string.IsNullOrWhiteSpace(NewProjectNameTextBox?.Text))
        {
            RestoreDuplicateSelections();
            return;
        }

        if (ProjectComboBox?.SelectedItem is not QuotationProject project ||
            !_existingLines.TryGetValue(project.Id, out var lines))
        {
            RestoreDuplicateSelections();
            return;
        }

        var keys = lines.Select(DuplicateKey).ToHashSet(StringComparer.Ordinal);
        foreach (var item in DraftItems)
        {
            if (keys.Contains(DuplicateKey(item)))
            {
                if (!item.IsDuplicate)
                {
                    item.WasSelectedBeforeDuplicate = item.IsSelected;
                }

                item.IsDuplicate = true;
                item.IsSelected = false;
                item.Status = "Possível duplicidade na cotação; desmarcado por padrão.";
            }
            else if (item.IsDuplicate)
            {
                item.IsDuplicate = false;
                item.IsSelected = item.WasSelectedBeforeDuplicate;
                item.Status = item.Source.HasBlockingError
                    ? string.Join("; ", item.Source.Warnings.DefaultIfEmpty("Revisão obrigatória"))
                    : "Pronto para revisão";
            }
        }

        DraftItemsGrid?.Items.Refresh();
    }

    private void RestoreDuplicateSelections()
    {
        foreach (var item in DraftItems.Where(item => item.IsDuplicate))
        {
            item.IsDuplicate = false;
            item.IsSelected = item.WasSelectedBeforeDuplicate;
            item.Status = item.Source.HasBlockingError
                ? string.Join("; ", item.Source.Warnings.DefaultIfEmpty("Revisão obrigatória"))
                : "Pronto para revisão";
        }

        DraftItemsGrid?.Items.Refresh();
    }

    private AiProviderConfiguration ReadProviderConfiguration()
    {
        if (ProviderComboBox.SelectedItem is not ProviderChoice choice)
        {
            throw new InvalidOperationException("Selecione um provedor de IA.");
        }

        var endpoint = new Uri(EndpointTextBox.Text.Trim(), UriKind.Absolute);
        var local = endpoint.IsLoopback || string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (endpoint.Scheme != Uri.UriSchemeHttps && !local)
        {
            throw new ArgumentException("Use HTTPS; HTTP é permitido somente para localhost.");
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException("Não coloque credenciais na URL do endpoint.");
        }

        var selected = choice.Configuration;
        var isOpenAi = selected.IsOpenAi &&
                       string.Equals(endpoint.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase);
        var result = selected with
        {
            Endpoint = endpoint,
            Model = ModelTextBox.Text.Trim(),
            Protocol = ((ProtocolChoice)ProtocolComboBox.SelectedItem).Value,
            OutputMode = ((OutputModeChoice)OutputModeComboBox.SelectedItem).Value,
            IsOpenAi = isOpenAi,
            IsFree = FreeProviderCheckBox.IsChecked == true,
            ContextWindow = ReadInteger(ContextTextBox.Text, "limite de contexto"),
            MaximumOutputTokens = ReadInteger(ProviderMaxOutputTextBox.Text, "limite de saída"),
            InputCostBrlPerMillion = ReadDecimal(InputCostTextBox.Text, "custo de entrada"),
            OutputCostBrlPerMillion = ReadDecimal(OutputCostTextBox.Text, "custo de saída")
        };
        if (string.IsNullOrWhiteSpace(result.Model))
        {
            throw new ArgumentException("Informe o identificador do modelo.");
        }

        if (!result.IsOpenAi && !result.IsFree &&
            result.InputCostBrlPerMillion == 0 && result.OutputCostBrlPerMillion == 0)
        {
            throw new ArgumentException(
                "Informe o custo de entrada/saída do endpoint ou marque o serviço como gratuito.");
        }

        return result;
    }

    private IReadOnlyList<ProviderChoice> BuildProviderChoices()
    {
        var result = AiModelCatalog.OpenAiProfiles
            .Select(profile => new ProviderChoice(
                profile.Id,
                AiModelCatalog.CreateOpenAiConfiguration(profile)))
            .ToList();
        foreach (var setting in _settings.AiProviders ?? [])
        {
            if (!Uri.TryCreate(setting.Endpoint, UriKind.Absolute, out var endpoint))
            {
                continue;
            }

            result.Add(new ProviderChoice(
                setting.Id,
                new AiProviderConfiguration
                {
                    Id = setting.Id,
                    DisplayName = setting.DisplayName,
                    Endpoint = endpoint,
                    Model = setting.Model,
                    Protocol = Enum.TryParse<AiProviderProtocol>(setting.Protocol, out var protocol)
                        ? protocol
                        : AiProviderProtocol.Responses,
                    OutputMode = Enum.TryParse<AiStructuredOutputMode>(setting.OutputMode, out var outputMode)
                        ? outputMode
                        : AiStructuredOutputMode.JsonSchema,
                    IsFree = setting.IsFree,
                    ContextWindow = setting.ContextWindow,
                    MaximumOutputTokens = setting.MaximumOutputTokens,
                    InputCostBrlPerMillion = setting.InputCostBrlPerMillion,
                    OutputCostBrlPerMillion = setting.OutputCostBrlPerMillion
                }));
        }

        result.Add(new ProviderChoice(
            "compatible-custom",
            new AiProviderConfiguration
            {
                Id = "compatible-custom",
                DisplayName = "Endpoint compatível — configurar",
                Endpoint = new Uri("https://localhost/v1/"),
                Model = string.Empty
            }));
        return result;
    }

    private async Task PersistProviderSettingsAsync(AiProviderConfiguration provider)
    {
        var custom = !provider.IsOpenAi;
        var providers = (_settings.AiProviders ?? []).ToList();
        if (custom)
        {
            providers.RemoveAll(item => item.Id == provider.Id);
            providers.Add(new AiProviderSetting(
                provider.Id,
                provider.DisplayName,
                provider.Endpoint.AbsoluteUri,
                provider.Model,
                provider.Protocol.ToString(),
                provider.OutputMode.ToString(),
                provider.IsFree,
                provider.ContextWindow,
                provider.MaximumOutputTokens,
                provider.InputCostBrlPerMillion,
                provider.OutputCostBrlPerMillion));
        }

        var margin = ReadDecimal(SafetyMarginTextBox.Text, "margem");
        _settings = await _settingsService.UpdateAsync(latest => latest with
        {
            SettingsVersion = Math.Max(3, latest.SettingsVersion),
            AiProviders = providers,
            LastAiProviderId = provider.Id,
            AiSafetyMarginPercent = margin
        });
    }

    private IProgress<AiAnalysisProgress> CreateProgress() =>
        new Progress<AiAnalysisProgress>(value =>
        {
            AnalysisProgressBar.Maximum = Math.Max(1, value.Total);
            AnalysisProgressBar.Value = Math.Clamp(value.Completed, 0, Math.Max(1, value.Total));
            AnalysisStatusTextBlock.Text = value.Message;
        });

    private void SetAnalysisBusy(bool busy)
    {
        if (busy)
        {
            _analysisCancellation?.Dispose();
            _analysisCancellation = new CancellationTokenSource();
        }
        else
        {
            _analysisCancellation?.Dispose();
            _analysisCancellation = null;
        }

        CancelAnalysisButton.IsEnabled = busy;
        AnalyzeButton.IsEnabled = !busy && _preparation is not null && _cost is not null;
        StartAutomationButton.IsEnabled = !busy;
        BrowsePdfButton.IsEnabled = !busy;
        PrepareButton.IsEnabled = !busy;
        ProviderComboBox.IsEnabled = !busy;
        EndpointTextBox.IsEnabled = !busy;
        ModelTextBox.IsEnabled = !busy;
        ProtocolComboBox.IsEnabled = !busy;
        OutputModeComboBox.IsEnabled = !busy;
        ApiKeyPasswordBox.IsEnabled = !busy;
        CredentialModeComboBox.IsEnabled = !busy;
        DeleteDraftButton.IsEnabled = !busy;
        ClearDraftsButton.IsEnabled = !busy;
    }

    private static int CalculateOutputLimit(
        decimal ceiling,
        AiCostEstimate estimate,
        int providerMaximum)
    {
        if (ceiling >= estimate.MaximumCostBrl ||
            estimate.MaximumCostBrl <= estimate.ExpectedCostBrl)
        {
            return Math.Min(providerMaximum, checked((int)estimate.MaximumOutputTokens));
        }

        var proportion = (ceiling - estimate.ExpectedCostBrl) /
                         (estimate.MaximumCostBrl - estimate.ExpectedCostBrl);
        var tokens = estimate.ExpectedOutputTokens +
                     (estimate.MaximumOutputTokens - estimate.ExpectedOutputTokens) *
                     (double)Math.Clamp(proportion, 0m, 1m);
        return Math.Min(providerMaximum, Math.Max(1, (int)Math.Floor(tokens)));
    }

    private static string CredentialTarget(string providerId)
    {
        var safe = new string(providerId.Where(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
            .ToArray());
        return "PNCPKing/AI/" + (safe.Length == 0 ? "provider" : safe);
    }

    private static string DuplicateKey(QuotationLine line) =>
        $"{SearchText.Normalize(line.Description)}|{SearchText.Normalize(line.RequestedUnit)}|" +
        $"{SearchText.Normalize(line.SearchText)}";

    private static string DuplicateKey(AiQuotationReviewItem item) =>
        $"{SearchText.Normalize(item.Description)}|{SearchText.Normalize(item.Unit)}|" +
        $"{SearchText.Normalize(item.SearchText)}";

    private static int ReadInteger(string value, string field) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"Informe um valor inteiro válido para {field}.");

    private static decimal ReadDecimal(string value, string field) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsed) && parsed >= 0
            ? parsed
            : decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed) && parsed >= 0
                ? parsed
                : throw new ArgumentException($"Informe um valor válido para {field}.");

    private static IReadOnlyList<GeoChoice> BuildGeoChoices()
    {
        var choices = new List<GeoChoice>
        {
            new("Todo o Brasil", SearchGeoFilter.All),
            new("Sudeste", SearchGeoFilter.Southeast)
        };
        choices.AddRange(new[]
        {
            "AC", "AL", "AP", "AM", "BA", "CE", "DF", "ES", "GO", "MA", "MT", "MS", "MG",
            "PA", "PB", "PR", "PE", "PI", "RJ", "RN", "RS", "RO", "RR", "SC", "SP", "SE", "TO"
        }.Select(uf => new GeoChoice($"Somente {uf}", SearchGeoFilter.State(uf))));
        choices.Add(new GeoChoice("Mais próximas de Ribeirão Preto", SearchGeoFilter.NearRibeirao));
        return choices;
    }

    public sealed record CredentialModeChoice(AiCredentialPersistence Mode, string Label)
    {
        public override string ToString() => Label;
    }

    public sealed record ProtocolChoice(AiProviderProtocol Value, string Label)
    {
        public override string ToString() => Label;
    }

    public sealed record OutputModeChoice(AiStructuredOutputMode Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ProviderChoice(string Id, AiProviderConfiguration Configuration)
    {
        public override string ToString() =>
            $"{Configuration.DisplayName} — {Configuration.Model}".TrimEnd(' ', '—');
    }

    private sealed record GeoChoice(string DisplayName, SearchGeoFilter Filter);
}

public sealed class AiQuotationReviewItem
{
    private string _baselineRestrictiveSearchText;
    private string _baselineIntermediateSearchText;
    private string _baselineBroadSearchText;

    public AiQuotationReviewItem(AiQuotationDraftItem source)
    {
        Source = source;
        SourceOrder = source.SourceOrder;
        SourceNumber = source.SourceNumber;
        Description = source.Description;
        Quantity = source.Quantity;
        Unit = source.Unit;
        EstimatedUnitPrice = source.EstimatedUnitPrice;
        EstimatedTotalPrice = source.EstimatedTotalPrice;
        SearchText = source.SearchText;
        IntermediateSearchText = source.IntermediateSearchText;
        BroadSearchText = source.BroadSearchText;
        _baselineRestrictiveSearchText = SearchText;
        _baselineIntermediateSearchText = IntermediateSearchText;
        _baselineBroadSearchText = BroadSearchText;
        PositiveTerms = string.Join(
            " OU ",
            source.PositiveGroups.Select(group =>
                string.Join(" + ", group.Terms.Select(term => term.Text))));
        NegativeTerms = string.Join(", ", source.Exclusions.Select(term => term.Text));
        AcceptedUnits = string.Join(", ", source.AcceptedUnits);
        RequestedBasketSize = source.RequestedBasketSize is >= 3 and <= 10
            ? source.RequestedBasketSize
            : 3;
        Pages = string.Join(", ", new[]
            {
                source.DescriptionEvidence, source.QuantityEvidence, source.UnitEvidence,
                source.EstimateEvidence, source.SearchEvidence
            }.SelectMany(value => value.Pages).Distinct().Order());
        Origin = string.Join(
            "/",
            new[] { source.DescriptionEvidence.Origin, source.QuantityEvidence.Origin, source.UnitEvidence.Origin }
                .Distinct());
        Confidence = new[]
        {
            source.DescriptionEvidence.Confidence,
            source.QuantityEvidence.Confidence,
            source.UnitEvidence.Confidence,
            source.SearchEvidence.Confidence
        }.Average();
        Evidence = string.Join(
            " | ",
            new[]
            {
                source.DescriptionEvidence.Excerpt,
                source.QuantityEvidence.Excerpt,
                source.UnitEvidence.Excerpt,
                source.EstimateEvidence.Excerpt
            }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
        HasInference = new[]
        {
            source.DescriptionEvidence.Origin,
            source.QuantityEvidence.Origin,
            source.UnitEvidence.Origin,
            source.EstimateEvidence.Origin,
            source.SearchEvidence.Origin
        }.Contains(AiFieldOrigin.Inferred);
        Status = source.HasBlockingError
            ? string.Join("; ", source.Warnings.DefaultIfEmpty("Revisão obrigatória"))
            : "Pronto para revisão";
        IsValid = !source.HasBlockingError;
        UseEstimate = source.UseEstimatedPrice && source.EstimatedUnitPrice is > 0;
        IsSelected = source.IsSelected && !source.HasBlockingError;
    }

    public AiQuotationDraftItem Source { get; }
    public bool IsSelected { get; set; }
    public int SourceOrder { get; }
    public string SourceNumber { get; }
    public string Pages { get; }
    public string Description { get; set; }
    public decimal? Quantity { get; set; }
    public string Unit { get; set; }
    public decimal? EstimatedUnitPrice { get; set; }
    public decimal? EstimatedTotalPrice { get; set; }
    public bool UseEstimate { get; set; }
    public string SearchText { get; set; }
    public string IntermediateSearchText { get; set; }
    public string BroadSearchText { get; set; }
    public string PositiveTerms { get; }
    public string NegativeTerms { get; }
    public string AcceptedUnits { get; }
    public int RequestedBasketSize { get; set; }
    public string Origin { get; }
    public decimal Confidence { get; }
    public string Evidence { get; }
    public bool HasInference { get; }
    public string Status { get; set; }
    public bool IsValid { get; private set; }
    public bool IsDuplicate { get; set; }
    public bool WasSelectedBeforeDuplicate { get; set; }
    public SearchPromptOrigin PromptOrigin =>
        string.Equals(SearchText.Trim(), _baselineRestrictiveSearchText.Trim(), StringComparison.Ordinal) &&
        string.Equals(IntermediateSearchText.Trim(), _baselineIntermediateSearchText.Trim(), StringComparison.Ordinal) &&
        string.Equals(BroadSearchText.Trim(), _baselineBroadSearchText.Trim(), StringComparison.Ordinal)
            ? SearchPromptOrigin.Ai
            : SearchPromptOrigin.User;

    public void ApplyRefinement(string restrictive, string intermediate, string broad)
    {
        SearchText = restrictive;
        IntermediateSearchText = intermediate;
        BroadSearchText = broad;
        _baselineRestrictiveSearchText = restrictive;
        _baselineIntermediateSearchText = intermediate;
        _baselineBroadSearchText = broad;
    }

    public void SynchronizeContractCandidates(IReadOnlyList<string> contractPrompts)
    {
        SearchText = PNCPKing.Core.Search.SearchText.ReplaceContractCandidates(
            SearchText,
            contractPrompts);
        IntermediateSearchText = PNCPKing.Core.Search.SearchText.ReplaceContractCandidates(
            IntermediateSearchText,
            contractPrompts);
        BroadSearchText = PNCPKing.Core.Search.SearchText.ReplaceContractCandidates(
            BroadSearchText,
            contractPrompts);
        _baselineRestrictiveSearchText = PNCPKing.Core.Search.SearchText.ReplaceContractCandidates(
            _baselineRestrictiveSearchText,
            contractPrompts);
        _baselineIntermediateSearchText = PNCPKing.Core.Search.SearchText.ReplaceContractCandidates(
            _baselineIntermediateSearchText,
            contractPrompts);
        _baselineBroadSearchText = PNCPKing.Core.Search.SearchText.ReplaceContractCandidates(
            _baselineBroadSearchText,
            contractPrompts);
    }

    public void Validate()
    {
        var reasons = new List<string>();
        if (string.IsNullOrWhiteSpace(Description))
        {
            reasons.Add("descrição ausente");
        }

        if (Quantity is not > 0)
        {
            reasons.Add("quantidade inválida");
        }

        if (string.IsNullOrWhiteSpace(Unit))
        {
            reasons.Add("unidade ausente");
        }

        if (RequestedBasketSize is < 3 or > 10)
        {
            reasons.Add("cesta fora do intervalo 3–10");
        }

        try
        {
            _ = PNCPKing.Core.Search.SearchText.Parse(SearchText);
            _ = PNCPKing.Core.Search.SearchText.Parse(IntermediateSearchText);
            _ = PNCPKing.Core.Search.SearchText.Parse(BroadSearchText);
        }
        catch (Exception exception)
        {
            reasons.Add("prompt inválido: " + exception.Message);
        }

        IsValid = reasons.Count == 0;
        Status = IsValid ? "Válido" : string.Join("; ", reasons);
    }

    public QuotationImportItem ToImportItem() =>
        new(
            SourceOrder,
            SearchText.Trim(),
            Description.Trim(),
            Quantity!.Value,
            Unit.Trim(),
            null,
            null,
            1,
            RequestedBasketSize,
            EstimatedUnitPrice,
            EstimatedTotalPrice,
            UseEstimate && EstimatedUnitPrice is > 0,
            IntermediateSearchText.Trim(),
            BroadSearchText.Trim(),
            PromptOrigin);

    public AiQuotationDraftItem ToDraftItem() =>
        Source with
        {
            Description = Description.Trim(),
            Quantity = Quantity,
            Unit = Unit.Trim(),
            EstimatedUnitPrice = EstimatedUnitPrice,
            EstimatedTotalPrice = EstimatedTotalPrice,
            SearchText = SearchText.Trim(),
            IntermediateSearchText = IntermediateSearchText.Trim(),
            BroadSearchText = BroadSearchText.Trim(),
            HasBlockingError = !IsValid,
            Warnings = IsValid ? Source.Warnings : [Status],
            IsSelected = IsSelected,
            UseEstimatedPrice = UseEstimate && EstimatedUnitPrice is > 0,
            RequestedBasketSize = RequestedBasketSize
        };
}
