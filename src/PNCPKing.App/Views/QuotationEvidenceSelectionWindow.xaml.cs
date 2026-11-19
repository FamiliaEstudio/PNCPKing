using System.Windows;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;

namespace PNCPKing.App.Views;

public partial class QuotationEvidenceSelectionWindow : Window
{
    private readonly QuotationProjectReport _report;

    public QuotationEvidenceSelectionWindow(QuotationProjectReport report)
    {
        _report = report;
        InitializeComponent();
    }

    public IReadOnlySet<Guid>? SelectedLineIds { get; private set; }

    private void SelectedItems_Checked(object sender, RoutedEventArgs e)
    {
        ItemsBox.Focus();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItemsButton.IsChecked == true)
        {
            try
            {
                SelectedLineIds = QuotationEvidenceSelection.Parse(ItemsBox.Text, _report);
            }
            catch (FormatException exception)
            {
                MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                ItemsBox.Focus();
                return;
            }
        }

        DialogResult = true;
    }
}
