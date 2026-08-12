using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PNCPKing.App.ViewModels;

public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        NotifyReset();
    }

    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var added = false;
        foreach (var item in items)
        {
            Items.Add(item);
            added = true;
        }

        if (added)
        {
            NotifyReset();
        }
    }

    private void NotifyReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
