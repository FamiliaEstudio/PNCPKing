namespace PNCPKing.Core.Models;

public enum QuotationDescriptionLocation { Item32, Item5, Memorial, OtherItem }

public sealed record QuotationWordExportOptions
{
    public QuotationDescriptionLocation DescriptionLocation { get; init; }
    public string OtherItemNumber { get; init; } = string.Empty;
    public bool IsPriceRegistration { get; init; }
    public decimal? MinimumOrderPercentage { get; init; }

    public string DescriptionSentence => DescriptionLocation == QuotationDescriptionLocation.Memorial
        ? "A especificação detalhada constará no Memorial Descritivo."
        : $"A especificação detalhada constará no item {DescriptionLocation switch
        {
            QuotationDescriptionLocation.Item32 => "3.2",
            QuotationDescriptionLocation.Item5 => "5",
            _ => OtherItemNumber.Trim()
        }} deste Termo de Referência.";

    public void Validate()
    {
        if (!Enum.IsDefined(DescriptionLocation))
            throw new ArgumentException("Selecione o local do descritivo.");
        if (DescriptionLocation == QuotationDescriptionLocation.OtherItem &&
            (string.IsNullOrWhiteSpace(OtherItemNumber) ||
             OtherItemNumber.Trim().Split('.').Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit))))
            throw new ArgumentException("Informe somente o número do item do descritivo, por exemplo 7.1.");
        if (IsPriceRegistration && (MinimumOrderPercentage is null or <= 0 or > 100))
            throw new ArgumentException("Informe um percentual maior que zero e até 100% para a quantidade mínima.");
    }

    public static void ValidateMinimumOrderQuantity(decimal? quantity)
    {
        if (quantity is { } value && (value <= 0 || !QuotationQuantity.IsValid(value)))
            throw new ArgumentException("A quantidade mínima por pedido deve ser inteira e maior que zero; deixe vazia para usar o percentual.");
    }
}
