using System.Globalization;
using System.Windows;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public partial class QuotationWordExportWindow : Window
{
    public QuotationWordExportWindow()
    {
        InitializeComponent();
        UpdatePreview();
    }

    public QuotationWordExportOptions? Options { get; private set; }

    private void OptionsChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewText is null || OtherItemPanel is null || PercentagePanel is null) return;
        OtherItemPanel.Visibility = LocationBox.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        PercentagePanel.Visibility = RegistrationBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        PreviewText.Text = new QuotationWordExportOptions
        {
            DescriptionLocation = (QuotationDescriptionLocation)LocationBox.SelectedIndex,
            OtherItemNumber = string.IsNullOrWhiteSpace(OtherItemBox.Text) ? "…" : OtherItemBox.Text
        }.DescriptionSentence;
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            decimal? percentage = null;
            if (RegistrationBox.SelectedIndex == 1)
            {
                if (!decimal.TryParse(PercentageBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value))
                    throw new ArgumentException("Informe o percentual geral para esta exportação.");
                percentage = value;
            }
            var options = new QuotationWordExportOptions
            {
                DescriptionLocation = (QuotationDescriptionLocation)LocationBox.SelectedIndex,
                OtherItemNumber = OtherItemBox.Text.Trim(),
                IsPriceRegistration = RegistrationBox.SelectedIndex == 1,
                MinimumOrderPercentage = percentage
            };
            options.Validate();
            Options = options;
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
