using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PNCPKing.App.Services;
using PNCPKing.App.ViewModels;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Views;

public partial class CatalogDictionaryWindow : Window, INotifyPropertyChanged
{
    private readonly ICatalogRepository _repository;
    private readonly AppDiagnosticLog _diagnosticLog;
    private readonly IPerformanceTelemetry _telemetry;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private CancellationTokenSource? _operationCancellation;
    private string _status = "Carregando regras…";
    private bool _isBusy;

    public CatalogDictionaryWindow(
        ICatalogRepository repository,
        AppDiagnosticLog diagnosticLog,
        IPerformanceTelemetry telemetry)
    {
        _repository = repository;
        _diagnosticLog = diagnosticLog;
        _telemetry = telemetry;
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public RangeObservableCollection<CatalogRuleEditorRow> Rules { get; } = [];
    public CatalogRuleEditorRow? SelectedRule { get; set; }

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged(nameof(Status));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanInteract));
        }
    }

    public bool CanInteract => !IsBusy;

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await RunOperationAsync("load", ReloadCoreAsync).ConfigureAwait(true);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _operationCancellation?.Cancel();
        _lifetimeCancellation.Cancel();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _operationCancellation?.Dispose();
        _lifetimeCancellation.Dispose();
        _operationGate.Dispose();
    }

    private async Task ReloadCoreAsync(CancellationToken cancellationToken)
    {
        Status = "Carregando regras…";
        var rules = await _repository.GetEquivalenceRulesAsync(cancellationToken).ConfigureAwait(true);
        Rules.ReplaceAll(rules.Select(rule => new CatalogRuleEditorRow(rule)));
        Status = $"{Rules.Count:N0} regra(s). Conversões usam tolerância de 0,5% (mínimo absoluto 0,01).";
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
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
        await RunOperationAsync("save", async cancellationToken =>
        {
            RulesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            RulesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (SelectedRule is null) return;
            Status = "Salvando regra…";
            await _repository.SaveEquivalenceRuleAsync(SelectedRule.ToModel(), cancellationToken)
                .ConfigureAwait(true);
            await ReloadCoreAsync(cancellationToken).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("delete", async cancellationToken =>
        {
            if (SelectedRule is null) return;
            Status = "Excluindo regra…";
            await _repository.DeleteEquivalenceRuleAsync(SelectedRule.Id, cancellationToken)
                .ConfigureAwait(true);
            await ReloadCoreAsync(cancellationToken).ConfigureAwait(true);
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
        await RunOperationAsync("reset", async cancellationToken =>
        {
            Status = "Restaurando regras padrão…";
            await _repository.ResetDefaultEquivalenceRulesAsync(cancellationToken).ConfigureAwait(true);
            await ReloadCoreAsync(cancellationToken).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();

    private async Task RunOperationAsync(
        string phase,
        Func<CancellationToken, Task> action)
    {
        if (!await _operationGate.WaitAsync(0).ConfigureAwait(true))
        {
            _telemetry.Record("ui", "interaction-suppressed", TimeSpan.Zero);
            Activate();
            return;
        }

        using var span = _telemetry.Begin("catalog-equivalences", phase);
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        IsBusy = true;
        try
        {
            await action(_operationCancellation.Token).ConfigureAwait(true);
            span.Complete(Rules.Count);
        }
        catch (OperationCanceledException) when (_operationCancellation.IsCancellationRequested)
        {
            Status = "Operação cancelada; alterações já confirmadas foram preservadas.";
        }
        catch (Exception exception)
        {
            span.Fail(exception);
            Status = $"Não foi possível concluir a operação: {exception.Message}";
            _diagnosticLog.Error("catalog-equivalences", "Falha recuperável no editor.", exception);
            if (IsVisible)
            {
                MessageBox.Show(this, exception.Message, "Equivalências", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            IsBusy = false;
            _operationCancellation.Dispose();
            _operationCancellation = null;
            _operationGate.Release();
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
