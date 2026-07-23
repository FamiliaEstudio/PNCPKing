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
    private QuotationProjectDisplay? _selectedQuotationProject;
    private QuotationLineDisplay? _selectedQuotationLine;
    private QuotationBasketDisplay? _selectedQuotationBasket;
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
            return $"Página {QuotationBasketPage:N0} de {pages:N0} — {total:N0} cesta(s) válida(s); " +
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
        IQuotationWorkbookService quotationWorkbookService)
    {
        _quotationService = quotationService;
        _quotationWorkbookService = quotationWorkbookService;
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
            () => !IsFileBusy && SelectedQuotationProject is not null && QuotationLines.Count > 0);
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

    private async Task UseCurrentSampleAsync()
    {
        var projects = (await _quotationService.GetProjectsAsync().ConfigureAwait(true));
        var window = new QuotationSampleWindow(projects, QueryText.Trim(), MinimumPriceText, MaximumPriceText)
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

        var sessionText = SearchText.Normalize(_itemSearchService.CurrentSession?.Text);
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
                Weights = line.Weights
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
        try
        {
            var report = await _quotationService.GetReportAsync(project.Id).ConfigureAwait(true);
            await _quotationWorkbookService.ExportAsync(dialog.FileName, report).ConfigureAwait(true);
            StatusText = $"Cotação exportada para {dialog.FileName}.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Exportar cotação", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return sanitized.Length == 0 ? "Cotação PNCP King" : sanitized;
    }
}
