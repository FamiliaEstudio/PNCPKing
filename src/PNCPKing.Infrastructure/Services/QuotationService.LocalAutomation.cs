using System.Diagnostics;
using PNCPKing.Core.Interfaces;
using PNCPKing.Core.Models;
using PNCPKing.Core.Search;

namespace PNCPKing.Infrastructure.Services;

public sealed record LocalQuotationAutomationProgress(
    int CompletedItems, int TotalItems, double ItemProgress, string Message,
    QuotationLineAnalysis? UpdatedAnalysis = null)
{
    public double Percentage => TotalItems == 0 ? 100 :
        Math.Clamp((CompletedItems + ItemProgress) * 100d / TotalItems, 0, 100);
}

public sealed partial class QuotationService
{
    // One worker, one item and one discovery page at a time. SQLite's synchronous
    // work and basket calculation must never run on the WPF dispatcher.
    public Task CollectLocalAutomationAsync(QuotationAutomationRun run, IPriceCacheRepository prices,
        IProgress<LocalQuotationAutomationProgress>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        if (run.Mode != QuotationAutomationMode.FixedBatches)
            throw new InvalidOperationException("Esta coleta é exclusiva da importação pelo Excel.");
        await repository.UpdateAutomationRunStateAsync(run.Id, QuotationAutomationRunState.Running,
            "Pesquisando preços locais e montando cestas.", cancellationToken).ConfigureAwait(false);
        var pending = (await repository.GetLinesAsync(run.ProjectId, cancellationToken).ConfigureAwait(false))
            .Where(line => line.AutomationRunId == run.Id && line.AutomationState is
                QuotationAutomationItemState.Pending or QuotationAutomationItemState.Failed)
            .OrderBy(line => line.DisplayOrder).ToArray();

        for (var index = 0; index < pending.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = pending[index];
            var rows = new Dictionary<(string Contract, long Item, long Result), ItemSearchRow>();
            var input = new QuotationLineInput(line.Description, line.RequestedQuantity,
                line.RequestedUnit, line.MinimumUnitPrice, line.MaximumUnitPrice)
            { Weights = line.Weights, RequestedBasketSize = line.RequestedBasketSize };
            var prefix = $"Item {index + 1:N0}/{pending.Length:N0} — {line.EffectiveDisplayName}. ";
            void Report(double fraction, string message, QuotationLineAnalysis? analysis = null) =>
                progress?.Report(new(index, pending.Length, fraction, prefix + message, analysis));
            async Task PreservePartialAsync()
            {
                if (rows.Count > 0)
                    await CaptureSampleAsync(run.ProjectId, line.Id, input, rows.Values.ToArray(),
                        CancellationToken.None).ConfigureAwait(false);
            }

            await repository.UpdateAutomationItemStateAsync(line.Id, QuotationAutomationItemState.Running,
                "Pesquisa automática em andamento.", cancellationToken).ConfigureAwait(false);
            try
            {
                var prompts = new[]
                {
                    (Level: PromptMatchLevel.Restrictive, Text: line.SearchText),
                    (Level: PromptMatchLevel.Intermediate, Text: line.PromptSet?.IntermediateText ?? ""),
                    (Level: PromptMatchLevel.Broad, Text: line.PromptSet?.BroadText ?? "")
                }.Where(prompt => !string.IsNullOrWhiteSpace(prompt.Text))
                    .SelectMany(prompt => (line.PromptSet?.Origin == SearchPromptOrigin.Excel
                            ? SearchText.ParseMainCriteria(prompt.Text) : [SearchText.Parse(prompt.Text)])
                        .Select(expression => (prompt.Level, Expression: expression))).ToArray();
                if (prompts.Length == 0) throw new SearchQueryException("Informe uma pesquisa para o item.");
                for (var promptIndex = 0; promptIndex < prompts.Length; promptIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (level, expression) = prompts[promptIndex];
                    var query = new SearchQuery(expression.OriginalText, run.GeoFilter,
                        run.StartDate, run.EndDate, SearchSort.Nearest, 1, 200);
                    // Reuse the main search reader without changing its SQL, indexes,
                    // cursors, page size or resource tuning. G is a limit per criterion.
                    var sequence = new MainSearchLocalSequence(prices, query, [expression]);
                    Report(.8 * promptIndex / prompts.Length,
                        $"Prompt {promptIndex + 1}/{prompts.Length}: {expression.OriginalText}. {rows.Count:N0} preços.");
                    for (var pageNumber = 0; pageNumber < line.RequestedBatchCount && sequence.HasMore; pageNumber++)
                    {
                        var lastReport = Stopwatch.StartNew();
                        void AddRows(IReadOnlyList<ItemSearchRow> batch)
                        {
                            foreach (var row in batch)
                            {
                                var key = (row.Contract.PncpId, row.Item.ItemNumber, row.Result?.ResultSequence ?? 0);
                                // The first match retains the most restrictive provenance.
                                rows.TryAdd(key, row with { MatchedPromptLevel = level,
                                    MatchedSearchText = expression.OriginalText });
                            }
                        }
                        var fraction = .8 * (promptIndex + pageNumber / (double)line.RequestedBatchCount) / prompts.Length;
                        var page = await sequence.LoadNextAsync(line.MinimumUnitPrice, line.MaximumUnitPrice,
                            (_, value) =>
                            {
                                AddRows(value.Rows);
                                if (lastReport.ElapsedMilliseconds < 200) return;
                                Report(fraction, $"Prompt {promptIndex + 1}/{prompts.Length}, " +
                                    $"página {pageNumber + 1}/{line.RequestedBatchCount}; {rows.Count:N0} preços.");
                                lastReport.Restart();
                            }, cancellationToken).ConfigureAwait(false);
                        AddRows(page?.Rows ?? []);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                Report(.85, $"Montando cestas com {rows.Count:N0} preços coletados…");
                var analysis = await CaptureSampleAsync(run.ProjectId, line.Id, input,
                    rows.Values.ToArray(), cancellationToken).ConfigureAwait(false);
                var recommended = analysis.Baskets.FirstOrDefault(basket => basket.IsRecommended);
                if (recommended is not null)
                    await ConfirmBasketAsync(analysis, recommended.Key, cancellationToken).ConfigureAwait(false);
                var state = recommended is null ? QuotationAutomationItemState.Insufficient :
                    recommended.References.Count >= line.RequestedBasketSize
                        ? QuotationAutomationItemState.Completed : QuotationAutomationItemState.CompletedWithWarning;
                var message = state switch
                {
                    QuotationAutomationItemState.Completed => "Cesta recomendada selecionada automaticamente.",
                    QuotationAutomationItemState.CompletedWithWarning =>
                        $"Cesta recomendada reduzida para {recommended!.References.Count:N0} de {line.RequestedBasketSize:N0} preços.",
                    _ => $"Nenhuma cesta automática formada. {analysis.References.Count:N0} preço(s) preservado(s) " +
                        $"para revisão; {analysis.EligibleCount:N0} referência(s) elegível(is)."
                };
                await repository.UpdateAutomationItemStateAsync(line.Id, state, message, cancellationToken)
                    .ConfigureAwait(false);
                Report(1, message, analysis with { Line = analysis.Line with
                {
                    AutomationState = state, AutomationMessage = message,
                    SelectionConfirmed = recommended is not null, SelectedBasketKey = recommended?.Key
                } });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try { await PreservePartialAsync().ConfigureAwait(false); }
                finally
                {
                    await repository.UpdateAutomationItemStateAsync(line.Id, QuotationAutomationItemState.Pending,
                        "Execução interrompida; pronta para retomar.", CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
            catch (Exception exception)
            {
                var message = exception.Message;
                try { await PreservePartialAsync().ConfigureAwait(false); }
                catch (Exception saveError) { message += $" Não foi possível salvar a amostra parcial: {saveError.Message}"; }
                await repository.UpdateAutomationItemStateAsync(line.Id, QuotationAutomationItemState.Failed,
                    message, CancellationToken.None).ConfigureAwait(false);
                Report(1, message);
            }
        }
    }, cancellationToken);
}
