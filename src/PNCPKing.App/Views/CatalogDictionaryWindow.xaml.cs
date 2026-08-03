using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PNCPKing.App.ViewModels;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public partial class CatalogDictionaryWindow : Window, INotifyPropertyChanged
{
    private readonly ICatalogRepository _repository;

    public CatalogDictionaryWindow(ICatalogRepository repository)
    {
        _repository = repository;
        InitializeComponent();
        DataContext = this;
        Loaded += async (_, _) => await ReloadAsync().ConfigureAwait(true);
    }

    public ObservableCollection<CatalogRuleEditorRow> Rules { get; } = [];
    public CatalogRuleEditorRow? SelectedRule { get; set; }
    private string _status = "Carregando regras…";
    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async Task ReloadAsync()
    {
        Rules.Clear();
        foreach (var rule in await _repository.GetEquivalenceRulesAsync().ConfigureAwait(true))
        {
            Rules.Add(new CatalogRuleEditorRow(rule));
        }

        Status = $"{Rules.Count:N0} regra(s). Conversões usam tolerância de 0,5% (mínimo absoluto 0,01).";
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var row = new CatalogRuleEditorRow(new CatalogEquivalenceRule
        {
            Id = Guid.NewGuid(),
            Kind = CatalogRuleKind.Alias,
            Canonical = string.Empty,
            Alias = string.Empty
        });
        Rules.Add(row);
        RulesGrid.SelectedItem = row;
        RulesGrid.ScrollIntoView(row);
        RulesGrid.BeginEdit();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        RulesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RulesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (SelectedRule is null) return;
        await RunAsync(async () =>
        {
            await _repository.SaveEquivalenceRuleAsync(SelectedRule.ToModel()).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRule is null) return;
        await RunAsync(async () =>
        {
            await _repository.DeleteEquivalenceRuleAsync(SelectedRule.Id).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                this,
                "Restaurar todas as regras padrão? Regras personalizadas serão mantidas quando não entrarem em conflito.",
                "Restaurar equivalências",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(async () =>
        {
            await _repository.ResetDefaultEquivalenceRulesAsync().ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Equivalências", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public sealed class CatalogRuleEditorRow(CatalogEquivalenceRule source)
{
    public Guid Id { get; } = source.Id;
    public CatalogRuleKind Kind { get; set; } = source.Kind;
    public string Canonical { get; set; } = source.Canonical;
    public string Alias { get; set; } = source.Alias;
    public string Dimension { get; set; } = source.Dimension;
    public decimal Factor { get; set; } = source.Factor;
    public bool IsDefault { get; } = source.IsDefault;

    public CatalogEquivalenceRule ToModel() => new()
    {
        Id = Id,
        Kind = Kind,
        Canonical = Canonical,
        Alias = Alias,
        Dimension = Dimension,
        Factor = Factor,
        IsDefault = IsDefault
    };
}
