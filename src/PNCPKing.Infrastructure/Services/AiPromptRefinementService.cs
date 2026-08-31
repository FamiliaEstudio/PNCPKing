using System.Text.Json;
using System.Text.Json.Serialization;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Services;

public sealed class AiPromptRefinementService(
    IAiQuotationProvider provider) : IAiPromptRefinementService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public async Task<AiPromptRefinementResult> RefineAsync(
        AiPromptRefinementRequest request,
        IProgress<AiAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items.Count == 0)
        {
            throw new ArgumentException("Selecione ao menos um item para retrabalhar.", nameof(request));
        }

        var existing = request.Items.Select(item => new
        {
            stable_id = item.StableId,
            description = item.Description,
            quantity = item.Quantity,
            unit = item.Unit,
            restrictive_text = SearchText.RemoveContractCandidates(item.SearchText),
            current_intermediate_text = SearchText.RemoveContractCandidates(item.IntermediateSearchText),
            current_broad_text = SearchText.RemoveContractCandidates(item.BroadSearchText)
        });
        var response = await provider.AnalyzeAsync(
            new AiProviderRequest
            {
                Configuration = request.Provider,
                ApiKey = request.ApiKey,
                Markdown = request.Markdown,
                MaximumOutputTokens = request.MaximumOutputTokens,
                SafetyIdentifier = request.SafetyIdentifier,
                GenerationKind = AiGenerationKind.PromptRefinement,
                ExistingStructuredDataJson = JsonSerializer.Serialize(existing, JsonOptions)
            },
            progress,
            cancellationToken).ConfigureAwait(false);
        var raw = JsonSerializer.Deserialize<RawResult>(response.Json, JsonOptions)
                  ?? throw new InvalidDataException("A geração de retrabalho está vazia.");
        var requested = request.Items.ToDictionary(value => value.StableId, StringComparer.Ordinal);
        var mapped = new List<AiPromptRefinementItem>(raw.Items.Length);
        foreach (var item in raw.Items)
        {
            if (!requested.TryGetValue(item.StableId, out var source))
            {
                throw new InvalidDataException($"A IA devolveu um item desconhecido: {item.StableId}.");
            }

            if (!string.Equals(
                    item.RestrictiveText.Trim(),
                    SearchText.RemoveContractCandidates(source.SearchText),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"A IA tentou alterar o prompt restritivo do item {source.SourceNumber}.");
            }

            ValidatePrompt(item.IntermediateText, "intermediário", source.SourceNumber);
            ValidatePrompt(item.BroadText, "amplo", source.SourceNumber);
            mapped.Add(new AiPromptRefinementItem(
                item.StableId,
                SearchText.RemoveContractCandidates(source.SearchText),
                item.IntermediateText.Trim(),
                item.BroadText.Trim()));
        }

        var missing = requested.Keys.Except(mapped.Select(value => value.StableId), StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"A IA omitiu {missing.Length:N0} item(ns) selecionado(s); nenhuma alteração foi aplicada.");
        }

        var contractPrompts = new List<string>(10);
        var seenPrompts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawPrompt in raw.ContractSearchPrompts)
        {
            var prompt = rawPrompt.Trim();
            if (prompt.Length == 0)
            {
                continue;
            }

            var normalized = SearchText.NormalizeContractCandidatePrompt(prompt);
            if (seenPrompts.Add(normalized))
            {
                contractPrompts.Add(normalized);
            }

            if (contractPrompts.Count == 10)
            {
                break;
            }
        }

        if (contractPrompts.Count != 10)
        {
            throw new InvalidDataException(
                $"A IA devolveu {contractPrompts.Count:N0} crivo(s) global(is) distinto(s); são necessários exatamente 10.");
        }

        return new AiPromptRefinementResult(
            mapped,
            contractPrompts,
            response.InputTokens,
            response.OutputTokens,
            raw.Warnings);
    }

    private static void ValidatePrompt(string value, string label, string itemNumber)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Prompt {label} ausente no item {itemNumber}.");
        }

        try
        {
            var parsed = SearchText.Parse(value);
            if (parsed.HasExplicitContractCandidates)
            {
                throw new SearchQueryException("A IA não deve gerar novos blocos C:.");
            }
        }
        catch (SearchQueryException exception)
        {
            throw new InvalidDataException(
                $"Prompt {label} inválido no item {itemNumber}: {exception.Message}",
                exception);
        }
    }

    private sealed class RawResult
    {
        [JsonPropertyName("warnings")]
        public string[] Warnings { get; init; } = [];

        [JsonPropertyName("contract_search_prompts")]
        public string[] ContractSearchPrompts { get; init; } = [];

        [JsonPropertyName("items")]
        public RawItem[] Items { get; init; } = [];
    }

    private sealed class RawItem
    {
        [JsonPropertyName("stable_id")]
        public string StableId { get; init; } = string.Empty;

        [JsonPropertyName("restrictive_text")]
        public string RestrictiveText { get; init; } = string.Empty;

        [JsonPropertyName("intermediate_text")]
        public string IntermediateText { get; init; } = string.Empty;

        [JsonPropertyName("broad_text")]
        public string BroadText { get; init; } = string.Empty;
    }
}
