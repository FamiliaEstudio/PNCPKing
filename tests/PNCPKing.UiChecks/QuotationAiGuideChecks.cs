extern alias AppUnderTest;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PNCPKing.Core.Models;

internal static partial class Program
{
    private static void CheckQuotationAiGuideOptions()
    {
        foreach (var importable in new[] { true, false })
        {
            var preview = new QuotationAiGuidePreview(Guid.NewGuid(), Guid.NewGuid(),
                [new(Guid.NewGuid(), "Item pendente com candidatos", "review", "Conferir faixa e justificativa.")
                    { CanImportCandidates = importable, CandidateCount = 80 },
                 new(Guid.NewGuid(), "Outro item pendente", "insufficient", "Faltam três preços comparáveis.")]);
            var window = new AppUnderTest::PNCPKing.App.Views.QuotationAiGuidePreviewWindow(preview);
            var root = (Grid)window.Content;
            var options = FindGuideControl<StackPanel>(root, "GuideOptions").Children.OfType<CheckBox>().ToArray();
            var apply = FindGuideControl<WrapPanel>(root, "GuideFooter").Children.OfType<Button>().Single(button => button.IsDefault);
            Require(options.Length == 2 && options.All(option => option.IsChecked == false),
                "As exceções de faixa e texto devem começar desmarcadas.");
            Require(apply.IsEnabled == importable,
                "Candidatos devem poder ser importados mesmo quando a cesta está pendente.");
            if (importable)
                Require(GuideButtonText(apply) == "Importar somente candidatos",
                    "Uma prévia sem cestas prontas deve informar que só importará candidatos.");
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                window.Width = 650;
                window.Height = 580;
                window.UpdateLayout();
                CheckGuideDetailSelection(root, preview);
                options[0].IsChecked = true;
                options[1].IsChecked = true;
                Require(window.AllowPriceRangeExpansion && window.AllowTechnicalReview && apply.IsEnabled,
                    "Alterar opções deve permitir revalidar mesmo quando a prévia anterior estava bloqueada.");
                Require(GuideButtonText(apply) == "Revalidar e mostrar alterações",
                    "As opções alteradas exigem nova prévia antes de gravar.");
                Require(apply.IsVisible && apply.ActualHeight > 0 &&
                        apply.TranslatePoint(new Point(0, apply.ActualHeight), root).Y <= root.ActualHeight + 1,
                    "O botão de revalidar precisa caber na janela.");
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Require(window.ShowDialog() == true, "A prévia não devolveu as opções de revalidação.");
        }

        var result = new QuotationAiGuidePreview(Guid.NewGuid(), Guid.NewGuid(),
            [new(Guid.NewGuid(), "Item aplicado", "applied", "Cesta aplicada e candidatos preservados."),
             new(Guid.NewGuid(), "Item para revisar", "review", "O preço selecionado exige revisão da unidade.")]);
        var resultWindow = new AppUnderTest::PNCPKing.App.Views.QuotationAiGuidePreviewWindow(result, allowApply: false);
        resultWindow.Dispatcher.BeginInvoke(new Action(() =>
        {
            var root = (Grid)resultWindow.Content;
            resultWindow.Width = 500;
            resultWindow.Height = 320;
            resultWindow.UpdateLayout();
            CheckGuideDetailSelection(root, result);
            var resultButtons = FindGuideControl<WrapPanel>(root, "GuideFooter").Children.OfType<Button>().ToArray();
            Require(resultButtons.Length == 1 && resultButtons[0].IsCancel && GuideButtonText(resultButtons[0]) == "Fechar",
                "O resultado da importação não deve oferecer reaplicação pela janela de conferência.");
            RequireGuideButtonAccessible(resultWindow, root, resultButtons[0]);
            resultWindow.Close();
        }));
        resultWindow.ShowDialog();
        CheckQuotationAiGuideSmallWindows();
    }

    private static void CheckGuideDetailSelection(Grid root, QuotationAiGuidePreview preview)
    {
        var grid = FindGuideControl<DataGrid>(root, "GuideItems");
        var detail = FindGuideControl<TextBox>(root, "GuideDetails");
        Require(detail.IsReadOnly, "A mensagem da prévia deve permanecer somente para leitura.");
        // Selecting real rows binds to the immutable anonymous Message property.
        // Constructing the window without selecting any row did not catch TwoWay.
        for (var index = 0; index < preview.Items.Count; index++)
        {
            grid.SelectedIndex = index;
            detail.GetBindingExpression(TextBox.TextProperty)!.UpdateTarget();
            Require(detail.Text == preview.Items[index].Message,
                "Selecionar a linha não atualizou o detalhe da mensagem do guia.");
        }
        grid.SelectedIndex = -1;
        detail.GetBindingExpression(TextBox.TextProperty)!.UpdateTarget();
        Require(string.IsNullOrEmpty(detail.Text), "Retirar a seleção manteve a mensagem de outro item.");
    }

    private static void CheckQuotationAiGuideSmallWindows()
    {
        var message = string.Join(" ", Enumerable.Repeat(
            "Confira a unidade, a faixa de preços e a justificativa do item no guia antes de formar uma cesta.", 12));
        foreach (var hasReady in new[] { false, true })
        foreach (var (width, height, scale) in new[] { (500d, 320d, 1d), (650d, 480d, 1d), (500d, 320d, 1.5d), (650d, 480d, 1.5d) })
        {
            var preview = new QuotationAiGuidePreview(Guid.NewGuid(), Guid.NewGuid(),
                Enumerable.Range(1, 423).Select(index => new QuotationAiGuideItemResult(Guid.NewGuid(),
                    $"Item {index} com descrição extensa para conferência",
                    hasReady && index == 1 ? "ready" : index % 2 == 0 ? "review" : "insufficient", message)
                    { CanImportCandidates = true, CandidateCount = 240 }).ToArray());
            var initialLabel = hasReady ? "Importar preços e aplicar cestas válidas" : "Importar somente candidatos";
            var window = new AppUnderTest::PNCPKing.App.Views.QuotationAiGuidePreviewWindow(preview);
            var root = (Grid)window.Content;
            // A layout transform exercises the smaller effective area at 150% density;
            // this is deterministic in CI and does not change the OS monitor DPI.
            root.LayoutTransform = new ScaleTransform(scale, scale);
            var body = FindGuideControl<ScrollViewer>(root, "GuideBody");
            var grid = FindGuideControl<DataGrid>(root, "GuideItems");
            var detail = FindGuideControl<TextBox>(root, "GuideDetails");
            var footer = FindGuideControl<WrapPanel>(root, "GuideFooter");
            var apply = footer.Children.OfType<Button>().Single(button => button.IsDefault);
            var options = FindGuideControl<StackPanel>(root, "GuideOptions").Children.OfType<CheckBox>().ToArray();
            Exception? failure = null;
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    window.Width = width;
                    window.Height = height;
                    grid.SelectedIndex = 0;
                    detail.GetBindingExpression(TextBox.TextProperty)!.UpdateTarget();
                    window.UpdateLayout();
                    Require(detail.Text == message, "A mensagem extensa selecionada não chegou ao detalhe.");
                    Require(FindGuideControl<TextBlock>(root, "GuideSummary").Text.Contains(hasReady
                            ? "1 cesta(s) pronta(s)" : "Nenhuma cesta da IA será aplicada."),
                        "A janela deve distinguir cestas prontas de candidatos sem nenhuma cesta aprovada.");
                    Require(GuideButtonText(apply) == initialLabel && apply.IsEnabled,
                        "O botão de importação deve refletir a existência de cestas prontas.");
                    Require(options.All(option => option.IsChecked == false),
                        "Uma janela pequena não deve habilitar exceções automaticamente.");
                    Require(body.ViewportHeight > 0 && body.ScrollableHeight > 0,
                        $"O corpo da prévia precisa rolar em {width}×{height}, escala {scale}.");
                    Require(grid.ActualHeight >= 180 && grid.EnableRowVirtualization &&
                            VirtualizingPanel.GetIsVirtualizing(grid) && VisualChildren(grid).OfType<DataGridRow>().Count() < preview.Items.Count,
                        "A tabela precisa manter altura útil e virtualização com centenas de itens.");
                    foreach (var button in footer.Children.OfType<Button>())
                        RequireGuideButtonAccessible(window, root, button);

                    foreach (var option in options)
                    {
                        option.BringIntoView(new Rect(0, 0, 20, 20));
                        window.UpdateLayout();
                        var checkPosition = option.TranslatePoint(new Point(8, 8), body);
                        Require(checkPosition.Y >= 0 && checkPosition.Y < body.ViewportHeight,
                            "A rolagem deve permitir alcançar cada opção de revisão.");
                    }
                    Require(body.VerticalOffset > 0, "As opções precisam ser alcançáveis pela rolagem do corpo.");
                    foreach (var button in footer.Children.OfType<Button>())
                        RequireGuideButtonAccessible(window, root, button);

                    options[0].IsChecked = true;
                    window.UpdateLayout();
                    Require(GuideButtonText(apply) == "Revalidar e mostrar alterações",
                        "Alterar a faixa deve continuar exigindo revalidação.");
                    RequireGuideButtonAccessible(window, root, apply);
                    options[0].IsChecked = false;
                    Require(GuideButtonText(apply) == initialLabel,
                        "Desfazer a opção deve restaurar o rótulo correspondente à prévia atual.");
                    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception error)
                {
                    failure = error;
                    window.Close();
                }
            }));
            var accepted = window.ShowDialog();
            if (failure is not null)
                throw new InvalidOperationException($"Prévia do guia em {width}×{height}, escala {scale}, cestas prontas: {hasReady}.", failure);
            Require(accepted == true && !window.AllowPriceRangeExpansion && !window.AllowTechnicalReview,
                "Confirmar a importação deve devolver a confirmação sem habilitar exceções.");
        }
    }

    private static T FindGuideControl<T>(Grid root, string name) where T : FrameworkElement =>
        LogicalTreeHelper.FindLogicalNode(root, name) as T ?? throw new InvalidOperationException($"Controle do guia não encontrado: {name}.");

    private static string? GuideButtonText(Button button) => button.Content is TextBlock text ? text.Text : button.Content?.ToString();

    private static void RequireGuideButtonAccessible(Window window, Grid root, Button button)
    {
        var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
        Require(button.IsVisible && bounds.Width > 0 && bounds.Height > 0 &&
                bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1,
            $"O botão '{GuideButtonText(button)}' deve caber inteiramente na janela.");
        var center = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), window);
        var hit = window.InputHitTest(center) as DependencyObject;
        Require(hit is not null && (ReferenceEquals(hit, button) || button.IsAncestorOf(hit)),
            $"O botão '{GuideButtonText(button)}' deve permanecer acessível ao clique.");
    }
}
