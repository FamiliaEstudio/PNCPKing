extern alias AppUnderTest;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using AppChooser = AppUnderTest::PNCPKing.App.Views.ColumnChooserWindow;
using AppChooserRow = AppUnderTest::PNCPKing.App.Views.ColumnChooserRow;
using AppSettings = AppUnderTest::PNCPKing.App.Services.AppSettings;
using AppSettingsService = AppUnderTest::PNCPKing.App.Services.AppSettingsService;
using AppColumnLayouts = AppUnderTest::PNCPKing.App.Services.DataGridColumnLayoutService;

internal static partial class Program
{
    private static async Task CheckColumnChooserAsync()
    {
        static AppChooserRow[] Rows() =>
        [
            new("a", "Primeira", true),
            new("b", "Oculta", false),
            new("c", "Terceira", true),
            new("d", "Quarta", true)
        ];

        var cancelCount = 0;
        var cancelled = new AppChooser(Rows());
        cancelled.ApplyRequested += (_, _) => cancelCount++;
        try
        {
            cancelled.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var list = (ListBox)cancelled.FindName("ColumnsList");
            var positions = (ComboBox)cancelled.FindName("PositionComboBox");
            Require(!positions.IsEnabled, "A posição deve aguardar a seleção de uma coluna.");
            list.SelectedItem = cancelled.Rows[1];
            Require(positions.IsEnabled && positions.SelectedItem is 2,
                "A posição inicial da coluna oculta está incorreta.");
            Require(!cancelled.Rows[1].IsVisible, "Selecionar a coluna alterou sua visibilidade.");
            positions.SelectedItem = 4;
            Require(cancelled.Rows.Select(row => row.Key).SequenceEqual(["a", "c", "d", "b"]),
                "Mover a coluna oculta não atualizou a ordem do rascunho.");
            Require(cancelled.Rows.Select(row => row.Position).SequenceEqual([1, 2, 3, 4]),
                "A numeração das demais colunas não foi atualizada.");
            Require(positions.SelectedItem is 4 && ReferenceEquals(list.SelectedItem, cancelled.Rows[3]),
                "A coluna movida perdeu a seleção.");
            FindChooserButton(cancelled, "Cancelar").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(cancelCount == 0, "Cancelar aplicou o rascunho.");
        }
        finally
        {
            if (cancelled.IsVisible) cancelled.Close();
        }

        var applied = new AppChooser(Rows());
        IReadOnlyList<AppChooserRow>? draft = null;
        applied.ApplyRequested += (_, rows) => draft = rows;
        try
        {
            applied.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var list = (ListBox)applied.FindName("ColumnsList");
            var positions = (ComboBox)applied.FindName("PositionComboBox");
            list.SelectedItem = applied.Rows[1];
            positions.SelectedItem = 1;
            applied.Rows[0].IsVisible = true;
            FindChooserButton(applied, "Aplicar").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(draft is not null && draft.Select(row => row.Key).SequenceEqual(["b", "a", "c", "d"])
                    && draft[0].IsVisible,
                "Aplicar não entregou ordem e visibilidade juntas.");
        }
        finally
        {
            if (applied.IsVisible) applied.Close();
        }

        await CheckColumnLayoutPersistenceAsync();
    }

    private static async Task CheckColumnLayoutPersistenceAsync()
    {
        // Redirect this check's settings to a synthetic file; never touch the user's preferences.
        var settingsPath = Path.Combine(AppContext.BaseDirectory, $"layout-ui-{Guid.NewGuid():N}.json");
        var settingsService = new AppSettingsService();
        typeof(AppSettingsService).GetField("_settingsPath",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(settingsService, settingsPath);

        try
        {
            var layouts = new AppColumnLayouts(settingsService, new AppSettings("synthetic", true));
            var grid = CreateLayoutGrid();
            var owner = new Window { Content = grid, Width = 700, Height = 400 };
            try
            {
                layouts.Register("synthetic-layout", grid);
                owner.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                layouts.ShowChooser(owner, grid);
                var chooser = Application.Current.Windows.OfType<AppChooser>().Single();
                var list = (ListBox)chooser.FindName("ColumnsList");
                var position = (ComboBox)chooser.FindName("PositionComboBox");
                list.SelectedItem = chooser.Rows[3];
                position.SelectedItem = 1;
                Require(LayoutHeaders(grid).SequenceEqual(["a", "b", "c", "d"]),
                    "A grade foi alterada antes de aplicar o rascunho.");
                FindChooserButton(chooser, "Aplicar").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(LayoutHeaders(grid).SequenceEqual(["d", "a", "b", "c"]) && grid.FrozenColumnCount == 2,
                    "Aplicar não moveu a coluna através da área congelada.");
                Require(grid.Columns.Single(column => (string)column.Header == "b").Visibility == Visibility.Collapsed,
                    "A coluna oculta perdeu sua visibilidade ao mudar a ordem.");
                layouts.Unregister(grid);
                await layouts.FlushAsync();
            }
            finally { owner.Close(); }

            var restored = new AppColumnLayouts(settingsService, await settingsService.LoadAsync());
            var secondGrid = CreateLayoutGrid();
            restored.Register("synthetic-layout", secondGrid);
            Require(LayoutHeaders(secondGrid).SequenceEqual(["d", "a", "b", "c"]) && secondGrid.FrozenColumnCount == 2,
                "A ordem salva não foi recuperada ao reabrir a grade.");
            var secondOwner = new Window { Content = secondGrid, Width = 700, Height = 400 };
            try
            {
                secondOwner.Show();
                restored.ShowChooser(secondOwner, secondGrid);
                var chooser = Application.Current.Windows.OfType<AppChooser>().Single();
                FindChooserButton(chooser, "Restaurar padrão").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(LayoutHeaders(secondGrid).SequenceEqual(["a", "b", "c", "d"]) &&
                        secondGrid.FrozenColumnCount == 2,
                    "Restaurar padrão não recuperou a ordem original.");
                restored.Unregister(secondGrid);
                await restored.FlushAsync();
            }
            finally { secondOwner.Close(); }
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    private static DataGrid CreateLayoutGrid()
    {
        var grid = new DataGrid { AutoGenerateColumns = false };
        foreach (var key in new[] { "a", "b", "c", "d" })
        {
            var column = new DataGridTextColumn { Header = key, Binding = new Binding(key) };
            AppColumnLayouts.SetKey(column, key);
            if (key == "b") column.Visibility = Visibility.Collapsed;
            grid.Columns.Add(column);
        }
        grid.FrozenColumnCount = 2;
        return grid;
    }

    private static IEnumerable<string> LayoutHeaders(DataGrid grid) =>
        grid.Columns.OrderBy(column => column.DisplayIndex).Select(column => (string)column.Header);

    private static Button FindChooserButton(Window window, string label) =>
        VisualChildren(window).OfType<Button>().Single(button => (string)button.Content == label);
}
