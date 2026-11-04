extern alias AppUnderTest;

using System.Windows;
using System.Windows.Controls;
using PNCPKing.Core.Models;

internal static partial class Program
{
    private static void CheckQuotationAiGuideOptions()
    {
        foreach (var importable in new[] { true, false })
        {
            var preview = new QuotationAiGuidePreview(Guid.NewGuid(), Guid.NewGuid(),
                [new(Guid.NewGuid(), "Item pendente com candidatos", "review", "Conferir faixa e justificativa.")
                    { CanImportCandidates = importable, CandidateCount = 80 }]);
            var window = new AppUnderTest::PNCPKing.App.Views.QuotationAiGuidePreviewWindow(preview);
            var root = (Grid)window.Content;
            var options = root.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<CheckBox>()).ToArray();
            var apply = root.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Button>())
                .Single(button => button.IsDefault);
            Require(options.Length == 2 && options.All(option => option.IsChecked == false),
                "As exceções de faixa e texto devem começar desmarcadas.");
            Require(apply.IsEnabled == importable,
                "Candidatos devem poder ser importados mesmo quando a cesta está pendente.");
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                window.Width = 650;
                window.Height = 580;
                window.UpdateLayout();
                options[0].IsChecked = true;
                options[1].IsChecked = true;
                Require(window.AllowPriceRangeExpansion && window.AllowTechnicalReview && apply.IsEnabled,
                    "Alterar opções deve permitir revalidar mesmo quando a prévia anterior estava bloqueada.");
                Require(apply.Content?.ToString() == "Revalidar e mostrar alterações",
                    "As opções alteradas exigem nova prévia antes de gravar.");
                Require(apply.IsVisible && apply.ActualHeight > 0 &&
                        apply.TranslatePoint(new Point(0, apply.ActualHeight), root).Y <= root.ActualHeight + 1,
                    "O botão de revalidar precisa caber na janela.");
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Require(window.ShowDialog() == true, "A prévia não devolveu as opções de revalidação.");
        }
    }
}
