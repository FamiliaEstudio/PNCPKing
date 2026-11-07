namespace PNCPKing.Core.Models;

public enum AiCredentialPersistence
{
    Saved,
    Section,
    OneTime
}

public enum AiProviderProtocol
{
    Responses,
    ChatCompletions
}

public enum AiStructuredOutputMode
{
    JsonSchema,
    PromptJson
}

public enum AiFieldOrigin
{
    Found,
    Calculated,
    Inferred,
    Missing
}

public sealed record AiModelProfile(
    string Id,
    string DisplayName,
    string Model,
    string ReasoningEffort,
    int ContextWindow,
    int MaximumOutputTokens,
    decimal InputUsdPerMillion,
    decimal OutputUsdPerMillion);

public sealed record AiProviderConfiguration
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required Uri Endpoint { get; init; }
    public required string Model { get; init; }
    public AiProviderProtocol Protocol { get; init; } = AiProviderProtocol.Responses;
    public AiStructuredOutputMode OutputMode { get; init; } = AiStructuredOutputMode.JsonSchema;
    public bool IsOpenAi { get; init; }
    public bool IsFree { get; init; }
    public int ContextWindow { get; init; } = 128_000;
    public int MaximumOutputTokens { get; init; } = 32_768;
    public decimal InputCostBrlPerMillion { get; init; }
    public decimal OutputCostBrlPerMillion { get; init; }
    public string ReasoningEffort { get; init; } = "medium";
}

public sealed record AiCostEstimate
{
    public long ExpectedInputTokens { get; init; }
    public long MaximumInputTokens { get; init; }
    public long ExpectedOutputTokens { get; init; }
    public long MaximumOutputTokens { get; init; }
    public decimal ExpectedCostBrl { get; init; }
    public decimal MaximumCostBrl { get; init; }
    public decimal ExchangeRate { get; init; }
    public DateOnly? ExchangeRateDate { get; init; }
    public decimal SafetyMarginPercent { get; init; }
    public bool FitsContext { get; init; }
    public int SuggestedPartCount { get; init; } = 1;
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record AiFieldEvidence
{
    public AiFieldOrigin Origin { get; init; } = AiFieldOrigin.Missing;
    public decimal Confidence { get; init; }
    public IReadOnlyList<int> Pages { get; init; } = [];
    public string Excerpt { get; init; } = string.Empty;
}

public sealed record AiSearchTerm(
    string Text,
    bool IsPhrase = false);

public sealed record AiPositiveGroup(
    IReadOnlyList<AiSearchTerm> Terms);

public sealed record AiQuotationDraftItem
{
    public required string StableId { get; init; }
    public int SourceOrder { get; init; }
    public string SourceNumber { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public decimal? Quantity { get; init; }
    public string Unit { get; init; } = string.Empty;
    public decimal? EstimatedUnitPrice { get; init; }
    public decimal? EstimatedTotalPrice { get; init; }
    public IReadOnlyList<AiPositiveGroup> PositiveGroups { get; init; } = [];
    public IReadOnlyList<AiSearchTerm> Exclusions { get; init; } = [];
    public IReadOnlyList<string> AcceptedUnits { get; init; } = [];
    public string SearchText { get; init; } = string.Empty;
    public string IntermediateSearchText { get; init; } = string.Empty;
    public string BroadSearchText { get; init; } = string.Empty;
    public AiFieldEvidence DescriptionEvidence { get; init; } = new();
    public AiFieldEvidence QuantityEvidence { get; init; } = new();
    public AiFieldEvidence UnitEvidence { get; init; } = new();
    public AiFieldEvidence EstimateEvidence { get; init; } = new();
    public AiFieldEvidence SearchEvidence { get; init; } = new();
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool HasBlockingError { get; init; }
    public bool IsSelected { get; init; } = true;
    public bool UseEstimatedPrice { get; init; }
    public int RequestedBasketSize { get; init; } = 3;
}

public sealed record AiQuotationDraft
{
    public const int CurrentAnalyzerVersion = 3;

    public required Guid Id { get; init; }
    public required string PdfSha256 { get; init; }
    public required string SourcePath { get; init; }
    public required string MarkdownPath { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string ProviderId { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int DeclaredItemCount { get; init; }
    public IReadOnlyList<AiQuotationDraftItem> Items { get; init; } = [];
    public IReadOnlyList<string> ContractSearchPrompts { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool HasBlockingError { get; init; }
    public int AnalyzerVersion { get; init; } = CurrentAnalyzerVersion;
}

public enum AiAnalysisStage
{
    ReadingPdf,
    Indexing,
    ConvertingMarkdown,
    EstimatingCost,
    CallingProvider,
    WaitingProvider,
    Validating,
    SavingDraft,
    Completed
}

public sealed record AiAnalysisProgress(
    AiAnalysisStage Stage,
    int Completed,
    int Total,
    string Message);

public sealed record AiProviderRequest
{
    public required AiProviderConfiguration Configuration { get; init; }
    public required string ApiKey { get; init; }
    public required string Markdown { get; init; }
    public required int MaximumOutputTokens { get; init; }
    public string SafetyIdentifier { get; init; } = string.Empty;
    public AiGenerationKind GenerationKind { get; init; } = AiGenerationKind.QuotationDraft;
    public string ExistingStructuredDataJson { get; init; } = string.Empty;
}

public enum AiGenerationKind
{
    QuotationDraft,
    PromptRefinement
}

public sealed record AiProviderResponse
{
    public required string Json { get; init; }
    public string ResponseId { get; init; } = string.Empty;
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public string Status { get; init; } = string.Empty;
}

public sealed record ExchangeRateQuote(
    string Currency,
    decimal SellRate,
    DateOnly Date,
    bool FromCache);

public sealed record AiDraftAnalysisRequest
{
    public required string PdfPath { get; init; }
    public required AiProviderConfiguration Provider { get; init; }
    public required string ApiKey { get; init; }
    public required int MaximumOutputTokens { get; init; }
    public string SafetyIdentifier { get; init; } = string.Empty;
    public bool ForceRefresh { get; init; }
    public int ApprovedPartCount { get; init; } = 1;
}

public sealed record AiMarkdownPreparation
{
    public required string PdfSha256 { get; init; }
    public required string SourcePath { get; init; }
    public required string CachedPdfPath { get; init; }
    public required string MarkdownPath { get; init; }
    public required string Markdown { get; init; }
    public int ProbableItemCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public enum QuotationAutomationMode
{
    FixedBatches,
    TimedRoundRobin
}

public enum EstimateResolutionStage
{
    NotApplicable,
    Within25Percent,
    Within50Percent,
    Unrestricted
}

public sealed record ItemSearchCheckpoint
{
    public long RandomPivot { get; init; }
    public ItemCandidateCursor? Cursor { get; init; }
    public int ContractsExamined { get; init; }
    public int BatchesCompleted { get; init; }
    public bool CandidateSetExhausted { get; init; }
    public EstimateResolutionStage EstimateStage { get; init; }
}

public sealed record TimedQuotationRunOptions(
    TimeSpan TimeBudget,
    bool RequireFullBasket = true,
    decimal MaximumDeviationPercent = 25m);

public enum PromptMatchLevel
{
    Restrictive,
    Intermediate,
    Broad
}

public enum SearchPromptOrigin
{
    Ai,
    User,
    Migrated
}

public enum SearchPromptValidationState
{
    Valid,
    Invalid
}

public sealed record ItemSearchPromptSet
{
    public required Guid LineId { get; init; }
    public int Version { get; init; } = 1;
    public string RestrictiveText { get; init; } = string.Empty;
    public string IntermediateText { get; init; } = string.Empty;
    public string BroadText { get; init; } = string.Empty;
    public SearchPromptOrigin Origin { get; init; } = SearchPromptOrigin.Ai;
    public SearchPromptValidationState ValidationState { get; init; } = SearchPromptValidationState.Valid;
    public PromptMatchLevel ActiveLevel { get; init; } = PromptMatchLevel.Restrictive;
    public int ContractsAtActiveLevel { get; init; }
    public int MatchedItems { get; init; }
    public int RevealedPrices { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string GetText(PromptMatchLevel level) => level switch
    {
        PromptMatchLevel.Restrictive => RestrictiveText,
        PromptMatchLevel.Intermediate => IntermediateText,
        PromptMatchLevel.Broad => BroadText,
        _ => RestrictiveText
    };

    public IReadOnlyList<(PromptMatchLevel Level, string Text)> GetActivePrompts() =>
        new[]
        {
            (Level: PromptMatchLevel.Restrictive, Text: RestrictiveText),
            (Level: PromptMatchLevel.Intermediate, Text: IntermediateText),
            (Level: PromptMatchLevel.Broad, Text: BroadText)
        }
        .Where(value => value.Level <= ActiveLevel && !string.IsNullOrWhiteSpace(value.Text))
        .ToArray();
}

public sealed record ContractSearchPrompt
{
    public required Guid RunId { get; init; }
    public int DisplayOrder { get; init; }
    public string Text { get; init; } = string.Empty;
    public long RandomPivot { get; init; }
    public ItemCandidateCursor? Cursor { get; init; }
    public bool CandidateSetExhausted { get; init; }
    public int ContractsExamined { get; init; }
    public bool IsFallback { get; init; }
}

public sealed record ContractSearchCheckpoint
{
    public required Guid RunId { get; init; }
    public required string ContractId { get; init; }
    public int PromptOrder { get; init; }
    public DateTimeOffset ProcessedAt { get; init; }
    public int MatchedItems { get; init; }
    public int RevealedPrices { get; init; }
}

public sealed record TimedQuotationProgress
{
    public TimeSpan ActiveElapsed { get; init; }
    public TimeSpan Remaining { get; init; }
    public int BatchNumber { get; init; }
    public int ContractInBatch { get; init; }
    public int ContractsInBatch { get; init; }
    public string CurrentContractId { get; init; } = string.Empty;
    public string CurrentContractPrompt { get; init; } = string.Empty;
    public int UniqueContractsProcessed { get; init; }
    public int ItemListsFromCache { get; init; }
    public int ItemListsFromApi { get; init; }
    public int MatchedItems { get; init; }
    public int RevealedPrices { get; init; }
    public int RestrictiveItems { get; init; }
    public int IntermediateItems { get; init; }
    public int BroadItems { get; init; }
    public int ResolvedItems { get; init; }
    public int ItemResultCalls { get; init; }
    public int FailedCalls { get; init; }
    public int ContractsWithoutResult { get; init; }
    public string Message { get; init; } = string.Empty;
    public Guid? UpdatedLineId { get; init; }
}

public sealed record AiPromptRefinementItem(
    string StableId,
    string RestrictiveText,
    string IntermediateText,
    string BroadText);

public sealed record AiPromptRefinementRequest
{
    public required AiProviderConfiguration Provider { get; init; }
    public required string ApiKey { get; init; }
    public required string Markdown { get; init; }
    public required IReadOnlyList<AiQuotationDraftItem> Items { get; init; }
    public int MaximumOutputTokens { get; init; }
    public string SafetyIdentifier { get; init; } = string.Empty;
}

public sealed record AiPromptRefinementResult(
    IReadOnlyList<AiPromptRefinementItem> Items,
    IReadOnlyList<string> ContractSearchPrompts,
    long InputTokens,
    long OutputTokens,
    IReadOnlyList<string> Warnings);
