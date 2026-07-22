using System.Text.Json;

namespace PNCPKing.Infrastructure.Api;

internal sealed class ModalityDto
{
    public long Id { get; set; }
    public string? Nome { get; set; }
    public bool StatusAtivo { get; set; }
}

internal sealed class ContractPageDto
{
    public List<ContractDto>? Data { get; set; }
    public long TotalRegistros { get; set; }
    public long TotalPaginas { get; set; }
    public int NumeroPagina { get; set; }
}

internal sealed class ContractDto
{
    public OrganizationDto? OrgaoEntidade { get; set; }
    public int AnoCompra { get; set; }
    public int SequencialCompra { get; set; }
    public string? NumeroControlePNCP { get; set; }
    public string? ObjetoCompra { get; set; }
    public string? InformacaoComplementar { get; set; }
    public string? Processo { get; set; }
    public UnitDto? UnidadeOrgao { get; set; }
    public decimal? ValorTotalHomologado { get; set; }
    public long ModalidadeId { get; set; }
    public string? ModalidadeNome { get; set; }
    public string? SituacaoCompraNome { get; set; }
    public string? DataPublicacaoPncp { get; set; }
    public string? DataAtualizacaoGlobal { get; set; }
}

internal sealed class OrganizationDto
{
    public string? Cnpj { get; set; }
    public string? RazaoSocial { get; set; }
}

internal sealed class UnitDto
{
    public string? NomeUnidade { get; set; }
    public string? MunicipioNome { get; set; }
    public string? UfSigla { get; set; }
    public JsonElement CodigoIbge { get; set; }
}

internal sealed class ItemDto
{
    public long NumeroItem { get; set; }
    public string? Descricao { get; set; }
    public decimal? Quantidade { get; set; }
    public string? UnidadeMedida { get; set; }
    public string? InformacaoComplementar { get; set; }
    public string? ItemCategoriaNome { get; set; }
    public string? NcmNbsCodigo { get; set; }
    public string? NcmNbsDescricao { get; set; }
    public string? CatalogoCodigoItem { get; set; }
    public CatalogDto? Catalogo { get; set; }
    public CatalogCategoryDto? CategoriaItemCatalogo { get; set; }
    public string? SituacaoCompraItemNome { get; set; }
    public bool TemResultado { get; set; }
    public string? DataAtualizacao { get; set; }
}

internal sealed class ResultDto
{
    public long SequencialResultado { get; set; }
    public string? NiFornecedor { get; set; }
    public string? NomeRazaoSocialFornecedor { get; set; }
    public string? TipoPessoa { get; set; }
    public SupplierLocationDto? LocalidadeFornecedor { get; set; }
    public decimal? QuantidadeHomologada { get; set; }
    public decimal? ValorUnitarioHomologado { get; set; }
    public decimal? ValorTotalHomologado { get; set; }
    public string? DataResultado { get; set; }
    public JsonElement SituacaoCompraItemResultadoId { get; set; }
    public string? SituacaoCompraItemResultadoNome { get; set; }
}

internal sealed class CatalogDto
{
    public string? Nome { get; set; }
}

internal sealed class CatalogCategoryDto
{
    public string? Nome { get; set; }
    public string? Descricao { get; set; }
}

internal sealed class SupplierLocationDto
{
    public string? NomeMunicipio { get; set; }
    public string? Uf { get; set; }
}
