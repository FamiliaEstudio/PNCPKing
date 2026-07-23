using System.Windows;
using PNCPKing.Core.Search;

namespace PNCPKing.App.Views;

public partial class SweetCodeWindow : Window
{
    public SweetCodeWindow(bool enabled, IReadOnlyList<string> expressions)
    {
        InitializeComponent();
        EnabledCheckBox.IsChecked = enabled;
        CodesTextBox.Text = string.Join(Environment.NewLine, expressions);
        CodesTextBox.Focus();
    }

    public bool SweetCodesEnabled { get; private set; }
    public IReadOnlyList<string> Expressions { get; private set; } = [];

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        var errors = new List<string>();
        var lines = CodesTextBox.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var expression = lines[index].Trim();
            if (expression.Length == 0)
            {
                continue;
            }

            try
            {
                _ = SearchText.Parse(expression);
                var normalized = SearchText.Normalize(expression);
                if (unique.Add(normalized))
                {
                    result.Add(expression);
                }
            }
            catch (Exception exception)
            {
                errors.Add($"Linha {index + 1}: {exception.Message}");
            }
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, errors.Take(12)) +
                (errors.Count > 12 ? $"\n… e mais {errors.Count - 12} erro(s)." : string.Empty),
                "Sweet Codes inválidos",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SweetCodesEnabled = EnabledCheckBox.IsChecked == true;
        Expressions = result;
        DialogResult = true;
    }
}
