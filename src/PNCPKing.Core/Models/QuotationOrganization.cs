using System.Text.Json;

namespace PNCPKing.Core.Models;

public sealed record QuotationGroup(Guid Id, Guid ProjectId, string Name, IReadOnlyList<Guid> LineIds);

public enum QuotationQuotaKind { None, Principal, Reserved }

public sealed record QuotationResultGroup(
    Guid Id, int Number, Guid SourceGroupId, string Name, QuotationQuotaKind Kind, Guid? PairedGroupId);

public sealed record QuotationPosition(
    int Number, Guid LineId, Guid? ResultGroupId, QuotationQuotaKind Kind,
    decimal Quantity, decimal UnitPrice, decimal TotalPrice);

public sealed record QuotationOrganizationSnapshot
{
    public int Version { get; init; } = 1;
    public DateTimeOffset AppliedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Signature { get; init; } = string.Empty;
    public IReadOnlyList<QuotationResultGroup> Groups { get; init; } = [];
    public IReadOnlyList<QuotationPosition> Positions { get; init; } = [];

    public string ItemNumbers(Guid lineId) => string.Join(" e ", Positions
        .Where(position => position.LineId == lineId).OrderBy(position => position.Number)
        .Select(position => position.Number));

    public string GroupNumbers(Guid lineId) => string.Join(" / ", Positions
        .Where(position => position.LineId == lineId && position.ResultGroupId is not null)
        .OrderBy(position => position.Number)
        .Select(position => Groups.First(group => group.Id == position.ResultGroupId))
        .Select(group => $"Grupo {group.Number} — {KindLabel(group.Kind)}"));

    public static string KindLabel(QuotationQuotaKind kind) => kind switch
    {
        QuotationQuotaKind.Principal => "Principal",
        QuotationQuotaKind.Reserved => "Reservada",
        _ => "Sem cota"
    };

    public string ToJson() => JsonSerializer.Serialize(this);
    public static QuotationOrganizationSnapshot? FromJson(string? json) => string.IsNullOrEmpty(json)
        ? null
        : JsonSerializer.Deserialize<QuotationOrganizationSnapshot>(json)
          ?? throw new InvalidDataException("A organização salva é inválida.");
}

public static class QuotationQuantity
{
    public static bool IsValid(decimal value) => value >= 0 && value == decimal.Truncate(value);

    public static void Validate(decimal value)
    {
        if (!IsValid(value))
            throw new ArgumentException("A quantidade solicitada deve ser inteira e não pode ser negativa.");
    }
}
