using Microsoft.Data.Sqlite;
using PNCPKing.Core.Geography;
using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class ItemSearchRepositoryTests
{
    [Fact]
    public async Task CompleteItemList_IsReconciledAtomicallyAndSearchUsesContractSemantics()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RepositorySearchTests.Contract("contract", "Aquisição de alimentos", "SP", 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(contract.PncpId, [
            Item(contract.PncpId, 1, "Café torrado especial", true),
            Item(contract.PncpId, 2, "Chá de ervas", false)
        ], false);
        await database.Repository.ReplaceItemResultsAsync(contract.PncpId, 1, [Result(contract.PncpId, 1, 20m)]);

        var accentless = await database.Repository.SearchItemsAsync(contract.PncpId, "cafe torr");
        var phrase = await database.Repository.SearchItemsAsync(contract.PncpId, "\"cafe torrado\"");
        var snapshot = await database.Repository.GetItemSnapshotAsync(contract.PncpId);

        Assert.Single(accentless);
        Assert.Single(phrase);
        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsCurrentFor(contract));
        Assert.Equal(2, snapshot.ItemCount);

        // A second successful complete-list commit removes an item no longer sent by
        // PNCP and cascades its cached prices in the same transaction.
        await database.Repository.UpsertItemsAsync(contract.PncpId, [
            Item(contract.PncpId, 2, "Chá de ervas", false)
        ], false);

        Assert.Empty(await database.Repository.SearchItemsAsync(contract.PncpId, "cafe"));
        Assert.Null(await database.Repository.GetCachedItemResultsAsync(contract.PncpId, 1));
        snapshot = await database.Repository.GetItemSnapshotAsync(contract.PncpId);
        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot.ItemCount);
    }

    [Fact]
    public async Task ItemSearch_AppliesAndOrPhrasesPrefixesAndGlobalDescriptionExclusions()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = RepositorySearchTests.Contract("grammar", "Café, chá e filtros", "SP", 1);
        await database.Repository.UpsertContractsAsync([contract]);
        await database.Repository.UpsertItemsAsync(contract.PncpId, [
            Item(contract.PncpId, 1, "Café torrado com filtro permanente", false),
            Item(contract.PncpId, 2, "Café torrado para cafeteira elétrica", false),
            Item(contract.PncpId, 3, "Chá com filtro de papel", false),
            Item(contract.PncpId, 4, "Chá verde em recipiente metálico", false)
        ], false);

        var and = await database.Repository.SearchItemsAsync(contract.PncpId, "cafe + filtro");
        var expression = await database.Repository.SearchItemsAsync(
            contract.PncpId,
            "\"café torr\" OU chá -cafeteira -\"filtro de papel\"");

        Assert.Equal([1L], and.Select(item => item.ItemNumber));
        Assert.Equal([1L, 4L], expression.Select(item => item.ItemNumber).Order());
    }

    [Fact]
    public async Task CandidateCursor_OrdersFirstFiftyThenRemainingSpThenStatesAndRotatesWithinUf()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.Repository.UpsertContractsAsync([
            LocatedContract("ribeirao", "Ribeirão Preto", "3543402", "SP", 1),
            LocatedContract("sertaozinho", "Sertãozinho", "3551702", "SP", 2),
            LocatedContract("sao-paulo", "São Paulo", "3550308", "SP", 3),
            LocatedContract("campinas", "Campinas", "3509502", "SP", 4),
            LocatedContract("santos", "Santos", "3548500", "SP", 5),
            LocatedContract("prudente", "Presidente Prudente", "3541406", "SP", 6),
            LocatedContract("belo-horizonte", "Belo Horizonte", "3106200", "MG", 7),
            LocatedContract("salvador", "Salvador", "2927408", "BA", 8)
        ]);
        var query = new SearchQuery("pincel", GeoScope.All, Sort: SearchSort.Nearest);
        var expression = PNCPKing.Core.Search.SearchText.Parse(query.Text);

        var first = await ReadAllCandidatesAsync(database.Repository, query, expression, 0, 3);
        var repeated = await ReadAllCandidatesAsync(database.Repository, query, expression, 0, 2);
        var ids = first.Select(item => item.Contract.PncpId).ToArray();

        Assert.Equal(ids, repeated.Select(item => item.Contract.PncpId));
        Assert.Equal(["ribeirao", "sertaozinho"], ids.Take(2));
        var sp = first.Where(item => item.Contract.Uf == "SP" && item.Cursor.GeographicLayer == 1).ToArray();
        Assert.Equal(4, sp.Length);
        Assert.All(sp, item => Assert.Equal(
            BrazilMunicipalityCatalog.GetStateProximityRank("SP"),
            item.Cursor.GroupRank));
        Assert.True(
            Array.IndexOf(ids, "prudente") < Array.IndexOf(ids, "belo-horizonte") &&
            Array.IndexOf(ids, "belo-horizonte") < Array.IndexOf(ids, "salvador"));
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());

        var pivot = checked(sp[1].Cursor.RandomOrderKey + 1);
        var rotated = await ReadAllCandidatesAsync(database.Repository, query, expression, pivot, 20);
        Assert.NotEqual(
            sp.Select(item => item.Contract.PncpId).ToArray(),
            rotated.Where(item => item.Contract.Uf == "SP" && item.Cursor.GeographicLayer == 1)
                .Select(item => item.Contract.PncpId).ToArray());
    }

    [Fact]
    public async Task VersionOneMigration_PreservesItemsAndBackfillsNearbyMunicipalityCode()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PNCPKing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "version-one.db");
        try
        {
            await CreateVersionOneDatabaseAsync(path);
            var repository = new SqliteContractRepository(path);
            await repository.InitializeAsync();

            var contract = await repository.GetContractAsync("old-contract");
            var items = await repository.SearchItemsAsync("old-contract", "cafe");
            Assert.NotNull(contract);
            Assert.Equal("3543402", contract.MunicipalityIbgeCode);
            Assert.Single(items);
            Assert.Equal("Café em grãos", items[0].Description);

            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            await using var version = connection.CreateCommand();
            version.CommandText = "SELECT version FROM schema_info WHERE id = 1;";
            Assert.Equal(
                SqliteContractRepository.CurrentSchemaVersion,
                Convert.ToInt32(await version.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task NearbyFilter_UsesCatalogCodesAndOrdersByStraightLineDistance()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.Repository.UpsertContractsAsync([
            RepositorySearchTests.Contract("itamogi", "Café", "MG", 1) with
            {
                Municipality = "Itamogi",
                MunicipalityIbgeCode = "3132909"
            },
            RepositorySearchTests.Contract("ribeirao", "Café", "SP", 2) with
            {
                Municipality = "Ribeirão Preto",
                MunicipalityIbgeCode = "3543402"
            },
            RepositorySearchTests.Contract("salvador", "Café", "BA", 3) with
            {
                Municipality = "Salvador",
                MunicipalityIbgeCode = "2927408"
            },
            RepositorySearchTests.Contract("name-only", "Café", "SP", 4) with
            {
                Municipality = "Ribeirão Preto",
                MunicipalityIbgeCode = null
            }
        ]);

        var result = await database.Repository.SearchAsync(new SearchQuery(
            "cafe",
            SearchGeoFilter.NearRibeirao,
            Sort: SearchSort.Nearest));

        Assert.Equal(3, result.Total);
        Assert.Equal(
            ["name-only", "ribeirao"],
            result.Results.Take(2).Select(contract => contract.PncpId));
        Assert.All(result.Results.Take(2), contract =>
            Assert.Equal(0d, contract.DistanceFromRibeiraoKilometers));
        Assert.Equal("itamogi", result.Results[2].PncpId);
        Assert.True(result.Results[2].DistanceFromRibeiraoKilometers > 0d);
    }

    private static ProcurementItem Item(string contractId, long number, string description, bool hasResult) => new()
    {
        ContractId = contractId,
        ItemNumber = number,
        Description = description,
        Unit = "kg",
        Status = "Ativo",
        HasResult = hasResult,
        HydrationStatus = hasResult ? ItemHydrationStatus.NotLoaded : ItemHydrationStatus.Complete
    };

    private static HomologationResult Result(string contractId, long itemNumber, decimal unitPrice) => new()
    {
        ContractId = contractId,
        ItemNumber = itemNumber,
        ResultSequence = 1,
        SupplierName = "Fornecedor",
        HomologatedUnitValueScaled = DecimalScale.ToScaled(unitPrice),
        ResultStatusId = 1,
        ResultStatusName = "Informado"
    };

    private static ContractRecord LocatedContract(
        string id,
        string municipality,
        string ibgeCode,
        string uf,
        int sequence) =>
        RepositorySearchTests.Contract(id, "Aquisição de pincel escolar", uf, sequence) with
        {
            Municipality = municipality,
            MunicipalityIbgeCode = ibgeCode
        };

    private static async Task<IReadOnlyList<ItemContractCandidate>> ReadAllCandidatesAsync(
        SqliteContractRepository repository,
        SearchQuery query,
        PNCPKing.Core.Search.SearchExpression expression,
        long pivot,
        int pageSize)
    {
        var result = new List<ItemContractCandidate>();
        ItemCandidateCursor? cursor = null;
        do
        {
            var page = await repository.SearchItemCandidatesAsync(query, expression, pivot, cursor, pageSize);
            result.AddRange(page.Results);
            cursor = page.NextCursor;
            if (!page.HasMore)
            {
                break;
            }
        }
        while (true);

        return result;
    }

    private static async Task CreateVersionOneDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE schema_info(id INTEGER PRIMARY KEY, version INTEGER NOT NULL);
            INSERT INTO schema_info(id, version) VALUES(1, 1);

            CREATE TABLE contracts(
                pncp_id TEXT PRIMARY KEY,
                cnpj TEXT NOT NULL,
                purchase_year INTEGER NOT NULL,
                purchase_sequence INTEGER NOT NULL,
                object TEXT NOT NULL DEFAULT '',
                additional_information TEXT NOT NULL DEFAULT '',
                process TEXT NOT NULL DEFAULT '',
                organization TEXT NOT NULL DEFAULT '',
                unit TEXT NOT NULL DEFAULT '',
                municipality TEXT NOT NULL DEFAULT '',
                uf TEXT NOT NULL DEFAULT '',
                modality_id INTEGER NOT NULL,
                modality_name TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL DEFAULT '',
                publication_date TEXT,
                global_updated_at TEXT,
                total_homologated_scaled INTEGER,
                search_text TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE items(
                contract_id TEXT NOT NULL REFERENCES contracts(pncp_id) ON DELETE CASCADE,
                item_number INTEGER NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                unit TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL DEFAULT '',
                has_result INTEGER NOT NULL DEFAULT 0,
                source_updated_at TEXT,
                hydration_status INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                cache_updated_at TEXT,
                PRIMARY KEY(contract_id, item_number)
            );

            CREATE TABLE item_results(
                contract_id TEXT NOT NULL,
                item_number INTEGER NOT NULL,
                result_sequence INTEGER NOT NULL,
                supplier_tax_id TEXT NOT NULL DEFAULT '',
                supplier_name TEXT NOT NULL DEFAULT '',
                quantity_scaled INTEGER,
                unit_value_scaled INTEGER,
                total_value_scaled INTEGER,
                result_date TEXT,
                result_status_id INTEGER NOT NULL DEFAULT 0,
                result_status_name TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(contract_id, item_number, result_sequence),
                FOREIGN KEY(contract_id, item_number) REFERENCES items(contract_id, item_number) ON DELETE CASCADE
            );

            CREATE TRIGGER contracts_mark_items_stale
            AFTER UPDATE OF global_updated_at ON contracts
            BEGIN
                UPDATE items SET hydration_status = 4 WHERE contract_id = new.pncp_id AND has_result = 1;
            END;

            CREATE TABLE sync_partitions(
                partition_key TEXT PRIMARY KEY,
                next_page INTEGER NOT NULL DEFAULT 1,
                completed INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );

            INSERT INTO contracts(
                pncp_id, cnpj, purchase_year, purchase_sequence, object, organization, unit,
                municipality, uf, modality_id, modality_name, status, publication_date,
                global_updated_at, search_text)
            VALUES('old-contract', 'ABC', 2026, 1, 'Café', 'Órgão', 'Unidade',
                   'Ribeirão Preto', 'SP', 6, 'Pregão', 'Divulgada',
                   '2026-06-01T00:00:00+00:00', '2026-06-01T00:00:00+00:00', 'cafe');

            INSERT INTO items(
                contract_id, item_number, description, unit, status, has_result, hydration_status)
            VALUES('old-contract', 1, 'Café em grãos', 'kg', 'Ativo', 1, 2);
            """;
        await command.ExecuteNonQueryAsync();
    }
}
