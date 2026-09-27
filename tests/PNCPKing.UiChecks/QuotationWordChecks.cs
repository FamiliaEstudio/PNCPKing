extern alias AppUnderTest;

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Services;
using ExportWindow = AppUnderTest::PNCPKing.App.Views.QuotationWordExportWindow;
using ItemWindow = AppUnderTest::PNCPKing.App.Views.QuotationItemDocumentWindow;

internal static partial class Program
{
    private static async Task CheckQuotationWordAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var optionsWindow = new ExportWindow();
        try
        {
            optionsWindow.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!((StackPanel)optionsWindow.FindName("PercentagePanel")).IsVisible, "Percentual visível sem Registro de Preços.");
            ((ComboBox)optionsWindow.FindName("LocationBox")).SelectedIndex = 3;
            ((TextBox)optionsWindow.FindName("OtherItemBox")).Text = "7.1";
            ((ComboBox)optionsWindow.FindName("RegistrationBox")).SelectedIndex = 1;
            ((TextBox)optionsWindow.FindName("PercentageBox")).Text = "10";
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(((StackPanel)optionsWindow.FindName("PercentagePanel")).IsVisible, "Percentual oculto em Registro de Preços.");
            Require(((TextBlock)optionsWindow.FindName("PreviewText")).Text.Contains("no item 7.1 deste Termo"), "Prévia incorreta.");
            CaptureAppearance(optionsWindow, "Word-export-options", outputDirectory, 0);
            ((ComboBox)optionsWindow.FindName("LocationBox")).SelectedIndex = 2;
            Require(((TextBlock)optionsWindow.FindName("PreviewText")).Text.EndsWith("no Memorial Descritivo."), "Prévia do Memorial incorreta.");
        }
        finally { optionsWindow.Close(); }

        var project = new QuotationProject(Guid.NewGuid(), "Validação Word", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var line = new QuotationLine
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Description = "Café Premium — item importado",
            RequestedQuantity = 100, RequestedUnit = "pacote", CatmatCodeOverride = "001234", MinimumOrderQuantity = 7,
            AutomationRunId = Guid.NewGuid(),
            CatalogSelection = new() { Kind = CatalogKind.Catmat, Code = "987654", Description = "Catálogo" }
        };
        var editor = new ItemWindow(line);
        try
        {
            editor.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(((TextBox)editor.FindName("CatmatBox")).Text == "001234", "Editor perdeu zeros do código.");
            Require(((TextBox)editor.FindName("MinimumBox")).Text == "7", "Editor perdeu o mínimo nominal.");
            Require(((TextBox)editor.FindName("MinimumBox")).IsEnabled, "Mínimo de item importado não é editável.");
            CaptureAppearance(editor, "Word-item-details", outputDirectory, 0);
        }
        finally { editor.Close(); }

        var saveEditor = new ItemWindow(line);
        saveEditor.Loaded += (_, _) => saveEditor.Dispatcher.BeginInvoke(() =>
        {
            ((TextBox)saveEditor.FindName("CatmatBox")).Clear();
            ((TextBox)saveEditor.FindName("MinimumBox")).Clear();
            AppearanceChildren(saveEditor).OfType<Button>().Single(button => Equals(button.Content, "Salvar"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Require(saveEditor.ShowDialog() == true && saveEditor.CatmatCodeOverride.Length == 0 && saveEditor.MinimumOrderQuantity is null,
            "Limpar os valores não restaura o cálculo e o catálogo.");

        var exporter = new QuotationWordService();
        foreach (var includePrices in new[] { false, true })
        foreach (var count in new[] { 100, 1000 })
        {
            var items = Enumerable.Range(1, count).Select(index => new QuotationLineAnalysis(line with
            {
                Id = Guid.NewGuid(), Description = $"Item {index} — Café Premium com descrição longa de embalagem e apresentação, preservando maiúsculas e minúsculas",
                SelectionConfirmed = true, SelectedBasketKey = "selected"
            }, [], [new QuotationBasket { Key = "selected", References = [], AveragePrice = 12.349m, AdoptedPrice = 12.349m,
                MinimumPrice = 12.349m, MaximumPrice = 12.349m, MaximumDeviationPercent = 0, Score = 100 }], 0, 0, 0, 0, 0)).ToArray();
            var tableNumber = includePrices ? "9.1" : "1.1";
            var ticks = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            timer.Tick += (_, _) => ticks++;
            var watch = Stopwatch.StartNew();
            timer.Start();
            try
            {
                var path = Path.Combine(outputDirectory, $"Tabela-{tableNumber}-{count}.docx");
                if (includePrices) await exporter.ExportPriceTableAsync(path, new(project, items));
                else await exporter.ExportAsync(path, new(project, items),
                        new() { IsPriceRegistration = true, MinimumOrderPercentage = 10 });
            }
            finally { timer.Stop(); }
            Console.WriteLine($"Word {tableNumber}: {count} itens; {watch.Elapsed.TotalMilliseconds:N0} ms; {ticks} ticks de interface durante exportação; banco e rede não utilizados.");
            Require(ticks > 0, "A interface não respondeu durante a exportação.");
        }

        var groupId = Guid.NewGuid();
        var groupItems = Enumerable.Range(1, 3).Select(index => new QuotationLineAnalysis(line with
        {
            Id = Guid.NewGuid(), GroupId = index < 3 ? groupId : null, Description = $"Item {index} — Café Premium",
            SelectionConfirmed = true, SelectedBasketKey = "selected"
        }, [], [new QuotationBasket { Key = "selected", References = [], AveragePrice = 1000, AdoptedPrice = 1000,
            MinimumPrice = 1000, MaximumPrice = 1000, MaximumDeviationPercent = 0, Score = 100 }], 0, 0, 0, 0, 0)).ToArray();
        QuotationGroup[] groups = [new(groupId, project.Id, "Grupo", groupItems.Take(2).Select(item => item.Line.Id).ToArray())];
        project = project with { Organization = QuotationOrganization.Calculate(project, groupItems, groups) };
        await exporter.ExportAsync(Path.Combine(outputDirectory, "Tabela-1.1-grupos.docx"), new(project, groupItems) { Groups = groups },
            new() { IsPriceRegistration = true, MinimumOrderPercentage = 10 });
        await exporter.ExportPriceTableAsync(Path.Combine(outputDirectory, "Tabela-9.1-grupos.docx"), new(project, groupItems) { Groups = groups });
        Console.WriteLine("Janelas Word, edição de item importado, limpeza de valores e exportação com grupos: aprovados.");
    }
}
