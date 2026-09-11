# Pesquisa principal por itens — 10/09/2026

Consulta reproduzida: `Serviço limpeza -odontológico "diária`, ordenada por mais
recentes, em todo o país, de 10/10/2025 a 10/09/2026.

## Causa e correção

Acima de 20 mil correspondências textuais, a consulta abandonava o índice dos
itens e percorria lotes de contratações, avaliando as descrições de seus itens.
Além disso, a ordenação desses lotes por constantes e por `COALESCE` impedia o
aproveitamento direto do índice de publicação.

A consulta por texto com unidade ou aproximação agora parte do FTS dos itens, aplica a expressão completa
(inclusive unidades e aproximações) antes do limite da página e ordena somente
as chaves dos preços elegíveis. Os registros completos são carregados para os
preços selecionados. A classificação por relevância conserva seu mecanismo
existente. Consultas amplas sem esses filtros e consultas sem termos textuais
conservam os lotes, evitando carregar todos os itens de um termo muito comum
para preencher uma página. Nas consultas por período e data, os lotes usam o
índice de publicação e avançam pelo cursor. Textos de menor cardinalidade também
usam a seleção de chaves pelo FTS.

Nenhum índice permanente ou migração foi acrescentado. O banco ativo não foi
alterado nesta validação.

## Medições e limites

Foi reutilizada uma cópia isolada de 17.222.520.832 bytes no HD, com SQLite em modo
somente leitura. Seu tamanho e data de modificação permaneceram iguais. O índice
encontrou 41.600 itens com o texto, antes do filtro de unidade.

| Medição | Resultado |
| --- | ---: |
| Primeira página, 50 preços | 62.107 ms |
| Segunda página, 50 preços distintos | 763 ms |

O primeiro acesso ainda teve custo elevado de leitura do HD; o seguinte se
beneficiou do cache do sistema operacional. Esses números são de páginas
diferentes da versão corrigida: **não constituem uma comparação antes/depois**
nem garantem latência inferior a um segundo em buscas frias. Não foi forçada a
limpeza do cache do sistema operacional antes das medições.

Em uma prova isolada da seleção de 257 contratações, a ordenação antiga não
concluiu em 20 segundos. A ordenação direta pelo índice concluiu em cerca de
2 ms. Esta é uma etapa da consulta, não uma medida da pesquisa inteira.

Os resultados brutos e relatórios de teste ficam em `artifacts/search-validation/`.
O teste de desempenho é opcional e exige `PNCPKING_MAIN_SEARCH_COPY` apontando
para uma cópia em diretório de benchmark; não inicializa, migra nem compacta o
banco. `PNCPKING_MAIN_SEARCH_REPORT` define o arquivo JSON de saída.

## Regressões cobertas

- Mais de 20 mil correspondências com unidade, sem retorno à varredura de contratações.
- Preservação da estratégia para expressões amplas sem filtro de unidade ou aproximação.
- Exclusões, unidade acentuada, prefixos, várias unidades e aproximações numéricas.
- Filtros de data, estado, preço, resultados ativos e snapshots atualizados.
- Ordenação por data e proximidade; desempate por contratação, item e resultado.
- Páginas completas, sem repetição, com progresso e cursor compatíveis.
- Publicação ausente, preservando consultas sem período e a exclusão por período.

Validação final: compilação Release sem avisos ou erros e 478 testes aprovados.
