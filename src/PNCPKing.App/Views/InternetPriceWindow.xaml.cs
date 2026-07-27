using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;

namespace PNCPKing.App.Views;

public partial class InternetPriceWindow : Window
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const int PriceHotkeyId = 0x504B11;
    private const int TaxIdHotkeyId = 0x504B12;
    private const uint Vk1 = 0x31;
    private const uint Vk2 = 0x32;

    private readonly IWindowCaptureService _capture;
    private readonly IInternetEvidenceStore _evidenceStore;
    private readonly IReadOnlyList<decimal> _currentPrices;
    private HwndSource? _source;
    private nint _handle;
    private bool _capturing;
    private EvidenceImageDescriptor? _priceImage;
    private EvidenceImageDescriptor? _taxIdImage;
    private DateTimeOffset _capturedAt;
    private readonly Guid _draftId;
    private readonly Guid _lineId;
    private readonly Guid? _basketId;
    private readonly DateTimeOffset _createdAt;

    public InternetPriceWindow(
        InternetPriceDraft draft,
        IReadOnlyList<decimal> currentPrices,
        IWindowCaptureService capture,
        IInternetEvidenceStore evidenceStore)
    {
        ArgumentNullException.ThrowIfNull(draft);
        InitializeComponent();
        _capture = capture;
        _evidenceStore = evidenceStore;
        _currentPrices = currentPrices;
        _draftId = draft.Id;
        _lineId = draft.LineId;
        _basketId = draft.BasketId;
        _createdAt = draft.CreatedAt == default ? DateTimeOffset.UtcNow : draft.CreatedAt;
        _capturedAt = draft.CapturedAt == default ? DateTimeOffset.UtcNow : draft.CapturedAt;
        _priceImage = draft.PriceImage;
        _taxIdImage = draft.TaxIdImage;

        SourceUrlTextBox.Text = draft.SourceUrl;
        UnitPriceTextBox.Text = draft.UnitPrice?.ToString("N4", CultureInfo.CurrentCulture) ?? string.Empty;
        DescriptionTextBox.Text = draft.Description;
        SupplierTextBox.Text = draft.SupplierName;
        TaxIdTextBox.Text = draft.SupplierTaxId;
        Loaded += async (_, _) => await LoadExistingImagesAsync().ConfigureAwait(true);
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        UpdateState();
    }

    public InternetPriceDraft? ResultDraft { get; private set; }
    public bool CompleteRequested { get; private set; }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
        if (!RegisterHotKey(_handle, PriceHotkeyId, ModAlt, Vk1) ||
            !RegisterHotKey(_handle, TaxIdHotkeyId, ModAlt, Vk2))
        {
            ValidationText.Text =
                "Os atalhos Alt+1/Alt+2 já estão em uso. Feche outra janela de preço e tente novamente.";
            ValidationText.Foreground = Brushes.DarkRed;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_handle != 0)
        {
            _ = UnregisterHotKey(_handle, PriceHotkeyId);
            _ = UnregisterHotKey(_handle, TaxIdHotkeyId);
        }

        _source?.RemoveHook(WndProc);
    }

    private nint WndProc(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message != WmHotkey)
        {
            return 0;
        }

        handled = true;
        if (!_capturing)
        {
            _ = CaptureAsync(wParam.ToInt32() == PriceHotkeyId);
        }

        return 0;
    }

    private async Task CaptureAsync(bool price)
    {
        _capturing = true;
        try
        {
            var captured = await _capture.CaptureForegroundWindowAsync(_handle).ConfigureAwait(true);
            var descriptor = await _evidenceStore.SavePngAsync(
                captured.PngBytes,
                captured.PixelWidth,
                captured.PixelHeight).ConfigureAwait(true);
            _capturedAt = DateTimeOffset.UtcNow;
            if (price)
            {
                _priceImage = descriptor;
                PriceImagePreview.Source = CreateBitmap(captured.PngBytes);
                PriceImageStatus.Text =
                    $"{captured.WindowTitle} · {captured.PixelWidth:N0}×{captured.PixelHeight:N0}";
            }
            else
            {
                _taxIdImage = descriptor;
                TaxIdImagePreview.Source = CreateBitmap(captured.PngBytes);
                TaxIdImageStatus.Text =
                    $"{captured.WindowTitle} · {captured.PixelWidth:N0}×{captured.PixelHeight:N0}";
            }

            UpdateState();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Capturar evidência",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _capturing = false;
        }
    }

    private async Task LoadExistingImagesAsync()
    {
        if (_priceImage is not null)
        {
            try
            {
                PriceImagePreview.Source = CreateBitmap(
                    await _evidenceStore.ReadVerifiedAsync(_priceImage).ConfigureAwait(true));
                PriceImageStatus.Text =
                    $"{_priceImage.PixelWidth:N0}×{_priceImage.PixelHeight:N0}";
            }
            catch (Exception exception)
            {
                PriceImageStatus.Text = exception.Message;
                _priceImage = null;
            }
        }

        if (_taxIdImage is not null)
        {
            try
            {
                TaxIdImagePreview.Source = CreateBitmap(
                    await _evidenceStore.ReadVerifiedAsync(_taxIdImage).ConfigureAwait(true));
                TaxIdImageStatus.Text =
                    $"{_taxIdImage.PixelWidth:N0}×{_taxIdImage.PixelHeight:N0}";
            }
            catch (Exception exception)
            {
                TaxIdImageStatus.Text = exception.Message;
                _taxIdImage = null;
            }
        }

        UpdateState();
    }

    private void Input_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateState();

    private void DeletePriceImage_Click(object sender, RoutedEventArgs e)
    {
        _priceImage = null;
        PriceImagePreview.Source = null;
        PriceImageStatus.Text = "Não capturado";
        UpdateState();
    }

    private void DeleteTaxIdImage_Click(object sender, RoutedEventArgs e)
    {
        _taxIdImage = null;
        TaxIdImagePreview.Source = null;
        TaxIdImageStatus.Text = "Não capturado";
        UpdateState();
    }

    private void SaveDraft_Click(object sender, RoutedEventArgs e)
    {
        ResultDraft = BuildDraft(requireComplete: false);
        if (ResultDraft is null)
        {
            return;
        }

        CompleteRequested = false;
        DialogResult = true;
    }

    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        ResultDraft = BuildDraft(requireComplete: true);
        if (ResultDraft is null)
        {
            return;
        }

        CompleteRequested = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private InternetPriceDraft? BuildDraft(bool requireComplete)
    {
        decimal? price = null;
        if (!string.IsNullOrWhiteSpace(UnitPriceTextBox.Text))
        {
            if (!decimal.TryParse(
                    UnitPriceTextBox.Text,
                    NumberStyles.Number | NumberStyles.AllowCurrencySymbol,
                    CultureInfo.CurrentCulture,
                    out var parsed))
            {
                ShowValidation("Informe um preço unitário válido.");
                return null;
            }

            price = parsed;
        }

        var draft = new InternetPriceDraft
        {
            Id = _draftId,
            LineId = _lineId,
            BasketId = _basketId,
            SourceUrl = SourceUrlTextBox.Text.Trim(),
            UnitPrice = price,
            Description = DescriptionTextBox.Text.Trim(),
            SupplierName = SupplierTextBox.Text.Trim(),
            SupplierTaxId = TaxIdTextBox.Text.Trim(),
            PriceImage = _priceImage,
            TaxIdImage = _taxIdImage,
            CapturedAt = _capturedAt,
            CreatedAt = _createdAt,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        if (requireComplete && !draft.IsComplete)
        {
            ShowValidation(
                "Para inserir na cesta, informe URL http/https, preço positivo, descrição, empresa, " +
                "CNPJ válido e capture os dois prints.");
            return null;
        }

        return draft;
    }

    private void UpdateState()
    {
        if (!IsInitialized)
        {
            return;
        }

        var draft = BuildDraftForPreview();
        CompleteButton.IsEnabled = draft.IsComplete;
        var prices = _currentPrices.ToList();
        if (draft.UnitPrice is > 0)
        {
            prices.Add(draft.UnitPrice.Value);
        }

        if (prices.Count == 0)
        {
            AverageText.Text = "A cesta ainda não possui preços.";
        }
        else
        {
            decimal? currentAverage = _currentPrices.Count == 0
                ? null
                : _currentPrices.Average();
            var projectedAverage = prices.Average();
            var newDeviation = draft.UnitPrice is > 0 && projectedAverage > 0
                ? Math.Abs(draft.UnitPrice.Value - projectedAverage) / projectedAverage * 100m
                : 0m;
            var maximumDeviation = projectedAverage > 0
                ? prices.Max(value => Math.Abs(value - projectedAverage) / projectedAverage * 100m)
                : 0m;
            AverageText.Text =
                $"Média atual: {(currentAverage?.ToString("C4") ?? "—")} · " +
                $"média projetada: {projectedAverage:C4} · " +
                $"desvio do novo preço: {newDeviation:N2}% · máximo: {maximumDeviation:N2}%";
            AverageText.Foreground = maximumDeviation <= 25m ? Brushes.DarkGreen : Brushes.DarkRed;
        }

        ValidationText.Text = draft.IsComplete
            ? "Cadastro completo. O preço pode ser inserido na cesta manual."
            : "Rascunho: ainda faltam dados válidos ou um dos dois prints.";
        ValidationText.Foreground = draft.IsComplete ? Brushes.DarkGreen : Brushes.DarkSlateGray;
    }

    private InternetPriceDraft BuildDraftForPreview()
    {
        _ = decimal.TryParse(
            UnitPriceTextBox.Text,
            NumberStyles.Number | NumberStyles.AllowCurrencySymbol,
            CultureInfo.CurrentCulture,
            out var price);
        return new InternetPriceDraft
        {
            Id = _draftId,
            LineId = _lineId,
            BasketId = _basketId,
            SourceUrl = SourceUrlTextBox.Text.Trim(),
            UnitPrice = price > 0 ? price : null,
            Description = DescriptionTextBox.Text.Trim(),
            SupplierName = SupplierTextBox.Text.Trim(),
            SupplierTaxId = TaxIdTextBox.Text.Trim(),
            PriceImage = _priceImage,
            TaxIdImage = _taxIdImage,
            CapturedAt = _capturedAt,
            CreatedAt = _createdAt,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Foreground = Brushes.DarkRed;
    }

    private static BitmapImage CreateBitmap(ReadOnlyMemory<byte> bytes)
    {
        var image = new BitmapImage();
        using var stream = new MemoryStream(bytes.ToArray());
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
