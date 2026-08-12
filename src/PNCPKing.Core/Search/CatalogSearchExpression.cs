using System.Text;

namespace PNCPKing.Core.Search;

public sealed record CatalogSearchAtom(string Value, bool IsPhrase);

public sealed record CatalogSearchClause(IReadOnlyList<CatalogSearchAtom> Terms);

public sealed record CatalogSearchExpression(
    IReadOnlyList<CatalogSearchClause> Alternatives,
    IReadOnlyList<CatalogSearchAtom> Exclusions,
    string? ExactCode)
{
    public IReadOnlyList<CatalogSearchAtom> PositiveTerms => Alternatives
        .SelectMany(clause => clause.Terms)
        .Distinct()
        .ToArray();

    public static CatalogSearchExpression Parse(string? text)
    {
        var source = SearchText.Sanitize(text).Trim();
        if (source.Length == 0)
        {
            throw new SearchQueryException("Informe uma descrição ou código para pesquisar no catálogo.");
        }

        var clauses = new List<CatalogSearchClause>();
        var current = new List<CatalogSearchAtom>();
        var exclusions = new List<CatalogSearchAtom>();
        var requiresTerm = false;
        for (var index = 0; index < source.Length;)
        {
            if (char.IsWhiteSpace(source[index]))
            {
                index++;
                continue;
            }

            if (source[index] == '+')
            {
                if (current.Count == 0 || requiresTerm)
                {
                    throw new SearchQueryException("O operador '+' precisa ficar entre dois termos positivos.");
                }

                requiresTerm = true;
                index++;
                continue;
            }

            if (source[index] == '|')
            {
                CompleteAlternative(clauses, current, ref requiresTerm);
                index++;
                continue;
            }

            var excluded = source[index] == '-';
            if (excluded)
            {
                index++;
                if (index >= source.Length || char.IsWhiteSpace(source[index]))
                {
                    throw new SearchQueryException("Use '-' colado à palavra ou frase que deseja excluir.");
                }
            }

            var atom = ReadAtom(source, ref index);
            if (!atom.IsPhrase && atom.Value is "ou" or "or")
            {
                if (excluded)
                {
                    throw new SearchQueryException("Não use um operador como exclusão.");
                }

                CompleteAlternative(clauses, current, ref requiresTerm);
                continue;
            }

            if (excluded)
            {
                exclusions.Add(atom);
            }
            else
            {
                current.Add(atom);
                requiresTerm = false;
            }
        }

        if (requiresTerm)
        {
            throw new SearchQueryException("A expressão não pode terminar com um operador.");
        }

        if (current.Count > 0)
        {
            clauses.Add(new CatalogSearchClause(current.ToArray()));
        }

        if (clauses.Count == 0)
        {
            throw new SearchQueryException("Informe ao menos uma palavra, frase ou código positivo.");
        }

        var exactCode = source.All(char.IsDigit) ? source : null;
        return new CatalogSearchExpression(clauses, exclusions.Distinct().ToArray(), exactCode);
    }

    private static void CompleteAlternative(
        ICollection<CatalogSearchClause> clauses,
        List<CatalogSearchAtom> current,
        ref bool requiresTerm)
    {
        if (current.Count == 0 || requiresTerm)
        {
            throw new SearchQueryException("O operador OU precisa ficar entre duas expressões positivas.");
        }

        clauses.Add(new CatalogSearchClause(current.ToArray()));
        current.Clear();
        requiresTerm = true;
    }

    private static CatalogSearchAtom ReadAtom(string source, ref int index)
    {
        if (source[index] is '"' or '“')
        {
            var opening = source[index++];
            var closing = opening == '“' ? '”' : '"';
            var builder = new StringBuilder();
            while (index < source.Length && source[index] != closing)
            {
                builder.Append(source[index++]);
            }

            if (index >= source.Length || builder.Length == 0)
            {
                throw new SearchQueryException("Feche a frase pesquisada com aspas.");
            }

            index++;
            return CreateAtom(builder.ToString(), true);
        }

        var start = index;
        while (index < source.Length && !char.IsWhiteSpace(source[index]) && source[index] is not '+' and not '|')
        {
            index++;
        }

        return CreateAtom(source[start..index], false);
    }

    private static CatalogSearchAtom CreateAtom(string value, bool isPhrase)
    {
        var normalized = SearchText.Normalize(value);
        if (normalized.Length == 0)
        {
            throw new SearchQueryException("A pesquisa contém um termo vazio ou inválido.");
        }

        return new CatalogSearchAtom(normalized, isPhrase);
    }
}
