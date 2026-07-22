using System.Windows;
using Microsoft.Win32;

namespace PNCPKing.App.Views;

public partial class DataFolderWindow : Window
{
    public DataFolderWindow(string initialFolder)
    {
        InitializeComponent();
        FolderTextBox.Text = initialFolder;
    }

    public string SelectedFolder { get; private set; } = string.Empty;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha a pasta do banco do PNCP King",
            InitialDirectory = Directory.Exists(FolderTextBox.Text) ? FolderTextBox.Text : null
        };
        if (dialog.ShowDialog() == true)
        {
            FolderTextBox.Text = dialog.FolderName;
        }
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FolderTextBox.Text))
        {
            MessageBox.Show("Escolha uma pasta válida.", "PNCP King", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            SelectedFolder = Path.GetFullPath(FolderTextBox.Text.Trim());
            Directory.CreateDirectory(SelectedFolder);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Pasta inválida", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
