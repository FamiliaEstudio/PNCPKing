using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;

namespace PNCPKing.App.Views;

public partial class ManualBasketWindow : Window
{
    private readonly Func<Guid, CancellationToken, Task<IReadOnlyList<QuotationLine>>> _loadLines;
    private readonly Func<Guid, CancellationToken, Task<IReadOnlyList<QuotationManualBasket>>> _loadBaskets;
    private readonly string _suggestedDescription;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _ready;
    private bool _closed;
    private bool _linesLoading;
    private bool _basketsLoading;
    private bool _linesFailed;
    private bool _basketsFailed;
    private int _lineLoadVersion;
    private int _basketLoadVersion;
    private int _manualBasketCount;

    public ManualBasketWindow(
        IReadOnlyList<QuotationProject> projects,
        Func<Guid, CancellationToken, Task<IReadOnlyList<QuotationLine>>> loadLines,
        Func<Guid, CancellationToken, Task<IReadOnlyList<QuotationManualBasket>>> loadBaskets,
        Guid? selectedProjectId,
        string suggestedDescription,
        string minimumPrice,
        string maximumPrice)
    {
        InitializeComponent();
        var openingEnterHeld = Keyboard.IsKeyDown(Key.Enter);
        PreviewKeyDown += (_, e) =>
        {
            if (openingEnterHeld && e.Key == Key.Enter) e.Handled = true;
        };
        PreviewKeyUp += (_, e) =>
        {
            if (e.Key == Key.Enter && openingEnterHeld)
            {
                openingEnterHeld = false;
                e.Handled = true;
            }
        };
        _loadLines = loadLines;
        _loadBaskets = loadBaskets;
        _suggestedDescription = suggestedDescription;
        ProjectComboBox.ItemsSource = projects;
        ProjectComboBox.SelectedItem = projects.FirstOrDefault(project => project.Id == selectedProjectId)
                                       ?? projects.FirstOrDefault();
        NewProjectNameTextBox.Text = projects.Count == 0
            ? $"Cotação {DateTime.Now:dd-MM-yyyy HH-mm}"
            : string.Empty;
        DescriptionTextBox.Text = suggestedDescription;
        MinimumPriceTextBox.Text = minimumPrice;
        MaximumPriceTextBox.Text = maximumPrice;
        Loaded += async (_, _) =>
        {
            if (_ready) return;
            _ready = true;
            await RefreshLinesAsync();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    public Guid? ExistingProjectId { get; private set; }
    public string NewProjectName { get; private set; } = string.Empty;
    public Guid? ExistingLineId { get; private set; }
    public Guid? ExistingBasketId { get; private set; }
    public string BasketName { get; private set; } = string.Empty;
    public QuotationLineInput? Input { get; private set; }

    private async void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_closed) await RefreshLinesAsync();
    }

    private async void LineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _closed) return;
        var line = (LineComboBox.SelectedItem as LineChoice)?.Line;
        var existing = line is not null;
        foreach (var control in NewLineControls)
        {
            control.IsEnabled = !existing;
        }

        if (line is not null)
        {
            DescriptionTextBox.Text = line.Description;
            QuantityTextBox.Text = line.RequestedQuantity > 0
                ? line.RequestedQuantity.ToString("#,##0.####", CultureInfo.CurrentCulture)
                : string.Empty;
            UnitTextBox.Text = line.RequestedUnit;
            MinimumPriceTextBox.Text = line.MinimumUnitPrice?.ToString("N4", CultureInfo.CurrentCulture) ?? string.Empty;
            MaximumPriceTextBox.Text = line.MaximumUnitPrice?.ToString("N4", CultureInfo.CurrentCulture) ?? string.Empty;
            BasketSizeTextBox.Text = line.RequestedBasketSize.ToString(CultureInfo.CurrentCulture);
        }
        else if (DescriptionTextBox.Text.Length == 0)
        {
            DescriptionTextBox.Text = _suggestedDescription;
        }

        await RefreshBasketsAsync();
    }

    private void BasketComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var basket = (BasketComboBox.SelectedItem as BasketChoice)?.Basket;
        BasketNameTextBox.Text = basket?.Name ?? BuildNextManualName();
    }

    private async Task RefreshLinesAsync()
    {
        var version = ++_lineLoadVersion;
        ++_basketLoadVersion;
        _linesLoading = true;
        _basketsLoading = false;
        _linesFailed = false;
        LoadStatusTextBlock.Text = string.Empty;
        var project = ProjectComboBox.SelectedItem as QuotationProject;
        LineComboBox.ItemsSource = new[] { new LineChoice("Novo item", null) };
        LineComboBox.SelectedIndex = 0;
        UpdateLoadingState();
        try
        {
            if (project is null) return;
            var lines = await _loadLines(project.Id, _lifetime.Token);
            if (_closed || version != _lineLoadVersion) return;
            var choices = new List<LineChoice> { new("Novo item", null) };
            choices.AddRange(QuotationOrganization.OrderLines(project, lines)
                .Select(line => new LineChoice(line.EffectiveDisplayName, line)));
            LineComboBox.ItemsSource = choices;
            LineComboBox.SelectedIndex = 0;
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception exception)
        {
            if (_closed || version != _lineLoadVersion) return;
            _linesFailed = true;
            LoadStatusTextBlock.Text = $"Não foi possível carregar os itens: {exception.Message}";
        }
        finally
        {
            if (!_closed && version == _lineLoadVersion)
            {
                _linesLoading = false;
                UpdateLoadingState();
            }
        }
    }

    private async Task RefreshBasketsAsync()
    {
        var version = ++_basketLoadVersion;
        var line = (LineComboBox.SelectedItem as LineChoice)?.Line;
        _manualBasketCount = 0;
        _basketsFailed = false;
        _basketsLoading = line is not null;
        BasketComboBox.ItemsSource = new[] { new BasketChoice("Nova cesta manual", null) };
        BasketComboBox.SelectedIndex = 0;
        UpdateLoadingState();
        try
        {
            if (line is null) return;
            LoadStatusTextBlock.Text = string.Empty;
            var baskets = await _loadBaskets(line.Id, _lifetime.Token);
            if (_closed || version != _basketLoadVersion) return;
            var choices = new List<BasketChoice> { new("Nova cesta manual", null) };
            choices.AddRange(baskets.Where(basket => basket.ReferenceIds.Count > 0)
                .Select(basket => new BasketChoice(
                    $"{basket.Name} ({basket.ReferenceIds.Count:N0} preço(s))",
                    basket)));
            _manualBasketCount = choices.Count - 1;
            BasketComboBox.ItemsSource = choices;
            BasketComboBox.SelectedIndex = 0;
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception exception)
        {
            if (_closed || version != _basketLoadVersion) return;
            _basketsFailed = true;
            LoadStatusTextBlock.Text = $"Não foi possível carregar as cestas: {exception.Message}";
        }
        finally
        {
            if (!_closed && version == _basketLoadVersion)
            {
                _basketsLoading = false;
                UpdateLoadingState();
            }
        }
    }

    private void UpdateLoadingState()
    {
        LineComboBox.IsEnabled = !_linesLoading;
        BasketComboBox.IsEnabled = !_linesLoading && !_basketsLoading;
        SaveButton.IsEnabled = !_linesLoading && !_basketsLoading && !_linesFailed && !_basketsFailed;
        if (!_linesFailed && !_basketsFailed)
            LoadStatusTextBlock.Text = _linesLoading ? "Carregando itens do projeto..."
                : _basketsLoading ? "Carregando cestas do item..." : string.Empty;
    }

    private string BuildNextManualName() => $"Manual {_manualBasketCount + 1:N0}";

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled) return;
        try
        {
            NewProjectName = NewProjectNameTextBox.Text.Trim();
            ExistingProjectId = NewProjectName.Length > 0
                ? null
                : (ProjectComboBox.SelectedItem as QuotationProject)?.Id;
            if (ExistingProjectId is null && NewProjectName.Length == 0)
            {
                throw new ArgumentException("Selecione um projeto ou informe o nome de um novo.");
            }

            var selectedLine = NewProjectName.Length > 0
                ? null
                : (LineComboBox.SelectedItem as LineChoice)?.Line;
            ExistingLineId = selectedLine?.Id;
            ExistingBasketId = selectedLine is null ? null : (BasketComboBox.SelectedItem as BasketChoice)?.Basket?.Id;
            BasketName = BasketNameTextBox.Text.Trim();
            if (BasketName.Length == 0)
            {
                throw new ArgumentException("Informe o nome da cesta manual.");
            }

            if (selectedLine is not null)
            {
                var line = selectedLine;
                Input = new QuotationLineInput(
                    line.Description,
                    line.RequestedQuantity,
                    line.RequestedUnit,
                    line.MinimumUnitPrice,
                    line.MaximumUnitPrice)
                {
                    Weights = line.Weights,
                    RequestedBasketSize = line.RequestedBasketSize
                };
            }
            else
            {
                var description = DescriptionTextBox.Text.Trim();
                var unit = UnitTextBox.Text.Trim();
                if (description.Length == 0)
                {
                    throw new ArgumentException("Informe a descrição do novo item.");
                }

                var quantity = 0m;
                if (!string.IsNullOrWhiteSpace(QuantityTextBox.Text) &&
                    (!decimal.TryParse(
                        QuantityTextBox.Text,
                        NumberStyles.Number,
                        CultureInfo.CurrentCulture,
                        out quantity) || quantity <= 0))
                {
                    throw new ArgumentException("Informe uma quantidade maior que zero ou deixe o campo vazio.");
                }

                if (!int.TryParse(BasketSizeTextBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var basketSize) ||
                    basketSize is < 3 or > 10)
                {
                    throw new ArgumentException("Informe de 3 a 10 preços para a cesta automática.");
                }

                var minimum = ParseOptionalPrice(MinimumPriceTextBox.Text, "preço mínimo");
                var maximum = ParseOptionalPrice(MaximumPriceTextBox.Text, "preço máximo");
                if (minimum < 0 || maximum < 0 || minimum is not null && maximum is not null && minimum > maximum)
                {
                    throw new ArgumentException("A faixa de preço é inválida.");
                }

                Input = new QuotationLineInput(description, quantity, unit, minimum, maximum)
                {
                    RequestedBasketSize = basketSize
                };
            }

            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Cesta manual", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static decimal? ParseOptionalPrice(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return decimal.TryParse(
            text,
            NumberStyles.Number | NumberStyles.AllowCurrencySymbol,
            CultureInfo.CurrentCulture,
            out var value)
            ? value
            : throw new ArgumentException($"O {label} não é válido.");
    }

    private Control[] NewLineControls =>
    [
        DescriptionTextBox,
        QuantityTextBox,
        UnitTextBox,
        BasketSizeTextBox,
        MinimumPriceTextBox,
        MaximumPriceTextBox
    ];

    private sealed record LineChoice(string Label, QuotationLine? Line);
    private sealed record BasketChoice(string Label, QuotationManualBasket? Basket);
}
