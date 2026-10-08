using System.Windows;
using System.Windows.Controls;
using PNCPKing.App.Services;
using PNCPKing.Infrastructure.Services;

namespace PNCPKing.App.Views;

public sealed class GitHubUpdateWindow : Window
{
    private readonly CheckBox _updateProgram;
    private readonly CheckBox _updatePrices;
    public bool UpdateProgram => _updateProgram.IsEnabled && _updateProgram.IsChecked == true;
    public bool UpdatePrices => _updatePrices.IsEnabled && _updatePrices.IsChecked == true;

    public GitHubUpdateWindow(GitHubUpdatePlan plan, GitHubReleaseNotes? releaseNotes = null,
        bool pricesRequireProgramUpdate = false)
    {
        SetResourceReference(StyleProperty, "AppWindow");
        Title = "Atualizar pelo GitHub";
        Width = 660;
        Height = plan.App is null ? 380 : 600;
        MinWidth = 380;
        MinHeight = 380;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MonitorAwareWindowBehavior.Attach(this);

        var panel = new Grid { Margin = new Thickness(20) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var summary = new StackPanel();
        summary.Children.Add(new TextBlock
        {
            Text = plan.AppStatus + "\n\n" + plan.PricesStatus,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        _updateProgram = new CheckBox
        {
            Content = "Atualizar o programa", IsEnabled = plan.App is not null, IsChecked = plan.App is not null,
            Margin = new Thickness(0, 0, 0, 8)
        };
        _updatePrices = new CheckBox
        {
            Content = "Atualizar os preços", IsEnabled = plan.Package is not null, IsChecked = plan.Package is not null,
            Margin = new Thickness(0, 0, 0, 8)
        };
        summary.Children.Add(_updateProgram);
        summary.Children.Add(_updatePrices);
        var selectionSummary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        summary.Children.Add(selectionSummary);
        panel.Children.Add(new ScrollViewer
        {
            Content = summary,
            MaxHeight = 230,
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
        _updateProgram.Checked += (_, _) => RefreshSelection();
        _updateProgram.Unchecked += (_, _) => RefreshSelection();
        _updatePrices.Checked += (_, _) => RefreshSelection();
        _updatePrices.Unchecked += (_, _) => RefreshSelection();
        RefreshSelection();
        buttons.Children.Add(update);
        buttons.Children.Add(new Button { Content = "Cancelar", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) });
        Grid.SetRow(buttons, 2);
        panel.Children.Add(buttons);
        Content = panel;

        void RefreshSelection()
        {
            _updatePrices.IsEnabled = plan.Package is not null && (!pricesRequireProgramUpdate || UpdateProgram);
            if (!_updatePrices.IsEnabled) _updatePrices.IsChecked = false;
            update.IsEnabled = UpdateProgram || UpdatePrices;
            var size = (UpdateProgram ? plan.App!.File.Size : 0) + (UpdatePrices ? plan.Package!.Download.Size : 0);
            selectionSummary.Text = $"Download selecionado: {size / (1024d * 1024):N1} MiB." +
                (UpdateProgram ? "\nO programa será atualizado e reiniciado." +
                    (UpdatePrices ? " Depois os preços serão baixados e importados automaticamente." : "") :
                    UpdatePrices ? "\nOs preços serão importados sem reiniciar o programa." : "") +
                (plan.Package is not null && pricesRequireProgramUpdate && !UpdateProgram
                    ? "\nEste pacote de preços exige também a atualização do programa." : "") +
                "\nCotações, cestas e configurações serão preservadas.";
        }
    }
}
