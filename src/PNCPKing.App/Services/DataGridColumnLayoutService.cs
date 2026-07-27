using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;

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
    private readonly DispatcherTimer _saveTimer;
    private readonly Dictionary<DataGrid, Registration> _registrations = [];
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private AppSettings _settings;
    private bool _applying;

    public DataGridColumnLayoutService(AppSettingsService settingsService, AppSettings settings)
    {
        _settingsService = settingsService;
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

    public void ShowChooser(Button button, DataGrid dataGrid)
    {
        if (!_registrations.TryGetValue(dataGrid, out var registration))
        {
            throw new InvalidOperationException("A grade não possui uma chave de configuração registrada.");
        }

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Top,
            StaysOpen = true
        };
        foreach (var keyedColumn in registration.Columns.OrderBy(item => item.Column.DisplayIndex))
        {
            var item = new MenuItem
            {
                Header = keyedColumn.Column.Header?.ToString() ?? "Coluna",
                IsCheckable = true,
                IsChecked = keyedColumn.Column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
                Tag = keyedColumn.Column
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

        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Restaurar padrão" };
        reset.Click += (_, _) => Reset(registration);
        menu.Items.Add(reset);
        menu.IsOpen = true;
    }

    public void Unregister(DataGrid dataGrid)
    {
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
        _saveTimer.Stop();
        CaptureAll();
        await SaveNowAsync().ConfigureAwait(false);
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
            _settings = await _settingsService.UpdateAsync(latest => latest with
            {
                SettingsVersion = Math.Max(3, latest.SettingsVersion),
                ColumnLayouts = _settings.ColumnLayouts
            }).ConfigureAwait(false);
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
