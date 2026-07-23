using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PNCPKing.App.Views;

internal static class DataGridColumnChooser
{
    public static void Show(Button button, DataGrid dataGrid)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Top,
            StaysOpen = true
        };
        foreach (var column in dataGrid.Columns)
        {
            var item = new MenuItem
            {
                Header = column.Header?.ToString() ?? "Coluna",
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
                Tag = column
            };
            item.Click += (_, _) =>
            {
                if (item.Tag is DataGridColumn selectedColumn)
                {
                    selectedColumn.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                }
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }
}
