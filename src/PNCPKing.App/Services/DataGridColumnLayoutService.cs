using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using PNCPKing.App.Views;
using PNCPKing.App.ViewModels;
using PNCPKing.Core.Interfaces;

namespace PNCPKing.App.Services;

public sealed class DataGridColumnLayoutService
{
    private static readonly DependencyPropertyDescriptor WidthDescriptor =
        DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
    private static readonly DependencyPropertyDescriptor VisibilityDescriptor =
        DependencyPropertyDescriptor.FromProperty(DataGridColumn.VisibilityProperty, typeof(DataGridColumn));
    private static readonly DependencyPropertyDescriptor DisplayIndexDescriptor =
        DependencyPropertyDescriptor.FromProperty(DataGridColumn.DisplayIndexProperty, typeof(DataGridColumn));

    private readonly AppSettingsService _settingsService;
    private readonly IPerformanceTelemetry _telemetry;
    private readonly DispatcherTimer _saveTimer;
    private readonly Dictionary<DataGrid, Registration> _registrations = [];
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private AppSettings _settings;
    private bool _applying;
    private ColumnChooserWindow? _activeChooser;
    private DataGrid? _activeChooserGrid;

    public DataGridColumnLayoutService(
        AppSettingsService settingsService,
        AppSettings settings,
        IPerformanceTelemetry? telemetry = null)
    {
        _settingsService = settingsService;
        _telemetry = telemetry ?? NullPerformanceTelemetry.Instance;
        _settings = settings with
        {
            SettingsVersion = Math.Max(3, settings.SettingsVersion),
            ColumnLayouts = settings.ColumnLayouts is null
                ? new Dictionary<string, List<ColumnLayoutSetting>>(StringComparer.Ordinal)
                : new Dictionary<string, List<ColumnLayoutSetting>>(settings.ColumnLayouts, StringComparer.Ordinal)
        };
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveNowAsync().ConfigureAwait(true);
        };
    }

    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key",
        typeof(string),
        typeof(DataGridColumnLayoutService),
        new PropertyMetadata(null));

    public static void SetKey(DependencyObject element, string value) => element.SetValue(KeyProperty, value);

    public static string? GetKey(DependencyObject element) => element.GetValue(KeyProperty) as string;

    public void Register(string gridKey, DataGrid dataGrid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gridKey);
        ArgumentNullException.ThrowIfNull(dataGrid);
        if (_registrations.ContainsKey(dataGrid))
        {
            return;
        }

        var keyedColumns = dataGrid.Columns
            .Select((column, index) => new KeyedColumn(GetStableColumnKey(column, index), column))
            .ToArray();
        var defaults = Capture(keyedColumns);
        var registration = new Registration(gridKey, keyedColumns, defaults);
        _registrations.Add(dataGrid, registration);

        ApplySaved(registration);
        foreach (var keyedColumn in keyedColumns)
        {
            WidthDescriptor.AddValueChanged(keyedColumn.Column, ColumnLayoutChanged);
            VisibilityDescriptor.AddValueChanged(keyedColumn.Column, ColumnLayoutChanged);
            DisplayIndexDescriptor.AddValueChanged(keyedColumn.Column, ColumnLayoutChanged);
        }
    }

    public void ShowChooser(Window owner, DataGrid dataGrid)
    {
        if (!_registrations.TryGetValue(dataGrid, out var registration))
        {
            throw new InvalidOperationException("A grade não possui uma chave de configuração registrada.");
        }

        if (_activeChooser is { IsVisible: true } active)
        {
            _telemetry.Record("ui", "interaction-suppressed", TimeSpan.Zero);
            active.Activate();
            return;
        }

        using var span = _telemetry.Begin("ui", "column-chooser-open");
        var rows = registration.Columns
            .OrderBy(item => item.Column.DisplayIndex)
            .Select(item => new ColumnChooserRow(
                item.Key,
                item.Column.Header?.ToString() ?? "Coluna",
                item.Column.Visibility == Visibility.Visible,
                registration.Defaults.First(state => state.Key == item.Key).IsVisible))
            .ToArray();
        var chooser = new ColumnChooserWindow(rows)
        {
            Owner = owner
        };
        _activeChooser = chooser;
        _activeChooserGrid = dataGrid;
        chooser.ApplyRequested += (_, draft) => ApplyVisibilityDraft(registration, dataGrid, draft);
        chooser.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeChooser, chooser))
            {
                _activeChooser = null;
                _activeChooserGrid = null;
            }
        };
        chooser.Show();
        span.Complete(rows.Length);
    }

    public void Unregister(DataGrid dataGrid)
    {
        if (ReferenceEquals(_activeChooserGrid, dataGrid))
        {
            _activeChooser?.Close();
        }

        if (!_registrations.Remove(dataGrid, out var registration))
        {
            return;
        }

        var layouts = _settings.ColumnLayouts ??
                      new Dictionary<string, List<ColumnLayoutSetting>>(StringComparer.Ordinal);
        layouts[registration.GridKey] = Capture(registration.Columns);
        _settings = _settings with { SettingsVersion = Math.Max(3, _settings.SettingsVersion), ColumnLayouts = layouts };
        foreach (var keyedColumn in registration.Columns)
        {
            WidthDescriptor.RemoveValueChanged(keyedColumn.Column, ColumnLayoutChanged);
            VisibilityDescriptor.RemoveValueChanged(keyedColumn.Column, ColumnLayoutChanged);
            DisplayIndexDescriptor.RemoveValueChanged(keyedColumn.Column, ColumnLayoutChanged);
        }

        ScheduleSave();
    }

    public async Task FlushAsync()
    {
        using var span = _telemetry.Begin("ui", "column-layout-flush");
        try
        {
            int columnCount;
            if (_saveTimer.Dispatcher.CheckAccess())
            {
                _saveTimer.Stop();
                CaptureAll();
                columnCount = _registrations.Values.Sum(item => item.Columns.Count);
            }
            else
            {
                columnCount = await _saveTimer.Dispatcher.InvokeAsync(() =>
                        {
                            _saveTimer.Stop();
                            CaptureAll();
                            return _registrations.Values.Sum(item => item.Columns.Count);
                        }, DispatcherPriority.Send)
                    .Task
                    .ConfigureAwait(false);
            }

            await SaveNowAsync().ConfigureAwait(false);
            span.Complete(columnCount);
        }
        catch (Exception exception)
        {
            span.Fail(exception);
            throw;
        }
    }

    private void ApplySaved(Registration registration)
    {
        if (_settings.ColumnLayouts is null ||
            !_settings.ColumnLayouts.TryGetValue(registration.GridKey, out var saved) ||
            saved is null)
        {
            return;
        }

        var byKey = saved
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        _applying = true;
        try
        {
            foreach (var keyedColumn in registration.Columns)
            {
                if (!byKey.TryGetValue(keyedColumn.Key, out var state))
                {
                    continue;
                }

                keyedColumn.Column.Visibility = state.IsVisible ? Visibility.Visible : Visibility.Collapsed;
                keyedColumn.Column.Width = ParseWidth(state);
            }

            foreach (var item in registration.Columns
                         .Where(item => byKey.ContainsKey(item.Key))
                         .OrderBy(item => byKey[item.Key].DisplayIndex))
            {
                var desired = Math.Clamp(
                    byKey[item.Key].DisplayIndex,
                    0,
                    Math.Max(0, registration.Columns.Count - 1));
                item.Column.DisplayIndex = desired;
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private void Reset(Registration registration)
    {
        _applying = true;
        try
        {
            ApplyStates(registration, registration.Defaults);
        }
        finally
        {
            _applying = false;
        }

        CaptureAll();
        ScheduleSave();
    }

    private void ApplyVisibilityDraft(
        Registration registration,
        DataGrid dataGrid,
        IReadOnlyDictionary<string, bool> visibility)
    {
        using var span = _telemetry.Begin("ui", "column-layout-apply");
        _applying = true;
        try
        {
            using (dataGrid.Dispatcher.DisableProcessing())
            {
                foreach (var item in registration.Columns)
                {
                    if (visibility.TryGetValue(item.Key, out var isVisible))
                    {
                        item.Column.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }
        }
        finally
        {
            _applying = false;
        }

        Capture(registration);
        ScheduleSave();
        span.Complete(registration.Columns.Count);
    }

    private static void ApplyStates(
        Registration registration,
        IReadOnlyList<ColumnLayoutSetting> states)
    {
        var byKey = states.ToDictionary(item => item.Key, StringComparer.Ordinal);
        foreach (var item in registration.Columns)
        {
            if (!byKey.TryGetValue(item.Key, out var state))
            {
                continue;
            }

            item.Column.Visibility = state.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            item.Column.Width = ParseWidth(state);
        }

        foreach (var item in registration.Columns.OrderBy(item => byKey[item.Key].DisplayIndex))
        {
            item.Column.DisplayIndex = byKey[item.Key].DisplayIndex;
        }
    }

    private void ColumnLayoutChanged(object? sender, EventArgs e)
    {
        if (_applying)
        {
            return;
        }

        CaptureAll();
        ScheduleSave();
    }

    private void CaptureAll()
    {
        var layouts = _settings.ColumnLayouts ??
                      new Dictionary<string, List<ColumnLayoutSetting>>(StringComparer.Ordinal);
        foreach (var registration in _registrations.Values)
        {
            layouts[registration.GridKey] = Capture(registration.Columns);
        }

        _settings = _settings with { SettingsVersion = Math.Max(3, _settings.SettingsVersion), ColumnLayouts = layouts };
    }

    private void Capture(Registration registration)
    {
        var layouts = _settings.ColumnLayouts ??
                      new Dictionary<string, List<ColumnLayoutSetting>>(StringComparer.Ordinal);
        layouts[registration.GridKey] = Capture(registration.Columns);
        _settings = _settings with
        {
            SettingsVersion = Math.Max(3, _settings.SettingsVersion),
            ColumnLayouts = layouts
        };
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveNowAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                _settings = await _settingsService.UpdateAsync(latest => latest with
                {
                    SettingsVersion = Math.Max(3, latest.SettingsVersion),
                    ColumnLayouts = _settings.ColumnLayouts
                }).ConfigureAwait(false);
            }
            catch (Exception exception) when (!AsyncCommandRuntime.IsCritical(exception))
            {
                _telemetry.Record(
                    "ui",
                    "column-layout-save",
                    TimeSpan.Zero,
                    succeeded: false,
                    errorKind: exception.GetType().Name);
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static List<ColumnLayoutSetting> Capture(IEnumerable<KeyedColumn> columns) =>
        columns.Select(item =>
        {
            var width = item.Column.Width;
            return new ColumnLayoutSetting(
                item.Key,
                item.Column.DisplayIndex,
                item.Column.Visibility == Visibility.Visible,
                double.IsNaN(width.Value) || double.IsInfinity(width.Value) ? 1 : width.Value,
                width.UnitType.ToString());
        }).ToList();

    private static DataGridLength ParseWidth(ColumnLayoutSetting state)
    {
        var value = state.Width > 0 && double.IsFinite(state.Width) ? state.Width : 1;
        return Enum.TryParse<DataGridLengthUnitType>(state.WidthUnit, out var unit)
            ? new DataGridLength(value, unit)
            : new DataGridLength(value, DataGridLengthUnitType.Pixel);
    }

    private static string GetStableColumnKey(DataGridColumn column, int index)
    {
        var explicitKey = GetKey(column);
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            return explicitKey;
        }

        if (!string.IsNullOrWhiteSpace(column.SortMemberPath))
        {
            return column.SortMemberPath;
        }

        if (column is DataGridBoundColumn { Binding: Binding binding } &&
            binding.Path?.Path is { Length: > 0 } bindingPath)
        {
            return bindingPath;
        }

        return $"column-{index}";
    }

    private sealed record KeyedColumn(string Key, DataGridColumn Column);

    private sealed record Registration(
        string GridKey,
        IReadOnlyList<KeyedColumn> Columns,
        IReadOnlyList<ColumnLayoutSetting> Defaults);
}
