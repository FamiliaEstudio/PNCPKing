# Pesquisa principal por itens — 10/09/2026

Consulta reproduzida: `Serviço limpeza -odontológico "diária`, ordenada por mais
recentes, em todo o país, de 10/10/2025 a 10/09/2026.

## Causa e correção

Acima de 20 mil correspondências textuais, a consulta abandonava o índice dos
itens e percorria lotes de contratações, avaliando as descrições de seus itens.
Além disso, a ordenação desses lotes por constantes e por `COALESCE` impedia o
aproveitamento direto do índice de publicação.

A consulta com termos indexáveis nos itens agora parte do FTS dos itens, aplica a expressão completa
(inclusive unidades e aproximações) antes do limite da página e ordena somente
as chaves dos preços elegíveis. Os registros completos são carregados para os
preços selecionados. A classificação por relevância conserva seu mecanismo
existente. A quantidade de correspondências deixou de decidir entre pesquisa por
itens e varredura de contratações. Foram retirados a constante de 20 mil, a
contagem prévia e o desvio por cardinalidade. Consultas sem termos indexáveis
nos itens, como uma expressão composta somente por unidade, conservam os lotes.
As cláusulas explícitas de contratação mantêm seu caminho existente.

O corte apareceu no commit `813bbe6` como `occurrences > 20_000`. Seu fundamento
era uma heurística de desempenho: encontrar preços em poucas contratações
recentes para textos muito comuns. Não havia dependência de esquema ou de
integridade que exigisse a troca. A decisão não considerava unidade, preço,
período, localização, atualização do snapshot ou densidade de resultados
utilizáveis. O ensaio descrito em `main-search-batch-cost-analysis.md` mostrou
que esse indicador podia selecionar um caminho muito mais lento.

A correção reutiliza a seleção de chaves já existente abaixo do corte. Não
introduz uma fila de todos os candidatos previamente ordenados, `Dictionary`,
índice adicional ou cache entre páginas. Consequentemente, remove o salto
artificial de estratégia, mas não elimina o custo da primeira leitura do HD
nem a filtragem repetida entre páginas.

Os limites de página (50 na interface e até 200 no repositório) continuam
permitindo avançar pelo cursor. O `pageSize + 1` detecta a existência da próxima
página depois dos filtros; não limita a pesquisa aos primeiros candidatos do
FTS. As janelas da ordenação por relevância ampliam-se progressivamente e não
usam o antigo desvio dos 20 mil. Esses mecanismos têm finalidades distintas e
foram preservados.

Nenhum índice permanente ou migração foi acrescentado. O banco ativo não foi
alterado nesta validação.

## Medição anterior e limites

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
banco. `PNCPKING_MAIN_SEARCH_REPORT` define o prefixo dos arquivos JSON de saída,
com um sufixo para cada cenário. `PNCPKING_MAIN_SEARCH_ROUNDS` permite repetir as
medições. O teste cobre a expressão com diária por data e a expressão ampla
sem diária por data e proximidade.

## Regressões cobertas

- 19 mil, 20 mil e 20.001 correspondências, sem mudança de estratégia por volume.
- Mais de 20 mil correspondências com ou sem unidade, sem retorno à varredura de contratações.
- Preços válidos somente no final do conjunto e dois resultados de um item separados entre páginas.
- Preservação dos lotes para expressões sem termos indexáveis nos itens.
- Exclusões, unidade acentuada, prefixos, várias unidades e aproximações numéricas.
- Filtros de data, estado, preço, resultados ativos e snapshots atualizados.
- Ordenação por data e proximidade; desempate por contratação, item e resultado.
- Páginas completas, sem repetição, com progresso e cursor compatíveis.
- Publicação ausente, preservando consultas sem período e a exclusão por período.

Validação anterior: compilação Release sem avisos ou erros e 478 testes aprovados.

## Validação da remoção do corte

Compilação da solução em Release: zero avisos e zero erros. As 37 verificações
selecionadas de pesquisa, filtros, cursores e protótipos passaram, incluindo os
seis casos nas fronteiras de 19.000, 20.000 e 20.001 itens.

A suíte completa executou 490 testes: 487 passaram e três falharam em
`AiAutomationTests` (`TimedAutomation_ProcessesFiftyUniqueSharedContractsAndSkipsEmptyPromptLevel`,
`TimedAutomation_StartsAtFirstPopulatedPromptWhenRestrictiveIsEmpty` e
`TimedAutomation_ResumeAddsEveryMissingFallbackAndIgnoresDuplicatePrompts`).
Esses testes criam publicações com `DateTimeOffset.UtcNow`, enquanto a automação
normaliza o fim do período com `DateTime.Today`. Na execução, já era 11/09 em UTC
e ainda 10/09 no horário local; as publicações artificiais ficaram depois do
limite. Os testes, o serviço dessa automação e seu repositório de contratações
não foram modificados nesta alteração. O relatório preserva as falhas; não
declara a suíte completa aprovada.

O ensaio após a remoção reutilizou a mesma cópia de 17.222.520.832 bytes, em
somente leitura, de 10/10/2025 a 10/09/2026. Os resultados foram:

| Consulta | Primeira tentativa: primeiros 50 | Repetições: primeiros 50 | Próximos 50 |
| --- | ---: | ---: | ---: |
| Com diária, recentes | 33.047 ms | 760 ms e 768 ms | mediana de 769 ms, três rodadas |
| Sem diária, recentes | excedeu 120 s | não executadas após a interrupção | não obtidos |
| Sem diária, proximidade | excedeu 120 s | não executadas após a interrupção | não obtidos |

As seis páginas concluídas da consulta com diária tiveram 50 preços cada,
sem duplicatas entre páginas. Suas identidades e sua ordem coincidiram com
os fingerprints da referência anterior. O teste também confirmou tamanho e
data de modificação preservados na cópia. O relatório do ensaio registra um
cenário aprovado e dois interrompidos pelo prazo; não houve aprovação completa
desse ensaio de desempenho.

Não foi limpo o cache do sistema operacional nem isolada toda a atividade do
Windows. Um instantâneo dos contadores do HD durante o ensaio registrou cerca
de 164 leituras/s, 7,3 MB/s de leitura e fila de duas operações, incluindo outras
atividades do sistema. Isso não mede isoladamente a consulta nem permite
atribuir todo o tempo a um componente. Os tempos das rodadas anteriores não
devem ser apresentados como um comparativo frio controlado com esta execução.

**Conclusão:** o desvio dos 20 mil era uma regra autoimposta de escolha de
estratégia e foi eliminado. Isso não bastou para garantir uma primeira página
rápida: a consulta pelo índice ainda precisa ler e filtrar muitos itens e obter
os campos das contratações e resultados antes de selecionar os preços. O tempo
de primeira leitura continua sendo um problema observado, separado da troca
artificial de estratégia. Os próximos estudos devem localizar essas leituras e
seu volume antes de propor novos índices ou preparação antecipada de filas.

Logs, TRX, JSON das páginas concluídas e observação do disco:
`artifacts/search-threshold-validation/`. Nenhum novo executável foi publicado
nesta alteração.
