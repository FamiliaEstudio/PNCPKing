using System.Globalization;
using System.Windows;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public partial class QuotationItemDocumentWindow : Window
{
    public QuotationItemDocumentWindow(QuotationLine line)
    {
        InitializeComponent();
        ItemNameText.Text = line.EffectiveDisplayName;
        CatmatBox.Text = line.CatmatCodeOverride;
        MinimumBox.Text = line.MinimumOrderQuantity?.ToString("0", CultureInfo.CurrentCulture) ?? string.Empty;
        CatalogHintText.Text = line.CatalogSelection is { Kind: CatalogKind.Catmat } selection
            ? $"Quando vazio, o Word usará o CATMAT vinculado: {selection.Code}. O vínculo será preservado."
            : "Quando vazio, o Word usará um CATMAT vinculado, se houver. O vínculo do catálogo será preservado.";
    }

    public string CatmatCodeOverride { get; private set; } = string.Empty;
    public decimal? MinimumOrderQuantity { get; private set; }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            decimal? minimum = null;
            if (!string.IsNullOrWhiteSpace(MinimumBox.Text))
            {
                if (!decimal.TryParse(MinimumBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value))
                    throw new ArgumentException("Informe uma quantidade mínima inteira positiva ou deixe vazia.");
                minimum = value;
            }
            QuotationWordExportOptions.ValidateMinimumOrderQuantity(minimum);
            CatmatCodeOverride = CatmatBox.Text.Trim();
            MinimumOrderQuantity = minimum;
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
