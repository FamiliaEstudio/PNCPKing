using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using PNCPKing.App.ViewModels;
using PNCPKing.Core.Models;
using PNCPKing.Core.Quotations;
using PNCPKing.Infrastructure.Data;
using PNCPKing.Infrastructure.Services;
using Xunit.Abstractions;

namespace PNCPKing.Tests;

public sealed class QuotationDuplicatePriceCostTests(ITestOutputHelper output)
{
    // Observe SQLite statements, including each statement in multi-statement commands.
    // Keep the delegate alive while native handles can be returned to the provider's pool.
    private sealed class TraceFactory(string path) : ISqliteConnectionFactory
    {
        private readonly SqliteConnectionFactory _source = new(path);
        public string DatabasePath => _source.DatabasePath;
        public ISqliteWorkCoordinator WorkCoordinator => _source.WorkCoordinator;
        public int MigrationCacheKib => _source.MigrationCacheKib;
        public long MmapBytes => _source.MmapBytes;
        public int WorkerThreads => _source.WorkerThreads;
        public string ProfileName => _source.ProfileName;
        private static readonly AsyncLocal<TraceFactory?> Active = new();
        private static readonly Callback Observer = Observe;
        private int _reads, _opens, _writes;
        private readonly HashSet<string> _readsByLine = [];

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Callback(uint kind, IntPtr context, IntPtr statement, IntPtr sql);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_trace_v2(IntPtr db, uint mask, Callback callback, IntPtr context);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_expanded_sql(IntPtr statement);
        [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_free(IntPtr ptr);

        private static int Observe(uint kind, IntPtr context, IntPtr statement, IntPtr sql)
        {
            if (Active.Value is not { } phase) return 0;
            var text = Marshal.PtrToStringUTF8(sql)?.TrimStart() ?? "";
            if (text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || text.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            {
                phase._reads++;
                if (text.Contains("FROM quotation_references") && text.Contains("WHERE line_id = $lineId ORDER BY id"))
                {
                    var expanded = sqlite3_expanded_sql(statement);
                    try { phase._readsByLine.Add(Marshal.PtrToStringUTF8(expanded)!); }
                    finally { sqlite3_free(expanded); }
                }
            }
            else if (new[] { "DELETE", "UPDATE", "INSERT", "REPLACE", "CREATE" }.Any(verb => text.StartsWith(verb, StringComparison.OrdinalIgnoreCase)))
                phase._writes++;
            return 0;
        }

        public async Task<SqliteConnection> OpenAsync(CancellationToken token = default)
        {
            var connection = await _source.OpenAsync(token);
            if (Active.Value == this)
            {
                _opens++;
                Assert.Equal(0, sqlite3_trace_v2(connection.Handle!.DangerousGetHandle(), 1, Observer, IntPtr.Zero));
            }
            return connection;
        }

        public async Task<T> Measure<T>(string phase, Func<Task<T>> action, int reads, int opens, int writes,
            ITestOutputHelper output, IReadOnlySet<Guid>? expectedAnalysisIds = null)
        {
            _reads = _opens = _writes = 0;
            _readsByLine.Clear();
            Active.Value = this;
            try
            {
                var result = await action();
                output.WriteLine($"{phase}: SELECT/WITH={_reads}, opens={_opens}, DML={_writes}, linhas com leitura de referências individuais={_readsByLine.Count}");
                Assert.Equal((reads, opens, writes), (_reads, _opens, _writes));
                if (expectedAnalysisIds is not null)
                {
                    Assert.Equal(expectedAnalysisIds.Count, _readsByLine.Count);
                    Assert.All(expectedAnalysisIds, id => Assert.Contains(_readsByLine, sql => sql.Contains(id.ToString("N"), StringComparison.Ordinal)));
                }
                return result;
            }
            finally { Active.Value = null; }
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(451, 1)]
    [InlineData(451, 2)]
    [InlineData(451, 0)]
    [InlineData(1, 1, true)]
    [InlineData(451, 1, true)]
    public async Task LoadedScreenReadsOnlyChangedLinesAndReplacesOnlyTheirRows(int count, int affectedCount, bool automatic = false)
    {
        await using var db = await TestDatabase.CreateAsync();
        var factory = new TraceFactory(db.Repository.DatabasePath);
        var repo = new SqliteQuotationRepository(factory);
        var svc = new QuotationService(repo, new QuotationAnalyzer());
        var project = await repo.CreateProjectAsync("Escala");
        var targets = new HashSet<Guid>();
        for (int i = 1; i <= count; i++)
        {
            var line = Guid.NewGuid();
            var affected = i >= Math.Min(427, count) && targets.Count < affectedCount;
            if (affected) targets.Add(line);
            QuotationReference P(string id, int purchase, int value) => new()
            {
                LineId = line, Id = id, ContractId = $"11222333000181-1-{purchase:D6}/2026", ItemNumber = 1, ResultSequence = 1,
                SupplierName = "Empresa " + id, SupplierTaxId = "08236940000196", ItemDescription = "Produto",
                ItemUnit = "un", UnitPrice = value, ResultDate = new(2026, 11, 1)
            };
            await repo.SaveSampleAsync(project.Id, line, new($"Produto {i:D3}", 10, "un", null, null),
                [P("a", 1, 10), P("b", affected ? 1 : 2, 11), P("c", 3, 12)]);
            if (automatic) await repo.ConfirmBasketAsync(line, "a||b||c");
            else await repo.SaveManualBasketAsync(line, null, "Manual", ["a", "b", "c"]);
        }

        // Same full-report read that rc.2 dispatched after its transaction. Here it is the preloaded screen.
        var loaded = await factory.Measure("carregar relatório / recarga rc.2", () => svc.GetReportAsync(project.Id),
            (automatic ? 3 : 4) * count + 4, 2 * count + 4, 0, output);
        var rows = new ObservableCollection<QuotationLineDisplay>(loaded.Lines.Select(l => new QuotationLineDisplay(l)));
        var originals = rows.ToArray();
        var replaced = new List<Guid>();
        rows.CollectionChanged += (_, e) =>
        {
            Assert.Equal(NotifyCollectionChangedAction.Replace, e.Action);
            replaced.Add(((QuotationLineDisplay)e.NewItems![0]!).Line.Id);
        };
        await using var spectator = new SqliteConnection($"Data Source={db.Repository.DatabasePath};Pooling=False");
        await spectator.OpenAsync();
        async Task<long> Version()
        {
            await using var query = spectator.CreateCommand();
            query.CommandText = "PRAGMA data_version";
            return Convert.ToInt64(await query.ExecuteScalarAsync());
        }
        var before = await Version();
        var preview = await factory.Measure("prévia", () => svc.FindDuplicatePricesAsync(project.Id),
            affectedCount == 0 ? 2 : 4, 1, 0, output, new HashSet<Guid>());
        Assert.Equal(affectedCount, preview.Count);
        Assert.All(preview, row => Assert.True(row.Impact.NeedsAcceptance));
        var consents = preview.Select(row => (row.LineId, row.BasketKey)).ToHashSet();

        // Null is the actual decision returned for Cancel, ESC or closing the review window.
        var cancelledReview = await factory.Measure("cancelar a prévia", () => svc.ApplyDuplicatePriceReviewAsync(project.Id, preview, null),
            0, 0, 0, output);
        Assert.Empty(cancelledReview);
        var skipped = await factory.Measure("nenhum aceite", () => svc.ApplyDuplicatePriceReviewAsync(project.Id, preview, new HashSet<(Guid, string)>()),
            0, 0, 0, output);
        Assert.Empty(skipped);
        if (affectedCount != 0)
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await factory.Measure("interromper por token", () => Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => svc.ApplyDuplicatePriceReviewAsync(project.Id, preview, consents, cancelled.Token)), 0, 0, 0, output);
        }
        var afterCancel = await Version();
        output.WriteLine($"PRAGMA data_version antes/depois do cancelamento: {before} → {afterCancel}");
        Assert.Equal(before, afterCancel);
        Assert.Empty(replaced);

        var changed = await factory.Measure("gravação", () => svc.ApplyDuplicatePriceReviewAsync(project.Id, preview, consents),
            affectedCount == 0 ? 0 : 4, affectedCount == 0 ? 0 : 1, affectedCount == 0 ? 0 : (automatic ? 3 : 2) * affectedCount + 1, output);
        Assert.True(targets.SetEquals(changed));
        var result = await factory.Measure("atualização da interface", async () =>
        {
            var updates = await svc.GetCleanedItemsAsync(project.Id, changed);
            return QuotationLineDisplay.ReplaceCleanedItems(loaded, rows, updates).Report;
        }, 6 * affectedCount, 4 * affectedCount, 0, output, targets);
        Assert.Equal(affectedCount, replaced.Count);
        Assert.True(targets.SetEquals(replaced));
        for (int i = 0; i < count; i++)
        {
            Assert.Same(result.Lines[i], rows[i].Analysis);
            Assert.Equal(!targets.Contains(rows[i].Line.Id), ReferenceEquals(originals[i], rows[i]));
        }
        var reopened = new QuotationService(new SqliteQuotationRepository(factory), new QuotationAnalyzer());
        var repeat = await factory.Measure("reabrir e repetir a prévia", () => reopened.FindDuplicatePricesAsync(project.Id), 2, 1, 0, output);
        Assert.Empty(repeat);
        var again = await factory.Measure("aplicar repetição", () => reopened.ApplyDuplicatePriceReviewAsync(project.Id, repeat, consents), 0, 0, 0, output);
        Assert.Empty(again);
    }
}
