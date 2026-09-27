extern alias AppUnderTest;

using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;

internal static partial class Program
{
    private static Application CreateUiApplication()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Exercise the compiled production theme without starting App.OnStartup or opening user data.
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/PNCPKing;component/Resources/Theme.xaml", UriKind.Relative)
        });
        return app;
    }

    private static void LoadBaselineResources(string sourceDirectory)
    {
        // Allows before/after images from an exported pre-theme XAML tree, using its original styles.
        var root = XDocument.Load(Path.Combine(sourceDirectory, "App.xaml")).Root!;
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(wpf + "ResourceDictionary", root.Attributes().Where(a => a.IsNamespaceDeclaration),
            root.Element(wpf + "Application.Resources")!.Elements());
        Application.Current.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }

    private static async Task CheckAppearanceAsync(string sourceDirectory, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var reports = new List<object>();
        foreach (var path in Directory.GetFiles(Path.Combine(sourceDirectory, "Views"), "*.xaml").Order())
        {
            var name = Path.GetFileNameWithoutExtension(path);
            // Render the real layout without its code-behind, constructors or service lifecycle.
            // Interaction checks elsewhere instantiate production controls and use synthetic services.
            var started = Stopwatch.GetTimestamp();
            var window = LoadAppearanceWindow(path);
            window.DataContext = AppearanceData();
            try
            {
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                reports.Add(CaptureAppearance(window, name, outputDirectory, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                foreach (var tabs in AppearanceChildren(window).OfType<TabControl>().Where(t => t.IsVisible).ToArray())
                {
                    for (var index = 1; index < tabs.Items.Count; index++)
                    {
                        tabs.SelectedIndex = index;
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        reports.Add(CaptureAppearance(window, $"{name}-tab-{index}", outputDirectory, 0));
                    }
                    tabs.SelectedIndex = 0;
                }
                if (name == "MainWindow")
                {
                    var content = (FrameworkElement)window.Content;
                    // LayoutTransform approximates logical space at DPI scales; this does not
                    // replace testing on a monitor actually configured to those DPI settings.
                    foreach (var (width, height) in new[] { (1366d, 768d), (1920d, 1080d), (800d, 520d) })
                    foreach (var scale in new[] { 1d, 1.25, 1.5 })
                    {
                        window.Width = width;
                        window.Height = height;
                        content.LayoutTransform = new ScaleTransform(scale, scale);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        reports.Add(CaptureAppearance(window, $"MainWindow-{width}x{height}-{scale * 100}", outputDirectory, 0));
                    }
                }
            }
            finally { window.Close(); }
        }
        var plan = new GitHubUpdatePlan(
            new AppUpdateManifest(1, "1.2.16", "win-x64", SqliteContractRepository.CurrentSchemaVersion,
                new ReleaseFile("PNCPKing.exe", 1024, new string('a', 64))), null,
            "Nova versão disponível.", "Preços em dia.");
        var project = new QuotationProject(Guid.NewGuid(), "Cotação de demonstração", DateTimeOffset.Now, DateTimeOffset.Now);
        var codeWindows = new Window[]
        {
            new PNCPKing.App.Views.GitHubUpdateWindow(plan,
                new GitHubReleaseNotes([new("1.2.16", "Aprimoramentos da apresentação das telas.\nDados fictícios para validação visual.")], null)),
            new PNCPKing.App.Views.QuotationGroupsWindow(new(project, []))
        };
        foreach (var window in codeWindows)
        {
            try
            {
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                reports.Add(CaptureAppearance(window, window.GetType().Name, outputDirectory, 0));
            }
            finally { window.Close(); }
        }
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "appearance.json"),
            JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Appearance: {reports.Count} layouts rendered using synthetic data.");
    }

    private static async Task CheckThemeControlsAsync()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        var input = new TextBox { Text = "Edição preservada" };
        var info = new TextBox { Text = "Informação selecionável", Style = (Style)Application.Current.FindResource("SelectableInfo") };
        var accept = new Button { Content = "_Aplicar", IsDefault = true };
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        var search = new Button { Content = "Pesquisar", Style = (Style)Application.Current.FindResource("SearchButton") };
        var body = new TextBlock { Text = "O conteúdo da aba mantém a cor de texto normal." };
        var tabs = new TabControl { Items = { new TabItem { Header = "Aba selecionada", Content = body } } };
        foreach (var child in new UIElement[] { input, info, accept, cancel, search, tabs }) panel.Children.Add(child);
        var window = new Window { Width = 560, Height = 400, Content = panel };
        window.SetResourceReference(FrameworkElement.StyleProperty, "AppWindow");
        var accepts = 0;
        var cancels = 0;
        accept.Click += (_, _) => accepts++;
        cancel.Click += (_, _) => cancels++;
        try
        {
            window.Show();
            window.Activate();
            input.Focus();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!search.IsDefault && accept.IsDefault && cancel.IsCancel,
                "O destaque visual alterou os papéis de Enter/Escape.");
            AccessKeyManager.ProcessKey(PresentationSource.FromVisual(window), "\r", false);
            AccessKeyManager.ProcessKey(PresentationSource.FromVisual(window), "\x1b", false);
            Require(accepts == 1 && cancels == 1, "Enter/Escape não acionaram os botões originais.");
            Require(((SolidColorBrush)body.Foreground).Color == ((SolidColorBrush)window.Foreground).Color,
                $"A cor do cabeçalho da aba vazou para o conteúdo: {body.Foreground}, janela: {window.Foreground}.");
            Require(info.IsReadOnly && !info.IsUndoEnabled && info.BorderThickness == new Thickness(0) &&
                    info.Background is SolidColorBrush { Color.A: 0 }, "O texto informativo virou campo editável ou ganhou uma borda.");
            info.Focus();
            info.Select(0, 10);
            Require(info.SelectedText == "Informação" && ApplicationCommands.Copy.CanExecute(null, info),
                "O texto informativo perdeu a seleção/cópia.");
            Require(input.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)), "A navegação por Tab foi interrompida.");
            foreach (var button in new[] { accept, search })
            {
                foreach (var enabled in new[] { true, false })
                {
                    button.IsEnabled = enabled;
                    window.UpdateLayout();
                    var chrome = (Border)button.Template.FindName("Chrome", button);
                    Require(Contrast((SolidColorBrush)button.Foreground, (SolidColorBrush)chrome.Background) >= 4.5,
                        $"Texto ilegível no botão {button.Content}, habilitado={enabled}.");
                }
            }
        }
        finally { window.Close(); }
    }

    private static double Contrast(SolidColorBrush first, SolidColorBrush second)
    {
        static double Channel(byte value)
        {
            var channel = value / 255d;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        var a = Luminance(first.Color);
        var b = Luminance(second.Color);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static Window LoadAppearanceWindow(string path)
    {
        var root = XDocument.Load(path).Root!;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        root.Attribute(x + "Class")!.Remove();
        foreach (var declaration in root.Attributes().Where(a => a.IsNamespaceDeclaration).ToArray())
            if (declaration.Value.StartsWith("clr-namespace:", StringComparison.Ordinal) &&
                !declaration.Value.Contains(";assembly=", StringComparison.Ordinal))
            {
                XNamespace original = declaration.Value;
                XNamespace resolved = declaration.Value + ";assembly=PNCPKing";
                foreach (var element in root.DescendantsAndSelf())
                {
                    if (element.Name.Namespace == original) element.Name = resolved + element.Name.LocalName;
                    foreach (var attribute in element.Attributes().Where(a => a.Name.Namespace == original).ToArray())
                    {
                        element.SetAttributeValue(resolved + attribute.Name.LocalName, attribute.Value);
                        attribute.Remove();
                    }
                }
                declaration.Value += ";assembly=PNCPKing";
            }
        var events = new HashSet<string>
        {
            "Click", "SelectionChanged", "SelectedItemChanged", "TextChanged", "ValueChanged",
            "PreviewKeyDown", "KeyDown", "PreviewMouseLeftButtonDown", "PreviewMouseLeftButtonUp",
            "PreviewMouseRightButtonDown", "PreviewMouseMove", "MouseLeave", "MouseDoubleClick",
            "DragOver", "DragLeave", "Drop", "Loaded", "Closing", "TreeViewItem.Expanded"
        };
        foreach (var setter in root.Descendants().Where(e => e.Name.LocalName == "EventSetter").ToArray())
            setter.Remove();
        foreach (var element in root.DescendantsAndSelf())
            foreach (var attribute in element.Attributes().Where(a => events.Contains(a.Name.LocalName)).ToArray())
                attribute.Remove();
        return (Window)XamlReader.Parse(root.ToString());
    }

    private static object AppearanceData()
    {
        dynamic data = new ExpandoObject();
        data.QueryText = "café torrado";
        data.Header = "Café torrado e moído — pacote de 500 g";
        data.ItemSummary = "Cotação de demonstração • 100 unidades";
        data.MaintenancePanelSummary = "Base local pronta para pesquisa";
        data.InterfaceIndicatorBrush = "#2E7D32";
        data.InterfaceIndicatorText = "Interface estável";
        data.PncpIndicatorBrush = "#2E7D32";
        data.PncpIndicatorText = "PNCP disponível";
        data.IsMaintenancePanelOpen = false;
        data.IsInitializing = false;
        data.IsContractsPanelOpen = false;
        data.SelectedResultsWorkspace = AppUnderTest::PNCPKing.App.ViewModels.ResultsWorkspace.Search;
        data.IsDocumentBusy = false;
        data.HasSweetCodeSuggestions = false;
        data.IsCustomDateRange = false;
        data.ProgressText = "Pesquisa concluída. Confira os preços e a cesta selecionada.";
        data.StatusText = "Pronto";
        data.ResourceStatusText = "Dados sintéticos — sem acesso ao banco";
        data.ItemSearchSummary = "50 preços encontrados";
        data.ManualBasketButtonText = "Criar cesta manual";
        data.GeoFilters = new[] { "Todo o Brasil", "São Paulo" };
        data.SelectedGeoFilter = "Todo o Brasil";
        data.DateRanges = new[] { "Últimos 12 meses" };
        data.SelectedDateRange = "Últimos 12 meses";
        data.ItemSearchRows = Enumerable.Range(0, 50).Select(LongRow).ToArray();
        data.ItemSearchRows[1].IsPinned = true;
        data.ItemSearchRows[2].IsSelectedForBasket = true;
        data.Summary = "Dados de demonstração para conferir leitura, alinhamento e apresentação.";
        return data;
    }

    private static object CaptureAppearance(Window window, string name, string outputDirectory, double openingMs)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        // Capture from the window's coordinate space so root margins and DPI simulation
        // transforms remain inside the bitmap instead of being cropped at the content size.
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(window.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
            context.DrawRectangle(window.Background, null, bounds);
        }
        bitmap.Render(drawing);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(outputDirectory, name + ".png"))) encoder.Save(stream);
        var controls = AppearanceChildren(content).OfType<Control>().Where(c => c.IsVisible)
            .Where(c => c is Button or TextBox or ComboBox or DataGrid or TabControl)
            .Select(c => new { type = c.GetType().Name, c.Name, width = c.ActualWidth, height = c.ActualHeight,
                label = c is Button button ? button.Content?.ToString() : null }).ToArray();
        return new { name, width = window.ActualWidth, height = window.ActualHeight, openingMs,
            workingSetBytes = Process.GetCurrentProcess().WorkingSet64, controls };
    }

    private static IEnumerable<DependencyObject> AppearanceChildren(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in AppearanceChildren(child)) yield return descendant;
        }
    }
}
