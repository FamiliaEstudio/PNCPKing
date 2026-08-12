using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PNCPKing.Core.Models;

namespace PNCPKing.App.Controls;

public sealed class CoverageTimeline : FrameworkElement
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable<CoverageDay>),
        typeof(CoverageTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsSourceChanged));

    private static readonly IReadOnlyDictionary<CoverageStatus, Brush> Brushes =
        new Dictionary<CoverageStatus, Brush>
        {
            [CoverageStatus.Missing] = Frozen("#BFC5CC"),
            [CoverageStatus.Partial] = Frozen("#E6B84E"),
            [CoverageStatus.Downloading] = Frozen("#3C8DDE"),
            [CoverageStatus.Complete] = Frozen("#36A269"),
            [CoverageStatus.AssumedComplete] = Frozen("#70B98D"),
            [CoverageStatus.Failed] = Frozen("#D9534F")
        };
    private IReadOnlyList<CoverageDay> _days = [];

    public CoverageTimeline()
    {
        MinHeight = 8;
        ToolTipService.SetShowDuration(this, 30_000);
    }

    public IEnumerable<CoverageDay>? ItemsSource
    {
        get => (IEnumerable<CoverageDay>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_days.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            drawingContext.DrawRectangle(Brushes[CoverageStatus.Missing], null, new Rect(RenderSize));
            return;
        }

        var unit = ActualWidth / _days.Count;
        for (var index = 0; index < _days.Count; index++)
        {
            var left = Math.Floor(index * unit);
            var right = index == _days.Count - 1
                ? ActualWidth
                : Math.Ceiling((index + 1) * unit);
            drawingContext.DrawRectangle(
                Brushes[_days[index].Status],
                null,
                new Rect(left, 0, Math.Max(1, right - left), ActualHeight));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_days.Count == 0 || ActualWidth <= 0)
        {
            ToolTip = null;
            return;
        }

        var index = Math.Clamp((int)(e.GetPosition(this).X * _days.Count / ActualWidth), 0, _days.Count - 1);
        ToolTip = _days[index].ToolTip;
    }

    private static void OnItemsSourceChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var timeline = (CoverageTimeline)target;
        if (args.OldValue is INotifyCollectionChanged oldCollection)
        {
            oldCollection.CollectionChanged -= timeline.OnCollectionChanged;
        }

        if (args.NewValue is INotifyCollectionChanged newCollection)
        {
            newCollection.CollectionChanged += timeline.OnCollectionChanged;
        }

        timeline.RefreshItems();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshItems();

    private void RefreshItems()
    {
        _days = ItemsSource?.ToArray() ?? [];
        InvalidateVisual();
    }

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
