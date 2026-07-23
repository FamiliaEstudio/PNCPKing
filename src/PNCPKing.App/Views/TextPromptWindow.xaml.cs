using System.Windows;

namespace PNCPKing.App.Views;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptTextBlock.Text = prompt;
        ValueTextBox.Text = initialValue;
        ValueTextBox.SelectAll();
        ValueTextBox.Focus();
    }

    public string Value { get; private set; } = string.Empty;

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        Value = ValueTextBox.Text.Trim();
        if (Value.Length == 0)
        {
            MessageBox.Show("Informe um valor.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
