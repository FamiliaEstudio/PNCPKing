using System.Windows;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.Views;

public enum DocumentAccessMode
{
    AllDocuments,
    RelevantPages
}

public partial class DocumentAccessWindow : Window
{
    public DocumentAccessWindow(string? suggestedReference)
    {
        InitializeComponent();
        ReferenceTextBox.Text = suggestedReference?.Trim() ?? string.Empty;
        ReferenceTextBox.CaretIndex = ReferenceTextBox.Text.Length;
    }

    public DocumentAccessMode SelectedMode { get; private set; }
    public IReadOnlyList<string> Expressions =>
        FlexiblePhraseMatcher.PrepareExpressions(
            ReferenceTextBox.Text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n'));

    private void AllDocuments_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = DocumentAccessMode.AllDocuments;
        DialogResult = true;
    }

    private void RelevantPages_Click(object sender, RoutedEventArgs e)
    {
        if (Expressions.Count == 0)
        {
            MessageBox.Show(
                "Informe ao menos uma expressão válida, usando uma linha para cada pesquisa.",
                "Páginas relevantes",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            ReferenceTextBox.Focus();
            return;
        }

        SelectedMode = DocumentAccessMode.RelevantPages;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
