# Base completa de preços: desenho e validação da versão 1.2.44

## Fronteira de preservação

O perfil `PricesPreservingLocalData` usa o formato de arquivo 5 e exige o esquema atual nos dois lados. Os perfis anteriores mantêm seus contratos. O novo arquivo não é um backup de recuperação nem um pacote incremental por data.

- O emissor cria um snapshot, elimina as tabelas locais conforme uma lista explícita, compacta para remover páginas livres com conteúdo particular e executa a verificação integral na origem. O arquivo inclui SHA-256 e metadados.
- O receptor extrai com SHA-256 no mesmo fluxo, confere esquema/DDL/índices e prepara tudo em staging na unidade do banco. Não executa `integrity_check`, `VACUUM` ou retenção nessa transferência.
- Cotações e tabelas locais são copiadas sem fundir identidades. Triggers que poderiam criar linhas ou ignorar referências vencidas ficam suspensos somente na transação de staging e são restaurados. Conteúdo é comparado nos dois sentidos; referências locais são verificadas.
- Contratações exclusivas do destino conservam sua árvore completa. Para contratos compartilhados, substituições e exclusões exigem versões ordenáveis e snapshots completos. Divergências sem prova suficiente são recusadas antes da troca, evitando resultados cancelados ressuscitados por união de chaves.
- Preferências de coleta e fixações do destino são preservadas; contadores derivados e catálogo são reconciliados. Evidências locais e settings externos não são apagados. A calibração local não é zerada.
- Um gate por caminho aguarda uma janela sem conexões, sem bloquear conexões aninhadas durante a drenagem. A guarda de arquivo Windows impede escritores externos; checkpoint é verificado. O staging é consolidado e descarregado antes de `File.Replace`, que mantém a base anterior em `.before-import-...bak`.
- Falhas antes da troca deixam o destino ativo. Cancelamento é permitido antes da ativação; a troca final é indivisível. Falhas de limpeza/progresso depois da troca não transformam o commit concluído em uma aparente falha.

## Exercícios automatizados

`FullPriceTransferTests` cobre preservação de todas as colunas das tabelas locais, IDs/cestas/divisores, referências vencidas, evidências e rascunhos, ausência de texto particular no pacote, origem intacta, reimportação, recuperação, corrupção, cancelamento, falha antes da troca, concorrência e conexões aninhadas. Também cobre timestamps com fusos, conflitos sem ordem segura, exclusões oficiais, estatísticas, catálogo ativo e checkpoints de cobertura.

A fixture de volume usa 20 itens por contrato, cinco UFs, 200 municípios, 500 fornecedores e quatro classes de produtos. As medidas abaixo são sintéticas no computador de desenvolvimento, **não uma previsão de tempo em HDD antigo**:

| Volume | Banco | Pacote | Exportação | Primeira importação | Reimportação |
|---|---:|---:|---:|---:|---:|
| 100 mil itens / 5 mil contratos | 30,97 MB | 5,52 MB | 0,97 s | 0,36 s | 1,38 s |
| 1 milhão de itens / 50 mil contratos | 306,93 MB | 53,74 MB | 9,09 s | 1,19 s | 13,85 s |

A primeira importação parte de destino pequeno com trabalhos locais; a reimportação usa o destino já completo. O benchmark de um milhão é opt-in: `PNCPKING_FULL_TRANSFER_BENCHMARK=1`, com filtro `FullPriceTransferTests`. Foi efetivamente executado, além dos testes comuns. As verificações integrais usadas pelo harness de teste não fazem parte do fluxo receptor do aplicativo.

## Limites

- Não houve acesso ao banco real nem teste físico no HDD de 16 anos. Não foram simulados falta física de disco ou corte de energia; há prévia conservadora de espaço, transações isoladas e troca atômica com recuperação.
- Os testes de falha/cancelamento validam os dados antes da fronteira de troca; a durabilidade final depende também do Windows, do sistema de arquivos e do hardware.
- Um destino grande exige leitura para verificar conflitos. Dados exclusivos precisam ser incorporados, por isso o tempo não equivale apenas a copiar o arquivo.
- Esquema ou extensões desconhecidas são recusados. O novo formato requer uma versão compatível nos dois PCs.
- A política normal de retenção de 11 meses continua ativa fora dessa transferência.
