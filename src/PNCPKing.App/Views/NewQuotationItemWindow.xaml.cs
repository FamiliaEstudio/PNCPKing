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

            if (!decimal.TryParse(
                    QuantityTextBox.Text,
                    NumberStyles.Number,
                    CultureInfo.CurrentCulture,
                    out var quantity) || quantity <= 0)
            {
                throw new ArgumentException("Informe uma quantidade maior que zero.");
            }

            if (unit.Length == 0)
            {
                throw new ArgumentException("Informe a unidade do item.");
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
