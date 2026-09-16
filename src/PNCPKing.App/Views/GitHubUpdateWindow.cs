using System.Windows;
using System.Windows.Controls;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.Views;

public sealed class GitHubUpdateWindow : Window
{
    public GitHubUpdateWindow(GitHubUpdatePlan plan)
    {
        Title = "Atualizar pelo GitHub";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = plan.AppStatus + "\n\n" + plan.PricesStatus + "\n\n" +
                $"Download: {plan.DownloadSize / (1024d * 1024):N1} MiB." +
                (plan.App is not null ? "\nO programa será fechado e reaberto para instalar a atualização." : "") +
                "\nCotações, cestas e configurações serão preservadas.",
            TextWrapping = TextWrapping.Wrap
        });
        CheckBox? adoption = null;
        if (plan.ReplaceBase)
        {
            adoption = new CheckBox
            {
                Margin = new Thickness(0, 16, 0, 0),
                Content = new TextBlock
                {
                    Text = "Concordo em adotar a base do GitHub. Os pacotes da linhagem anterior deixarão de ser compatíveis; meus dados particulares e versões oficiais mais recentes serão preservados.",
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 475
                }
            };
            panel.Children.Add(adoption);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0) };
        var update = new Button { Content = "Atualizar agora", IsDefault = true, IsEnabled = plan.HasUpdates && !plan.ReplaceBase,
            Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        update.Click += (_, _) => DialogResult = true;
        if (adoption is not null)
        {
            adoption.Checked += (_, _) => update.IsEnabled = plan.HasUpdates;
            adoption.Unchecked += (_, _) => update.IsEnabled = false;
        }
        buttons.Children.Add(update);
        buttons.Children.Add(new Button { Content = "Cancelar", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) });
        panel.Children.Add(buttons);
        Content = panel;
    }
}
