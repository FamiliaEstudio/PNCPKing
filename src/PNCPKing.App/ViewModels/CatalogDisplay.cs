using System.Collections.ObjectModel;
using PNCPKing.Core.Models;

namespace PNCPKing.App.ViewModels;

public sealed record CatalogKindOption(string Label, CatalogKind? Kind)
{
    public override string ToString() => Label;
}

public sealed class CatalogSearchResultDisplay
{
    public CatalogSearchResultDisplay(CatalogSearchResult source)
    {
        Source = source;
        Signals = source.Signals.Select(signal => new CatalogSignalDisplay(signal)).ToArray();
    }

    public CatalogSearchResult Source { get; }
    public string Kind => Source.Entry.KindLabel;
    public string Code => Source.Entry.Code;
    public string Description => Source.Entry.Description;
    public string Hierarchy => Source.Entry.Hierarchy;
    public decimal Score => Source.Score;
    public int Matches => Source.MatchCount;
    public int Conflicts => Source.ConflictCount;
    public int Missing => Source.MissingCount;
    public IReadOnlyList<CatalogSignalDisplay> Signals { get; }
}

public sealed class CatalogSignalDisplay(CatalogMatchSignal source)
{
    public string Text => source.State switch
    {
        CatalogMatchState.Match => source.Requested,
        CatalogMatchState.Conflict => $"{source.Requested} ≠ {source.Found}",
        _ => source.Requested
    };

    public string Background => source.State switch
    {
        CatalogMatchState.Match => "#D9F2E3",
        CatalogMatchState.Conflict => "#FADADA",
        _ => "#E5E7EA"
    };

    public string Foreground => source.State switch
    {
        CatalogMatchState.Match => "#176B3A",
        CatalogMatchState.Conflict => "#A12622",
        _ => "#59616A"
    };

    public string ToolTip => source.Explanation;
}

public sealed class CatalogSyncDisplay
{
    public CatalogSyncDisplay(CatalogSyncState source) => Source = source;

    public CatalogSyncState Source { get; }
    public string Kind => Source.Kind == CatalogKind.Catmat ? "CATMAT" : "CATSER";
    public double Percentage => Source.Percentage;
    public string Summary => Source.Status switch
    {
        CatalogSyncStatus.Missing => $"{Kind}: catálogo ausente",
        CatalogSyncStatus.Downloading =>
            $"{Kind}: {Math.Max(0, Source.NextPage - 1):N0}/{Source.TotalPages:N0} páginas · {Source.StagedRecords:N0} registros",
        CatalogSyncStatus.Complete =>
            $"{Kind}: {Source.ActiveRecords:N0} ativos · concluído {Source.CompletedAt:dd/MM/yyyy HH:mm}",
        CatalogSyncStatus.Failed => $"{Kind}: falha — {Source.LastError}",
        CatalogSyncStatus.Paused => $"{Kind}: pausado na página {Source.NextPage:N0}",
        _ => Kind
    };

    public string Color => Source.Status switch
    {
        CatalogSyncStatus.Downloading or CatalogSyncStatus.Paused => "#3C8DDE",
        CatalogSyncStatus.Complete => "#36A269",
        CatalogSyncStatus.Failed => "#D9534F",
        _ => "#9AA1A8"
    };
}

public sealed class CatalogHierarchyNode
{
    public required string Label { get; init; }
    public required CatalogKind Kind { get; init; }
    public int Level { get; init; }
    public CatalogHierarchyFilter Filter { get; init; } = new();
    public bool IsPlaceholder { get; init; }
    public bool ChildrenLoaded { get; set; }
    public ObservableCollection<CatalogHierarchyNode> Children { get; } = [];

    public void PrepareLazyChildren()
    {
        if (Children.Count == 0)
        {
            Children.Add(new CatalogHierarchyNode
            {
                Label = "Carregando…",
                Kind = Kind,
                Level = Level + 1,
                IsPlaceholder = true
            });
        }
    }
}
