using System.Windows;
using System.Windows.Controls;
using PNCPKing.App.Services;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.Views;

public sealed class GitHubUpdateWindow : Window
{
    public GitHubUpdateWindow(GitHubUpdatePlan plan, GitHubReleaseNotes? releaseNotes = null)
    {
        Title = "Atualizar pelo GitHub";
        Width = 660;
        Height = plan.App is null ? 280 : 520;
        MinWidth = 380;
        MinHeight = 280;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MonitorAwareWindowBehavior.Attach(this);

        var panel = new Grid { Margin = new Thickness(20) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var summary = new TextBlock
        {
            Text = plan.AppStatus + "\n\n" + plan.PricesStatus + "\n\n" +
                $"Download: {plan.DownloadSize / (1024d * 1024):N1} MiB." +
                (plan.App is not null ? "\nO programa será fechado e reaberto para instalar a atualização." : "") +
                "\nCotações, cestas e configurações serão preservadas.",
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(new ScrollViewer
        {
            Content = summary,
            MaxHeight = 140,
            Margin = new Thickness(0, 0, 0, 12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });

        if (plan.App is not null)
        {
            var notesPanel = new StackPanel();
            notesPanel.Children.Add(new TextBlock
            {
                Text = "Novidades por versão",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            if (releaseNotes is { Releases.Count: > 0 })
            {
                foreach (var release in releaseNotes.Releases)
                {
                    notesPanel.Children.Add(new TextBlock
                    {
                        Text = "Versão " + release.Version,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 8, 0, 4)
                    });
                    notesPanel.Children.Add(new TextBlock { Text = release.Body, TextWrapping = TextWrapping.Wrap });
                }
            }
            else notesPanel.Children.Add(new TextBlock { Text = "Nenhuma descrição de versão disponível." });
            if (!string.IsNullOrWhiteSpace(releaseNotes?.Warning))
                notesPanel.Children.Add(new TextBlock
                {
                    Text = releaseNotes.Warning,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 0)
                });
            var scroll = new ScrollViewer
            {
                Content = notesPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 1);
            panel.Children.Add(scroll);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        var update = new Button { Content = "Atualizar agora", IsDefault = true, IsEnabled = plan.HasUpdates,
            Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        update.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(update);
        buttons.Children.Add(new Button { Content = "Cancelar", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) });
        Grid.SetRow(buttons, 2);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
