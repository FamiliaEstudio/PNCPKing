namespace PNCPKing.Core.Models;

public sealed record SweetCodeLibrary(
    bool Enabled,
    IReadOnlyList<string> Expressions);
