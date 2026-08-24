using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Services;

public sealed class AiQuotationDraftService : IAiQuotationDraftService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IPdfTextIndexService _textIndexService;
    private readonly IPdfToMarkdownConverter _markdownConverter;
    private readonly IAiQuotationProvider _provider;
    private readonly IAiDraftCache _cache;
    private readonly string _cacheRoot;

    public AiQuotationDraftService(
        IPdfTextIndexService textIndexService,
        IPdfToMarkdownConverter markdownConverter,
        IAiQuotationProvider provider,
        IAiDraftCache cache,
        string dataFolder)
    {
        _textIndexService = textIndexService;
        _markdownConverter = markdownConverter;
        _provider = provider;
        _cache = cache;
        _cacheRoot = Path.Combine(dataFolder, "ai-automation-cache");
    }

    public async Task<AiQuotationDraft> CreateAsync(
        AiDraftAnalysisRequest request,
        IProgress<AiAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(request.PdfPath))
        {
            throw new FileNotFoundException("O PDF selecionado não existe.", request.PdfPath);
        }

        progress?.Report(new AiAnalysisProgress(
            AiAnalysisStage.ReadingPdf,
            0,
            1,
            "Calculando a identidade do PDF…"));
        var pdfHash = await ComputeSha256Async(request.PdfPath, cancellationToken).ConfigureAwait(false);
        if (!request.ForceRefresh)
        {
            var cached = await _cache.LoadAsync(pdfHash, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }
        }

        var preparation = await PrepareAsync(request.PdfPath, progress, cancellationToken).ConfigureAwait(false);
        var folder = Path.Combine(_cacheRoot, preparation.PdfSha256);

        var partCount = Math.Max(1, request.ApprovedPartCount);
        var markdownParts = SplitMarkdown(preparation.Markdown, partCount);
        var rawParts = new List<RawDraft>(markdownParts.Count);
        for (var partIndex = 0; partIndex < markdownParts.Count; partIndex++)
        {
            var providerResponse = await _provider.AnalyzeAsync(
                new AiProviderRequest
                {
                    Configuration = request.Provider,
                    ApiKey = request.ApiKey,
                    Markdown = markdownParts[partIndex],
                    MaximumOutputTokens = request.MaximumOutputTokens,
                    SafetyIdentifier = request.SafetyIdentifier
                },
                progress,
                cancellationToken).ConfigureAwait(false);
            await WriteTextAtomicAsync(
                Path.Combine(
                    folder,
                    markdownParts.Count == 1
                        ? "last-response.json"
                        : $"last-response-part-{partIndex + 1:N0}.json"),
                providerResponse.Json,
                cancellationToken).ConfigureAwait(false);
            rawParts.Add(
                JsonSerializer.Deserialize<RawDraft>(providerResponse.Json, JsonOptions)
                ?? throw new InvalidDataException($"A resposta da parte {partIndex + 1:N0} está vazia."));
        }

        progress?.Report(new AiAnalysisProgress(
            AiAnalysisStage.Validating,
            0,
            1,
            "Validando itens, origens e critérios de pesquisa localmente…"));
        var rawItems = rawParts
            .SelectMany(part => part.Items)
            .GroupBy(
                item => $"{item.SourceOrder}|{SearchText.Normalize(item.SourceNumber)}|" +
                        $"{SearchText.Normalize(item.Description)}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var raw = new RawDraft
        {
            DeclaredItemCount = markdownParts.Count > 1
                ? Math.Max(
                    rawItems.Length,
                    rawParts.Select(part => part.DeclaredItemCount).DefaultIfEmpty().Max())
                : rawParts.Select(part => part.DeclaredItemCount).DefaultIfEmpty().Max(),
            Items = rawItems,
            Warnings = rawParts
                .SelectMany(part => part.Warnings)
                .Concat(markdownParts.Count > 1
                    ? [$"O documento foi analisado em {markdownParts.Count:N0} partes autorizadas e reconciliado localmente."]
                    : [])
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            ContractSearchPrompts = rawParts
                .SelectMany(part => part.ContractSearchPrompts)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
        var mapped = raw.Items
            .Select((item, indexValue) => MapItem(pdfHash, item, indexValue + 1))
            .OrderBy(item => item.SourceOrder)
            .ToArray();
        var warnings = preparation.Warnings.Concat(raw.Warnings).Distinct(StringComparer.Ordinal).ToList();
        var contractPrompts = new List<string>();
        var normalizedContractPrompts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contractPrompt in raw.ContractSearchPrompts)
        {
            try
            {
                var normalizedPrompt = SearchText.NormalizeContractCandidatePrompt(contractPrompt);
                if (normalizedPrompt.Length > 0 && normalizedContractPrompts.Add(normalizedPrompt))
                {
                    contractPrompts.Add(normalizedPrompt);
                }
            }
            catch (SearchQueryException exception)
            {
                warnings.Add($"Prompt de contratação ignorado: {exception.Message}");
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

        mapped = mapped.Select(item => item with
        {
            SearchText = SearchText.ReplaceContractCandidates(item.SearchText, contractPrompts),
            IntermediateSearchText = SearchText.ReplaceContractCandidates(
                item.IntermediateSearchText,
                contractPrompts),
            BroadSearchText = SearchText.ReplaceContractCandidates(item.BroadSearchText, contractPrompts)
        }).ToArray();
        var blocking = mapped.Any(item => item.HasBlockingError);
        if (raw.DeclaredItemCount > 0 && raw.DeclaredItemCount != mapped.Length)
        {
            warnings.Add(
                $"O documento declara {raw.DeclaredItemCount:N0} item(ns), mas a IA retornou " +
                $"{mapped.Length:N0}. A discrepância precisa ser resolvida antes da pesquisa.");
            blocking = true;
        }

        var repeatedNumbers = mapped
            .Where(item => item.SourceNumber.Length > 0)
            .GroupBy(item => SearchText.Normalize(item.SourceNumber), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (repeatedNumbers.Length > 0)
        {
            warnings.Add(
                $"Numeração repetida detectada: {string.Join(", ", repeatedNumbers)}. " +
                "Nenhuma linha foi mesclada automaticamente.");
            blocking = true;
        }

        var positiveOrders = mapped.Select(item => item.SourceOrder).Where(value => value > 0).ToArray();
        if (positiveOrders.Length > 0)
        {
            var missing = Enumerable.Range(positiveOrders.Min(), positiveOrders.Max() - positiveOrders.Min() + 1)
                .Except(positiveOrders)
                .Take(25)
                .ToArray();
            if (missing.Length > 0)
            {
                warnings.Add(
                    $"Há posições ausentes na sequência: {string.Join(", ", missing)}. " +
                    "Revise o documento antes da pesquisa.");
                blocking = true;
            }
        }

        var draft = new AiQuotationDraft
        {
            Id = Guid.NewGuid(),
            PdfSha256 = preparation.PdfSha256,
            SourcePath = Path.GetFullPath(request.PdfPath),
            MarkdownPath = preparation.MarkdownPath,
            CreatedAt = DateTimeOffset.UtcNow,
            ProviderId = request.Provider.Id,
            Model = request.Provider.Model,
            DeclaredItemCount = raw.DeclaredItemCount,
            Items = mapped,
            ContractSearchPrompts = contractPrompts,
            Warnings = warnings,
            HasBlockingError = blocking
        };
        progress?.Report(new AiAnalysisProgress(
            AiAnalysisStage.SavingDraft,
            0,
            1,
            "Salvando rascunho retomável sem a chave de API…"));
        await _cache.SaveAsync(draft, preparation.Markdown, cancellationToken).ConfigureAwait(false);
        progress?.Report(new AiAnalysisProgress(
            AiAnalysisStage.Completed,
            1,
            1,
            $"{mapped.Length:N0} item(ns) estruturado(s)."));
        return draft;
    }

    private static IReadOnlyList<string> SplitMarkdown(string markdown, int requestedParts)
    {
        if (requestedParts <= 1)
        {
            return [markdown];
        }

        var sections = new List<string>();
        var current = new StringBuilder();
        foreach (var line in markdown.Split('\n'))
        {
            if (line.StartsWith("## Página ", StringComparison.Ordinal) && current.Length > 0)
            {
                sections.Add(current.ToString());
                current.Clear();
            }

            current.AppendLine(line);
        }

        if (current.Length > 0)
        {
            sections.Add(current.ToString());
        }

        if (sections.Count <= 1)
        {
            throw new InvalidOperationException(
                "O Markdown não contém marcadores de página suficientes para uma divisão segura.");
        }

        var parts = new List<string>(Math.Min(requestedParts, sections.Count));
        var targetSize = Math.Max(1, (int)Math.Ceiling(markdown.Length / (double)requestedParts));
        var buffer = new StringBuilder();
        foreach (var section in sections)
        {
            if (buffer.Length > 0 && buffer.Length + section.Length > targetSize &&
                parts.Count < requestedParts - 1)
            {
                parts.Add(buffer.ToString());
                buffer.Clear();
            }

            buffer.Append(section);
        }

        if (buffer.Length > 0)
        {
            parts.Add(buffer.ToString());
        }

        return parts;
    }

    public async Task<AiMarkdownPreparation> PrepareAsync(
        string pdfPath,
        IProgress<AiAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        if (!File.Exists(pdfPath))
        {
            throw new FileNotFoundException("O PDF selecionado não existe.", pdfPath);
        }

        progress?.Report(new AiAnalysisProgress(
            AiAnalysisStage.ReadingPdf,
            0,
            1,
            "Calculando a identidade do PDF…"));
        var pdfHash = await ComputeSha256Async(pdfPath, cancellationToken).ConfigureAwait(false);
        var folder = Path.Combine(_cacheRoot, pdfHash);
        Directory.CreateDirectory(folder);
        var cachedPdfPath = Path.GetFullPath(pdfPath);

        var markdownPath = Path.Combine(folder, "document.md");
        if (File.Exists(markdownPath))
        {
            var cachedMarkdown = await File.ReadAllTextAsync(markdownPath, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cachedMarkdown))
            {
                return new AiMarkdownPreparation
                {
                    PdfSha256 = pdfHash,
                    SourcePath = Path.GetFullPath(pdfPath),
                    CachedPdfPath = cachedPdfPath,
                    MarkdownPath = markdownPath,
                    Markdown = cachedMarkdown,
                    ProbableItemCount = EstimateProbableItemCount(cachedMarkdown)
                };
            }
        }

        var pdf = new CachedPdfDocument
        {
            LocalPath = cachedPdfPath,
            Sha256 = pdfHash,
            DocumentSequence = 0,
            DocumentTitle = Path.GetFileName(pdfPath),
            IndexCachePath = Path.Combine(folder, "document-index.json")
        };
        progress?.Report(new AiAnalysisProgress(
            AiAnalysisStage.Indexing,
            0,
            1,
            "Extraindo texto nativo e aplicando OCR somente onde necessário…"));
        var documentProgress = progress is null
            ? null
            : new Progress<DocumentProcessingProgress>(value =>
                progress.Report(new AiAnalysisProgress(
                    AiAnalysisStage.Indexing,
                    value.Completed,
                    Math.Max(1, value.Total),
                    value.Message)));
        var index = await _textIndexService.BuildAsync(pdf, documentProgress, cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new AiAnalysisProgress(
            AiAnalysisStage.ConvertingMarkdown,
            0,
            1,
            "Convertendo o índice local para Markdown…"));
        var conversion = await _markdownConverter.ConvertAsync(
            index,
            new MarkdownConversionOptions(),
            cancellationToken).ConfigureAwait(false);
        await WriteTextAtomicAsync(markdownPath, conversion.Markdown, cancellationToken).ConfigureAwait(false);
        return new AiMarkdownPreparation
        {
            PdfSha256 = pdfHash,
            SourcePath = Path.GetFullPath(pdfPath),
            CachedPdfPath = cachedPdfPath,
            MarkdownPath = markdownPath,
            Markdown = conversion.Markdown,
            ProbableItemCount = EstimateProbableItemCount(conversion.Markdown),
            Warnings = conversion.Warnings
        };
    }

    private static int EstimateProbableItemCount(string markdown)
    {
        var numbers = new HashSet<int>();
        var insideItemTable = false;
        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.TrimStart(' ', '|', '#', '-', '*');
            if (trimmed.StartsWith("Item Quant", StringComparison.OrdinalIgnoreCase))
            {
                insideItemTable = true;
                continue;
            }

            if (insideItemTable &&
                (trimmed.StartsWith("8.3.", StringComparison.Ordinal) ||
                 trimmed.StartsWith("8.3 ", StringComparison.Ordinal)))
            {
                break;
            }

            var digitCount = 0;
            while (digitCount < trimmed.Length && digitCount < 6 && char.IsDigit(trimmed[digitCount]))
            {
                digitCount++;
            }

            if (digitCount == 0 ||
                digitCount >= trimmed.Length ||
                trimmed[digitCount] == '.' ||
                !char.IsWhiteSpace(trimmed[digitCount]) && trimmed[digitCount] != '|' ||
                !int.TryParse(trimmed[..digitCount], out var number) ||
                number is < 1 or > 100_000)
            {
                continue;
            }

            if (insideItemTable)
            {
                numbers.Add(number);
            }
        }

        if (numbers.Contains(1))
        {
            var maximum = numbers.Max();
            if (maximum <= 10_000 && numbers.Count >= maximum * 0.80)
            {
                return maximum;
            }
        }

        return numbers.Count > 0 ? numbers.Count : 1;
    }

    private static AiQuotationDraftItem MapItem(string pdfHash, RawItem raw, int fallbackOrder)
    {
        var order = raw.SourceOrder > 0 ? raw.SourceOrder : fallbackOrder;
        var warnings = raw.Warnings.ToList();
        var positiveGroups = raw.PositiveGroups
            .Select(group => new AiPositiveGroup(
                group.Terms
                    .Where(term => !string.IsNullOrWhiteSpace(term.Text))
                    .Select(term => new AiSearchTerm(term.Text.Trim(), term.IsPhrase))
                    .ToArray()))
            .Where(group => group.Terms.Count > 0)
            .ToArray();
        var exclusions = raw.Exclusions
            .Where(term => !string.IsNullOrWhiteSpace(term.Text))
            .Select(term => new AiSearchTerm(term.Text.Trim(), term.IsPhrase))
            .ToArray();
        var units = raw.AcceptedUnits
            .Select(SearchText.Normalize)
            .Where(unit => unit.Length > 0 && !unit.Any(char.IsWhiteSpace))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var quantity = raw.Quantity is > 0 ? raw.Quantity : null;
        var unitPrice = raw.EstimatedUnitPrice is > 0 ? raw.EstimatedUnitPrice : null;
        var totalPrice = raw.EstimatedTotalPrice is > 0 ? raw.EstimatedTotalPrice : null;
        var estimateEvidence = MapEvidence(raw.EstimateEvidence);
        if (unitPrice is null && totalPrice is > 0 && quantity is > 0)
        {
            unitPrice = QuotationMoney.TruncateToCents(totalPrice.Value / quantity.Value);
            estimateEvidence = estimateEvidence with { Origin = AiFieldOrigin.Calculated };
        }
        else if (totalPrice is null && unitPrice is > 0 && quantity is > 0)
        {
            totalPrice = QuotationMoney.TruncateToCents(unitPrice.Value * quantity.Value);
            estimateEvidence = estimateEvidence with { Origin = AiFieldOrigin.Calculated };
        }

        var blocking = false;
        string searchText;
        try
        {
            searchText = AiSearchPromptFormatter.Format(positiveGroups, exclusions, units);
        }
        catch (Exception exception) when (exception is SearchQueryException or ArgumentException)
        {
            searchText = string.Empty;
            warnings.Add($"Critério de pesquisa inválido: {exception.Message}");
            blocking = true;
        }

        var intermediate = ValidateOrFallbackPrompt(
            raw.IntermediateSearchText,
            searchText,
            warnings,
            "intermediário");
        var broad = ValidateOrFallbackPrompt(
            raw.BroadSearchText,
            BuildFallbackPrompt(searchText, raw.Description),
            warnings,
            "amplo");

        if (string.IsNullOrWhiteSpace(raw.Description))
        {
            warnings.Add("Descrição ausente.");
            blocking = true;
        }

        if (quantity is null)
        {
            warnings.Add("Quantidade ausente ou inválida.");
            blocking = true;
        }

        if (string.IsNullOrWhiteSpace(raw.Unit))
        {
            warnings.Add("Unidade ausente.");
            blocking = true;
        }

        var stableValue = $"{pdfHash}|{order}|{raw.SourceNumber}|{raw.Description}";
        var stableHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableValue)))
            .ToLowerInvariant();
        return new AiQuotationDraftItem
        {
            StableId = $"ai:{stableHash[..24]}",
            SourceOrder = order,
            SourceNumber = raw.SourceNumber.Trim(),
            Description = raw.Description.Trim(),
            Quantity = quantity,
            Unit = raw.Unit.Trim(),
            EstimatedUnitPrice = unitPrice,
            EstimatedTotalPrice = totalPrice,
            PositiveGroups = positiveGroups,
            Exclusions = exclusions,
            AcceptedUnits = units,
            SearchText = searchText,
            IntermediateSearchText = intermediate,
            BroadSearchText = broad,
            DescriptionEvidence = MapEvidence(raw.DescriptionEvidence),
            QuantityEvidence = MapEvidence(raw.QuantityEvidence),
            UnitEvidence = MapEvidence(raw.UnitEvidence),
            EstimateEvidence = estimateEvidence,
            SearchEvidence = MapEvidence(raw.SearchEvidence),
            Warnings = warnings,
            HasBlockingError = blocking,
            IsSelected = !blocking
        };
    }

    private static AiFieldEvidence MapEvidence(RawEvidence raw) =>
        new()
        {
            Origin = raw.Origin?.Trim().ToLowerInvariant() switch
            {
                "found" or "encontrado" => AiFieldOrigin.Found,
                "calculated" or "calculado" => AiFieldOrigin.Calculated,
                "inferred" or "inferido" => AiFieldOrigin.Inferred,
                _ => AiFieldOrigin.Missing
            },
            Confidence = Math.Clamp(raw.Confidence, 0m, 1m),
            Pages = raw.Pages.Where(page => page > 0).Distinct().Order().ToArray(),
            Excerpt = raw.Excerpt.Trim()
        };

    private static string ValidateOrFallbackPrompt(
        string candidate,
        string fallback,
        ICollection<string> warnings,
        string label)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            try
            {
                var trimmed = candidate.Trim();
                _ = SearchText.Parse(trimmed);
                return trimmed;
            }
            catch (SearchQueryException exception)
            {
                warnings.Add($"Prompt {label} inválido; usado fallback local: {exception.Message}");
            }
        }

        return fallback;
    }

    private static string BuildFallbackPrompt(string prompt, string description)
    {
        var source = string.IsNullOrWhiteSpace(prompt) ? description : prompt;
        try
        {
            var parsed = SearchText.Parse(source);
            var words = parsed.PositiveText
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(value => value.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            return words.Length > 0 ? string.Join(" ", words) : SearchText.Normalize(description);
        }
        catch (SearchQueryException)
        {
            return SearchText.Normalize(description);
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WriteTextAtomicAsync(
        string path,
        string text,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed class RawDraft
    {
        [JsonPropertyName("declared_item_count")]
        public int DeclaredItemCount { get; init; }

        [JsonPropertyName("warnings")]
        public string[] Warnings { get; init; } = [];

        [JsonPropertyName("contract_search_prompts")]
        public string[] ContractSearchPrompts { get; init; } = [];

        [JsonPropertyName("items")]
        public RawItem[] Items { get; init; } = [];
    }

    private sealed class RawItem
    {
        [JsonPropertyName("source_order")]
        public int SourceOrder { get; init; }

        [JsonPropertyName("source_number")]
        public string SourceNumber { get; init; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; init; } = string.Empty;

        [JsonPropertyName("quantity")]
        public decimal? Quantity { get; init; }

        [JsonPropertyName("unit")]
        public string Unit { get; init; } = string.Empty;

        [JsonPropertyName("estimated_unit_price")]
        public decimal? EstimatedUnitPrice { get; init; }

        [JsonPropertyName("estimated_total_price")]
        public decimal? EstimatedTotalPrice { get; init; }

        [JsonPropertyName("positive_groups")]
        public RawGroup[] PositiveGroups { get; init; } = [];

        [JsonPropertyName("exclusions")]
        public RawTerm[] Exclusions { get; init; } = [];

        [JsonPropertyName("accepted_units")]
        public string[] AcceptedUnits { get; init; } = [];

        [JsonPropertyName("intermediate_search_text")]
        public string IntermediateSearchText { get; init; } = string.Empty;

        [JsonPropertyName("broad_search_text")]
        public string BroadSearchText { get; init; } = string.Empty;

        [JsonPropertyName("description_evidence")]
        public RawEvidence DescriptionEvidence { get; init; } = new();

        [JsonPropertyName("quantity_evidence")]
        public RawEvidence QuantityEvidence { get; init; } = new();

        [JsonPropertyName("unit_evidence")]
        public RawEvidence UnitEvidence { get; init; } = new();

        [JsonPropertyName("estimate_evidence")]
        public RawEvidence EstimateEvidence { get; init; } = new();

        [JsonPropertyName("search_evidence")]
        public RawEvidence SearchEvidence { get; init; } = new();

        [JsonPropertyName("warnings")]
        public string[] Warnings { get; init; } = [];
    }

    private sealed class RawGroup
    {
        [JsonPropertyName("terms")]
        public RawTerm[] Terms { get; init; } = [];
    }

    private sealed class RawTerm
    {
        [JsonPropertyName("text")]
        public string Text { get; init; } = string.Empty;

        [JsonPropertyName("is_phrase")]
        public bool IsPhrase { get; init; }
    }

    private sealed class RawEvidence
    {
        [JsonPropertyName("origin")]
        public string Origin { get; init; } = "missing";

        [JsonPropertyName("confidence")]
        public decimal Confidence { get; init; }

        [JsonPropertyName("pages")]
        public int[] Pages { get; init; } = [];

        [JsonPropertyName("excerpt")]
        public string Excerpt { get; init; } = string.Empty;
    }
}
