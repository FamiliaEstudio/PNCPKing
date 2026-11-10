extern alias AppUnderTest;

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PNCPKing.Core.Models;
using OrderWindow = AppUnderTest::PNCPKing.App.Views.QuotationOrganizationWindow;
using OrderRow = AppUnderTest::PNCPKing.App.Views.QuotationOrganizationWindow.ItemRow;

internal static partial class Program
{
    private static async Task CheckManualItemOrderAsync()
    {
        var project = new QuotationProject(Guid.NewGuid(), "Cinco mil itens", DateTimeOffset.Now, DateTimeOffset.Now)
            { ManualItemOrder = true };
        var lines = Enumerable.Range(1, 5000).Select(index => new QuotationLine
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Description = $"Item {index:D4} com descrição para verificar a sequência",
            DisplayOrder = index - 1, RequestedQuantity = 0, RequestedUnit = ""
        }).ToArray();
        var original = lines.Select(line => line.Id).ToArray();
        var window = new OrderWindow(project, lines);
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var grid = VisualChildren(window).OfType<DataGrid>().Single();
            var position = VisualChildren(window).OfType<TextBox>().Single();
            Require(window.Options.ManualOnly && !window.Options.AlphabeticalOnly,
                "Reabrir a ordem manual selecionou o modo errado.");
            Require(grid.EnableRowVirtualization && !grid.CanUserSortColumns &&
                    VisualChildren(grid).OfType<DataGridRow>().Count() < 100,
                "A lista manual materializou milhares de linhas ou permite ordenar apenas a visualização.");
            var last = (OrderRow)grid.Items[4999];
            grid.SelectedItem = last;
            position.Text = "12";
            FindChooserButton(window, "Mover").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.Options.ItemOrder![11] == last.Line.Id && last.Position == 12 && ReferenceEquals(grid.SelectedItem, last),
                "Mover um item distante não atualizou posição e seleção.");
            foreach (var invalid in new[] { "0", "5001", "1,5", "-1", "texto", "9999999999999999" })
            {
                position.Text = invalid;
                FindChooserButton(window, "Mover").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(last.Position == 12, "Uma posição inválida moveu o item.");
            }
            position.Text = "5000";
            FindChooserButton(window, "Mover").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.Options.ItemOrder!.SequenceEqual(original), "Mover para o final perdeu itens ou mudou sua ordem relativa.");
            position.Text = "1";
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(position)!, 0, System.Windows.Input.Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            position.RaiseEvent(enter);
            Require(enter.Handled && window.IsVisible && last.Position == 1,
                "Enter deveria mover o item sem aplicar nem fechar a janela.");
            FindChooserButton(window, "Subir").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(last.Position == 1, "Subir no início alterou indevidamente a ordem.");
            FindChooserButton(window, "Descer").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(last.Position == 2, "Descer não moveu o item para a posição seguinte.");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var third = (OrderRow)grid.Items[2];
            grid.ScrollIntoView(third); grid.UpdateLayout();
            var target = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(third);
            var over = ItemOrderDragEvent(target, last, DragDrop.DragOverEvent, target.ActualHeight - 2);
            target.RaiseEvent(over);
            Require(over.Effects == DragDropEffects.Move && target.BorderThickness.Bottom == 2,
                "O arrasto não indicou a posição de destino.");
            target.RaiseEvent(ItemOrderDragEvent(target, last, DragDrop.DropEvent, target.ActualHeight - 2));
            Require(last.Position == 3 && window.Options.ItemOrder![2] == last.Line.Id,
                "Soltar o item não o inseriu depois do destino.");
            Require(grid.Items.Cast<OrderRow>().Select(row => row.Position).SequenceEqual(Enumerable.Range(1, lines.Length)) &&
                    window.Options.ItemOrder!.Distinct().Count() == lines.Length,
                "A sequência contém numeração incorreta ou itens repetidos.");
            var draft = window.Options.ItemOrder!.ToArray();
            var modes = VisualChildren(window).OfType<RadioButton>().ToArray();
            modes[1].IsChecked = true;
            Require(!window.Options.ManualOnly && !window.Options.AlphabeticalOnly && window.Options.ItemOrder!.SequenceEqual(draft),
                "Grupos e cotas descartaram a sequência manual preparada na mesma janela.");
            modes[0].IsChecked = true;
            Require(window.Options.AlphabeticalOnly && window.Options.ItemOrder is null,
                "A ordem alfabética recebeu indevidamente a sequência manual.");
            modes[2].IsChecked = true;
            foreach (var (width, height) in new[] { (380d, 420d), (800d, 650d) })
            {
                window.Width = width; window.Height = height; window.UpdateLayout();
                var apply = FindChooserButton(window, "Aplicar ao projeto");
                var bounds = apply.TransformToAncestor(window).TransformBounds(new Rect(apply.RenderSize));
                Require(apply.IsVisible && bounds.Bottom <= window.ActualHeight && grid.ActualHeight > 30,
                    "Os controles ou a lista ficaram inacessíveis na janela reduzida.");
            }
        }
        finally { window.Close(); }
        Require(lines.Select(line => line.Id).SequenceEqual(original) && lines.Select(line => line.DisplayOrder).SequenceEqual(Enumerable.Range(0, lines.Length)),
            "Cancelar a edição alterou os itens originais.");
        Console.WriteLine("Manual item order: 5000 rows, virtualization, numeric moves, Enter, drag/drop, mode switching and small window passed.");
    }

    private static DragEventArgs ItemOrderDragEvent(DataGridRow target, OrderRow row, RoutedEvent routedEvent, double y)
    {
        var constructor = typeof(DragEventArgs).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var args = (DragEventArgs)constructor.Invoke([new DataObject(typeof(OrderRow), row),
            DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, target, new Point(5, y)]);
        args.RoutedEvent = routedEvent; args.Source = target;
        return args;
    }
}
