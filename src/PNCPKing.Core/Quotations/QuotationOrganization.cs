using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PNCPKing.Core.Models;

namespace PNCPKing.Core.Quotations;

public static class QuotationOrganization
{
    private static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("pt-BR"), ignoreCase: true);

    public static IOrderedEnumerable<QuotationLineAnalysis> Alphabetical(IEnumerable<QuotationLineAnalysis> lines) =>
        lines.OrderBy(value => value.Line.EffectiveDisplayName.Trim(), NameComparer)
            .ThenBy(value => value.Line.DisplayOrder).ThenBy(value => value.Line.Id);

    public static string Signature(QuotationProject project, IReadOnlyList<QuotationLineAnalysis> lines,
        IReadOnlyList<QuotationGroup> groups)
    {
        var input = new
        {
            Version = 1, project.IsMedication,
            Groups = groups.OrderBy(group => group.Id).Select(group => new
            {
                group.Id, group.Name, Members = group.LineIds.Order().ToArray()
            }).ToArray(),
            Lines = lines.OrderBy(value => value.Line.Id).Select(value => new
            {
                value.Line.Id, value.Line.GroupId, value.Line.Description, value.Line.DisplayName,
                value.Line.DisplayOrder, value.Line.RequestedQuantity, value.Line.RequestedUnit,
                value.Line.SelectionConfirmed, value.Line.SelectedBasketKey,
                Price = value.SelectedBasket?.AdoptedPrice,
                Method = value.SelectedBasket?.AggregationMethod,
                Prices = value.SelectedBasket?.PriceEntries.OrderBy(entry => entry.Reference.Id, StringComparer.Ordinal)
                    .Select(entry => new { entry.Reference.Id, entry.ConversionFactor, entry.EffectiveUnitPrice }).ToArray(),
                References = value.SelectedBasket?.References.OrderBy(reference => reference.Id, StringComparer.Ordinal)
                    .Select(reference => new { reference.Id, reference.UnitPrice }).ToArray()
            }).ToArray()
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input)));
    }

    public static QuotationOrganizationSnapshot Calculate(QuotationProject project,
        IReadOnlyList<QuotationLineAnalysis> lines, IReadOnlyList<QuotationGroup> groups)
    {
        if (lines.Count == 0) throw new InvalidOperationException("A cotação não contém itens para organizar.");
        var pending = lines.Where(value => value.Line.RequestedQuantity <= 0 ||
            !QuotationQuantity.IsValid(value.Line.RequestedQuantity) || !value.Line.SelectionConfirmed ||
            value.SelectedBasket is null || value.SelectedBasket.AdoptedPrice <= 0).ToArray();
        if (pending.Length > 0)
            throw new InvalidOperationException("Informe quantidade inteira positiva e confirme a cesta dos itens: " +
                string.Join(", ", pending.Select(value => value.Line.EffectiveDisplayName)));

        var byId = lines.ToDictionary(value => value.Line.Id);
        var members = new HashSet<Guid>();
        foreach (var group in groups)
        {
            if (group.ProjectId != project.Id || group.LineIds.Count == 0 ||
                group.LineIds.Any(id => !byId.ContainsKey(id) || !members.Add(id) || byId[id].Line.GroupId != group.Id))
                throw new InvalidOperationException("Os membros dos grupos não correspondem aos itens desta cotação.");
        }
        if (groups.Select(group => group.Id).Distinct().Count() != groups.Count ||
            lines.Any(value => value.Line.GroupId is not null && !members.Contains(value.Line.Id)))
            throw new InvalidOperationException("Os grupos da cotação são inválidos.");

        decimal Price(QuotationLineAnalysis value) => QuotationMoney.Truncate(
            value.SelectedBasket!.AdoptedPrice, project.PriceDecimalPlaces);
        decimal Total(QuotationLineAnalysis value) => checked(value.Line.RequestedQuantity * Price(value));
        static decimal Reserved(QuotationLineAnalysis value) => decimal.Floor(value.Line.RequestedQuantity / 4m);
        var organizedGroups = groups.Select(group => new
        {
            Source = group,
            Lines = Alphabetical(group.LineIds.Select(id => byId[id])).ToArray()
        }).Select(group => new
        {
            group.Source, group.Lines,
            HasQuota = group.Lines.Sum(Total) > 80_000m && group.Lines.Any(value => Reserved(value) > 0)
        }).OrderBy(group => group.Lines[0].Line.EffectiveDisplayName.Trim(), NameComparer)
            .ThenBy(group => group.Lines[0].Line.DisplayOrder).ThenBy(group => group.Lines[0].Line.Id).ToArray();
        var resultGroups = new List<QuotationResultGroup>();
        var positions = new List<QuotationPosition>();
        void Add(QuotationLineAnalysis value, Guid? groupId, QuotationQuotaKind kind, decimal quantity)
        {
            positions.Add(new(positions.Count + 1, value.Line.Id, groupId, kind, quantity,
                Price(value), checked(quantity * Price(value))));
        }
        foreach (var group in organizedGroups.Where(value => value.HasQuota)
                     .Concat(organizedGroups.Where(value => !value.HasQuota)))
        {
            var principalId = Guid.NewGuid();
            Guid? reservedId = group.HasQuota ? Guid.NewGuid() : null;
            var kind = group.HasQuota ? QuotationQuotaKind.Principal : QuotationQuotaKind.None;
            resultGroups.Add(new(principalId, resultGroups.Count + 1, group.Source.Id, group.Source.Name, kind, reservedId));
            foreach (var value in group.Lines)
                Add(value, principalId, kind, value.Line.RequestedQuantity - (group.HasQuota ? Reserved(value) : 0));
            if (reservedId is { } reserve)
            {
                resultGroups.Add(new(reserve, resultGroups.Count + 1, group.Source.Id, group.Source.Name,
                    QuotationQuotaKind.Reserved, principalId));
                foreach (var value in group.Lines.Where(value => Reserved(value) > 0))
                    Add(value, reserve, QuotationQuotaKind.Reserved, Reserved(value));
            }
        }
        var individuals = Alphabetical(lines.Where(value => value.Line.GroupId is null)).ToArray();
        bool HasQuota(QuotationLineAnalysis value) => Total(value) > 80_000m && Reserved(value) > 0;
        foreach (var value in individuals.Where(HasQuota))
        {
            Add(value, null, QuotationQuotaKind.Principal, value.Line.RequestedQuantity - Reserved(value));
            Add(value, null, QuotationQuotaKind.Reserved, Reserved(value));
        }
        foreach (var value in individuals.Where(value => !HasQuota(value)))
            Add(value, null, QuotationQuotaKind.None, value.Line.RequestedQuantity);
        return new() { Signature = Signature(project, lines, groups), Groups = resultGroups, Positions = positions };
    }

    public static IReadOnlyList<QuotationLineAnalysis> Order(QuotationProject project,
        IReadOnlyList<QuotationLineAnalysis> lines)
    {
        if (project.Organization is not { } organization)
            return project.SortItemsAlphabetically ? Alphabetical(lines).ToArray() : lines;
        var numbers = organization.Positions.GroupBy(position => position.LineId)
            .ToDictionary(group => group.Key, group => group.Min(position => position.Number));
        return lines.OrderBy(value => numbers.GetValueOrDefault(value.Line.Id, int.MaxValue))
            .ThenBy(value => value.Line.DisplayOrder).ThenBy(value => value.Line.Id).ToArray();
    }

    public static void ValidateSnapshot(QuotationProjectReport report)
    {
        if (report.Project.Organization is not { } snapshot) return;
        void Invalid() => throw new InvalidDataException("A organização de grupos e cotas do pacote é inválida.");
        if (snapshot.Version != 1 || string.IsNullOrWhiteSpace(snapshot.Signature) ||
            snapshot.Groups is null || snapshot.Positions is null) { Invalid(); return; }
        if (snapshot.Groups.Select(group => group.Id).Distinct().Count() != snapshot.Groups.Count ||
            !snapshot.Groups.Select(group => group.Number).SequenceEqual(Enumerable.Range(1, snapshot.Groups.Count)) ||
            snapshot.Positions.Count == 0 ||
            !snapshot.Positions.Select(position => position.Number).SequenceEqual(Enumerable.Range(1, snapshot.Positions.Count))) Invalid();
        var groups = snapshot.Groups.ToDictionary(group => group.Id);
        foreach (var group in snapshot.Groups)
        {
            if (group.Id == Guid.Empty || group.SourceGroupId == Guid.Empty || !Enum.IsDefined(group.Kind)) Invalid();
            if (group.Kind == QuotationQuotaKind.None)
            {
                if (group.PairedGroupId is not null) Invalid();
            }
            else if (group.PairedGroupId is not { } pairedId || !groups.TryGetValue(pairedId, out var paired) ||
                paired.Id == group.Id || paired.PairedGroupId != group.Id || paired.SourceGroupId != group.SourceGroupId ||
                paired.Kind != (group.Kind == QuotationQuotaKind.Principal ? QuotationQuotaKind.Reserved : QuotationQuotaKind.Principal) ||
                paired.Number != group.Number + (group.Kind == QuotationQuotaKind.Principal ? 1 : -1)) Invalid();
            if (!snapshot.Positions.Any(position => position.ResultGroupId == group.Id)) Invalid();
        }
        foreach (var position in snapshot.Positions)
        {
            if (position.LineId == Guid.Empty || !Enum.IsDefined(position.Kind) || position.Quantity <= 0 ||
                !QuotationQuantity.IsValid(position.Quantity) || position.UnitPrice <= 0 ||
                position.TotalPrice != checked(position.Quantity * position.UnitPrice)) Invalid();
            if (position.ResultGroupId is { } id &&
                (!groups.TryGetValue(id, out var group) || group.Kind != position.Kind)) Invalid();
        }
        foreach (var item in snapshot.Positions.GroupBy(position => position.LineId))
        {
            var main = item.Where(position => position.Kind != QuotationQuotaKind.Reserved).ToArray();
            var reserves = item.Where(position => position.Kind == QuotationQuotaKind.Reserved).ToArray();
            if (main.Length != 1 || reserves.Length > 1) { Invalid(); return; }
            if (reserves.Length == 1)
            {
                var reserve = reserves[0];
                if (main[0].Kind != QuotationQuotaKind.Principal || main[0].Number >= reserve.Number ||
                    reserve.UnitPrice != main[0].UnitPrice || reserve.Quantity != decimal.Floor((reserve.Quantity + main[0].Quantity) / 4m)) Invalid();
                if (main[0].ResultGroupId is { } id)
                {
                    if (groups[id].PairedGroupId != reserve.ResultGroupId) Invalid();
                }
                else if (reserve.ResultGroupId is not null) Invalid();
            }
        }
        // A stale snapshot may contain deleted members; validate its inputs only when the signature is current.
        if (report.OrganizationIsStale) return;
        var expected = Calculate(report.Project, report.Lines, report.Groups);
        if (!snapshot.Groups.Select(group => (group.Number, group.SourceGroupId, group.Name, group.Kind))
                .SequenceEqual(expected.Groups.Select(group => (group.Number, group.SourceGroupId, group.Name, group.Kind)))) Invalid();
        var expectedGroups = expected.Groups.ToDictionary(group => group.Id);
        if (!snapshot.Positions.Select(position => (position.Number, position.LineId, position.Kind,
                    position.Quantity, position.UnitPrice, position.ResultGroupId is { } id ? groups[id].Number : 0))
                .SequenceEqual(expected.Positions.Select(position => (position.Number, position.LineId, position.Kind,
                    position.Quantity, position.UnitPrice, position.ResultGroupId is { } id ? expectedGroups[id].Number : 0)))) Invalid();
    }
}
