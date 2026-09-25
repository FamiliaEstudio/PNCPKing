using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PNCPKing.App.ViewModels;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;

namespace PNCPKing.App.Views;

public sealed class QuotationGroupsWindow : Window
{
    private readonly QuotationProjectReport _report;
    private readonly ObservableCollection<QuotationGroup> _groups;
    private readonly List<ItemRow> _rows;
    private readonly DataGrid _items = new() { AutoGenerateColumns = false, IsReadOnly = true,
        SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow };
    private readonly ComboBox _group = new() { MinWidth = 220, DisplayMemberPath = "Name", Margin = new(4) };
    private readonly TextBox _name = new() { Width = 180, Margin = new(4), ToolTip = "Nome opcional do agrupamento" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(4) };

    public QuotationGroupsWindow(QuotationProjectReport report)
    {
        _report = report;
        _groups = new(report.Groups);
        _rows = report.Lines.Select(value => new ItemRow(value)).ToList();
        Title = "Gerenciar grupos e visualizar cotas";
        Width = 1180; Height = 680; MinWidth = 760; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(12) };
        Content = root;
        var intro = new TextBlock { Text = "Selecione itens com Ctrl/Shift para criar um agrupamento ou atribuí-los ao agrupamento escolhido. " +
            "Cada cota principal e reservada terá seu próprio número de grupo. A prévia só é aplicada por Organizar Itens.",
            TextWrapping = TextWrapping.Wrap, Margin = new(4, 4, 4, 10) };
        DockPanel.SetDock(intro, Dock.Top); root.Children.Add(intro);
        var actions = new WrapPanel(); DockPanel.SetDock(actions, Dock.Top); root.Children.Add(actions);
        _group.ItemsSource = _groups;
        _group.SelectionChanged += (_, _) => _name.Text = (_group.SelectedItem as QuotationGroup)?.Name ?? string.Empty;
        actions.Children.Add(_group); actions.Children.Add(_name);
        AddButton(actions, "Criar com seleção", CreateGroup);
        AddButton(actions, "Atribuir seleção", AssignGroup);
        AddButton(actions, "Renomear", RenameGroup);
        AddButton(actions, "Retirar seleção", () => ChangeMembership(null));
        AddButton(actions, "Excluir grupo", DeleteGroup);
        var search = new TextBox { Margin = new(4, 8, 4, 8), ToolTip = "Buscar pelo nome ou descrição do item" };
        DockPanel.SetDock(search, Dock.Top); root.Children.Add(search);
        var footer = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom); footer.Children.Add(buttons);
        AddButton(buttons, "Salvar grupos", () => DialogResult = true);
        var cancel = new Button { Content = "Cancelar", IsCancel = true, Margin = new(4), Padding = new(10, 5, 10, 5) };
        buttons.Children.Add(cancel);
        footer.Children.Add(new ScrollViewer { Content = _status, MaxHeight = 100,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _items.ItemsSource = _rows;
        AddColumn("Item", nameof(ItemRow.Name), 230);
        AddColumn("Agrupamento", nameof(ItemRow.GroupName), 140);
        AddColumn("Números", nameof(ItemRow.Numbers), 80);
        AddColumn("Grupos resultantes", nameof(ItemRow.ResultGroups), 240);
        AddColumn("Qtd. total", nameof(ItemRow.Quantity), 80);
        AddColumn("Qtd. principal", nameof(ItemRow.PrincipalQuantity), 95);
        AddColumn("Valor principal", nameof(ItemRow.PrincipalValue), 130);
        AddColumn("Qtd. reservada", nameof(ItemRow.ReservedQuantity), 95);
        AddColumn("Valor reservado", nameof(ItemRow.ReservedValue), 130);
        AddColumn("Observação", nameof(ItemRow.Note), 220);
        root.Children.Add(_items);
        search.TextChanged += (_, _) =>
        {
            var query = search.Text.Trim();
            CollectionViewSource.GetDefaultView(_items.ItemsSource).Filter = value => value is ItemRow row &&
                (row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                 row.Analysis.Line.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        };
        RefreshPreview();
    }

    public IReadOnlyList<QuotationGroup> Groups => _groups
        .Select(group => group with { LineIds = _rows.Where(row => row.GroupId == group.Id).Select(row => row.Analysis.Line.Id).ToArray() })
        .Where(group => group.LineIds.Count > 0).ToArray();

    private static void AddButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Margin = new(4), Padding = new(10, 5, 10, 5) };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    private void AddColumn(string name, string binding, double width) =>
        _items.Columns.Add(new DataGridTextColumn { Header = name, Binding = new Binding(binding), Width = width });

    private ItemRow[] Selected() => _items.SelectedItems.Cast<ItemRow>().ToArray();

    private void CreateGroup()
    {
        if (Selected().Length == 0) { _status.Text = "Selecione os itens que formarão o agrupamento."; return; }
        var number = 1;
        while (_groups.Any(group => group.Name == $"Agrupamento {number}")) number++;
        var group = new QuotationGroup(Guid.NewGuid(), _report.Project.Id,
            string.IsNullOrWhiteSpace(_name.Text) ? $"Agrupamento {number}" : _name.Text.Trim(), []);
        _groups.Add(group); _group.SelectedItem = group;
        ChangeMembership(group.Id);
    }

    private void AssignGroup()
    {
        if (_group.SelectedItem is QuotationGroup group) ChangeMembership(group.Id);
        else _status.Text = "Escolha o agrupamento que receberá a seleção.";
    }

    private void ChangeMembership(Guid? groupId)
    {
        foreach (var row in Selected()) row.GroupId = groupId;
        RemoveEmptyGroups(); RefreshPreview();
    }

    private void RenameGroup()
    {
        if (_group.SelectedItem is not QuotationGroup group || string.IsNullOrWhiteSpace(_name.Text)) return;
        var renamed = group with { Name = _name.Text.Trim() };
        _groups[_groups.IndexOf(group)] = renamed; _group.SelectedItem = renamed;
        RefreshPreview();
    }

    private void DeleteGroup()
    {
        if (_group.SelectedItem is not QuotationGroup group) return;
        foreach (var row in _rows.Where(row => row.GroupId == group.Id)) row.GroupId = null;
        _groups.Remove(group); RefreshPreview();
    }

    private void RemoveEmptyGroups()
    {
        foreach (var group in _groups.Where(group => _rows.All(row => row.GroupId != group.Id)).ToArray())
            _groups.Remove(group);
    }

    private void RefreshPreview()
    {
        QuotationOrganizationSnapshot? preview = null;
        try
        {
            var lines = _rows.Select(row => row.Analysis with { Line = row.Analysis.Line with { GroupId = row.GroupId } }).ToArray();
            preview = QuotationOrganization.Calculate(_report.Project, lines, Groups);
            _status.Text = $"Prévia: {preview.Groups.Count} grupos resultantes e {preview.Positions.Count} posições de itens. " +
                "Salvar grupos preserva a organização anterior; depois clique em Organizar Itens para aplicar esta sequência.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or OverflowException)
        {
            _status.Text = "Você pode salvar os grupos. Prévia pendente: " + exception.Message;
        }
        foreach (var row in _rows)
            row.Refresh(_groups.FirstOrDefault(group => group.Id == row.GroupId)?.Name ?? "Sem grupo", preview, _report.Project.PriceDecimalPlaces);
    }

    public sealed class ItemRow(QuotationLineAnalysis analysis) : ObservableObject
    {
        public QuotationLineAnalysis Analysis { get; } = analysis;
        public Guid? GroupId { get; set; } = analysis.Line.GroupId;
        public string Name => Analysis.Line.EffectiveDisplayName;
        public decimal Quantity => Analysis.Line.RequestedQuantity;
        public string GroupName { get; private set; } = string.Empty;
        public string Numbers { get; private set; } = string.Empty;
        public string ResultGroups { get; private set; } = string.Empty;
        public decimal? PrincipalQuantity { get; private set; }
        public decimal? ReservedQuantity { get; private set; }
        public string PrincipalValue { get; private set; } = string.Empty;
        public string ReservedValue { get; private set; } = string.Empty;
        public string Note { get; private set; } = string.Empty;

        public void Refresh(string name, QuotationOrganizationSnapshot? preview, int decimals)
        {
            GroupName = name; Numbers = preview?.ItemNumbers(Analysis.Line.Id) ?? string.Empty;
            ResultGroups = preview?.GroupNumbers(Analysis.Line.Id) ?? string.Empty;
            var positions = preview?.Positions.Where(position => position.LineId == Analysis.Line.Id).ToArray() ?? [];
            var main = positions.FirstOrDefault(position => position.Kind != QuotationQuotaKind.Reserved);
            var reserve = positions.FirstOrDefault(position => position.Kind == QuotationQuotaKind.Reserved);
            PrincipalQuantity = main?.Quantity; ReservedQuantity = preview is null ? null : reserve?.Quantity ?? 0;
            PrincipalValue = main?.TotalPrice.ToString($"C{decimals}") ?? string.Empty;
            ReservedValue = preview is null ? string.Empty : (reserve?.TotalPrice ?? 0).ToString($"C{decimals}");
            Note = preview is not null && Quantity < 4 ? "Reserva zero: permanece integralmente na principal" : string.Empty;
            OnPropertyChanged(string.Empty);
        }
    }
}
