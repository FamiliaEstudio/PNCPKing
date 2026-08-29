using System.Globalization;
using System.Windows;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public partial class NewQuotationItemWindow : Window
{
    public NewQuotationItemWindow()
    {
        InitializeComponent();
        DescriptionTextBox.Focus();
    }

    public NewQuotationItemWindow(QuotationLine line)
        : this()
    {
        ArgumentNullException.ThrowIfNull(line);
        Title = "Editar dados do item";
        IntroTextBlock.Text =
            "Atualize quantidade e unidade. Deixe em branco quando a informação não estiver disponível.";
        DescriptionTextBox.Text = line.Description;
        DescriptionTextBox.IsReadOnly = true;
        DescriptionTextBox.IsTabStop = false;
        QuantityTextBox.Text = line.RequestedQuantity > 0
            ? line.RequestedQuantity.ToString("N4", CultureInfo.CurrentCulture)
            : string.Empty;
        UnitTextBox.Text = line.RequestedUnit;
        FooterTextBlock.Text =
            "Preços, cestas, pesquisas, catálogo e nome do item serão preservados.";
        AcceptButton.Content = "Salvar alterações";
        QuantityTextBox.Focus();
        QuantityTextBox.SelectAll();
    }

    public QuotationLineInput? Input { get; private set; }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var description = DescriptionTextBox.Text.Trim();
            var unit = UnitTextBox.Text.Trim();
            if (description.Length == 0)
            {
                throw new ArgumentException("Informe a descrição do item.");
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

            Input = new QuotationLineInput(description, quantity, unit, null, null)
            {
                Weights = AdequacyWeights.Default,
                RequestedBasketSize = 3
            };
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Novo item", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
