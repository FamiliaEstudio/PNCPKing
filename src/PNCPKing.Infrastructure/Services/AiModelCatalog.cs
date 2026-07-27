using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

public static class AiModelCatalog
{
    public const string CatalogVersion = "2026-07-24";

    public static IReadOnlyList<AiModelProfile> OpenAiProfiles { get; } =
    [
        new(
            "openai-economy",
            "Econômico",
            "gpt-5.6-luna",
            "low",
            1_050_000,
            128_000,
            1m,
            6m),
        new(
            "openai-balanced",
            "Equilibrado",
            "gpt-5.6-terra",
            "medium",
            1_050_000,
            128_000,
            2.5m,
            15m),
        new(
            "openai-quality",
            "Qualidade máxima",
            "gpt-5.6-sol",
            "high",
            1_050_000,
            128_000,
            5m,
            30m)
    ];

    public static AiModelProfile DefaultOpenAiProfile => OpenAiProfiles[1];

    public static AiProviderConfiguration CreateOpenAiConfiguration(AiModelProfile profile) =>
        new()
        {
            Id = profile.Id,
            DisplayName = $"OpenAI — {profile.DisplayName}",
            Endpoint = new Uri("https://api.openai.com/v1/"),
            Model = profile.Model,
            Protocol = AiProviderProtocol.Responses,
            OutputMode = AiStructuredOutputMode.JsonSchema,
            IsOpenAi = true,
            ContextWindow = profile.ContextWindow,
            MaximumOutputTokens = profile.MaximumOutputTokens,
            ReasoningEffort = profile.ReasoningEffort
        };

    public static AiModelProfile? FindOpenAi(string model) =>
        OpenAiProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Model, model, StringComparison.OrdinalIgnoreCase));
}
