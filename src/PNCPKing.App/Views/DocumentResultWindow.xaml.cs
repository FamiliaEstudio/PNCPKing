using System.Windows;

namespace PNCPKing.App.Views;

public enum DocumentResultAction
{
    Close,
    OpenPdf,
    OpenFolder
}

public partial class DocumentResultWindow : Window
{
    public DocumentResultWindow(
        string filePath,
        string heading,
        string summary,
        IReadOnlyList<string> warnings)
    {
        InitializeComponent();
        FilePath = filePath;
        Heading = heading;
        Summary = summary;
        Warnings = string.Join(Environment.NewLine, warnings);
        WarningVisibility = warnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DataContext = this;
    }

    public string FilePath { get; }
    public string Heading { get; }
    public string Summary { get; }
    public string Warnings { get; }
    public Visibility WarningVisibility { get; }
    public DocumentResultAction SelectedAction { get; private set; }

    private void OpenPdf_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = DocumentResultAction.OpenPdf;
        DialogResult = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = DocumentResultAction.OpenFolder;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = DocumentResultAction.Close;
        DialogResult = false;
    }
}
