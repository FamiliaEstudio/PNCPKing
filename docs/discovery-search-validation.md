# Pesquisa por descoberta — 15/09/2026

## Comportamento

A pesquisa principal lê candidatos na ordem dos identificadores internos dos
itens, em lotes de 250, sem ordenar antecipadamente o conjunto por publicação,
proximidade ou relevância. Todos os perfis usam esse caminho. Os filtros de
texto, unidade, aproximação, geografia, período, preço, hidratação e atualização
do snapshot permanecem obrigatórios. Expressões antigas `C:(...)` conservam
a prioridade por contratação em duas passagens; expressões sem termos no FTS
percorrem os itens em lotes limitados.

Cada ação entrega até 50 preços válidos. A busca continua até encontrar o 51º
ou esgotar os candidatos, confirmando a existência da próxima página. Os preços
já encontrados aparecem durante essa confirmação. O cursor conserva o item,
sua identidade lógica e a sequência do último resultado entregue: o resultado
excedente não é pulado, inclusive quando um item ocupa várias páginas.

Os identificadores técnicos não são persistidos. Atualização, ampliação pela
API, atualização manual de uma contratação e compactação invalidam a
continuação; a próxima carga recompõe os preços locais antes de avançar.
Importação e retenção usam o reinício já existente. Respostas de uma pesquisa
anterior ou de dados invalidados não substituem o cursor atual. Após cancelar
uma página parcialmente entregue, **Carregar mais resultados** pode continuar
com um novo token, preservando as linhas entregues.

A tabela começa na ordem de descoberta. O clique numa coluna ordena os dados
carregados em memória; inserções seguintes usam a ordenação incremental nativa
do WPF. A coleção não é reconstruída a cada lote. Fixação, cesta e seleção
permanecem associadas ao preço. Os primeiros encontrados não são necessariamente
os mais recentes do banco.

O seletor de relevância/data/proximidade pertence ao painel de contratações.
Essa lista é consultada sob demanda, sem uma consulta ordenada oculta depois
da primeira página de preços. As assinaturas anteriores do repositório mantêm
seu comportamento; a nova sobrecarga recebe `PriceCacheLocalReadOrder`.

A calibração mede descoberta e compara caches efetivamente distintos, sem
duplicar candidatos pelo método anterior de seleção. Sua versão passa de 2
para 3, invalidando recomendações antigas. Não houve migração, novo índice,
nova dependência de produção ou mudança automática do tamanho do cache.

## Comparação no banco isolado

Foi usada exclusivamente a cópia de 17.222.520.832 bytes em diretório de
benchmark, com conexões privadas somente leitura, sem inicialização ou
migração. O teste verifica tamanho e data de modificação ao terminar.
Perfil Restrito explícito, cache de 32 MiB, duas páginas de 50, duas rodadas
com ordem invertida e prazo de 45 segundos por página. A referência é a busca
progressiva atual; café e diária por data, limpeza ampla por proximidade.

Tempos em segundos, mediana das duas rodadas. `>45` indica que nenhuma das
duas execuções atingiu a marca antes da interrupção. A confirmação inclui a
procura pelo próximo preço. A coluna de interrupções conta execuções, não
páginas concluídas.

| Cenário | Consulta | 50 preços entregues | Página confirmada | Segunda página | Interrupções |
| --- | --- | ---: | ---: | ---: | ---: |
| Café | Atual | >45 | >45 | — | 2/2 |
| Café | Descoberta | 0,058 | 0,066 | 0,042 | 0/2 |
| Limpeza com diária | Atual | 13,287 | 13,293 | 0,953 | 0/2 |
| Limpeza com diária | Descoberta | 1,533 | 1,539 | 0,940 | 0/2 |
| Limpeza ampla | Atual | >45 | >45 | — | 2/2 |
| Limpeza ampla | Descoberta | 0,054 | 0,056 | 0,053 | 0/2 |

A descoberta concluiu 100 preços nas seis medições. O primeiro preço chegou
em 12–54 ms. As sequências foram estáveis entre rodadas, sem duplicatas, e os
preços entregues foram verificados contra texto, período e validade do resultado.
Não se exige igualdade das primeiras páginas entre estratégias: justamente a
seleção antecipada por ordenação foi dispensada. A equivalência do conjunto
completo é verificada em bases sintéticas.

**A melhora não é uniforme.** Na repetição com diária, a consulta atual confirmou
a primeira página em 0,815 s, contra 1,524 s da descoberta. A outra execução da
atual levou 25,770 s. A descoberta antecipou a primeira linha nesse cenário,
mas consumiu aproximadamente 2,57 s de CPU nas duas páginas, contra 2,41 s da
referência, pelas medianas. Não há promessa de acelerar toda consulta.

O relatório registra também primeira linha, dez linhas, CPU, alocações
acumuladas, pico de memória privada e working set. A memória é do processo de
testes; alocações acumuladas não representam RAM retida. Os tempos acima são
de entrega pelo repositório, não de renderização da aplicação inteira.

Não houve limpeza controlada do cache do sistema operacional. Investigações e
execuções anteriores já haviam acessado a cópia: a primeira rodada não é um
teste frio. O executor também não representa necessariamente o PC antigo do
usuário. Uma tentativa anterior iniciou processos sobrepostos e disputou o
arquivo de relatório; seus resultados foram descartados. A comparação válida
foi executada por um único processo, em 3 min 34 s.

Resultados: `artifacts/discovery-validation/comparison.json` e
`discovery-comparison-single-process.trx`. A tentativa descartada está em
`comparison-discarded-overlap.json`; ela não participa da tabela acima.

## Verificação da interface

O verificador Windows usa o `UiBatchBuffer`, os modelos de exibição e a coleção
de produção, com dispatcher e DataGrid WPF reais e virtualização habilitada.
Para cada volume, aplica 30 páginas de 50 sobre uma base restaurada entre
rodadas, após aquecimento do renderizador. Mede a espera de uma operação de
prioridade de entrada enfileirada antes da inserção, incluindo o trabalho que
ocorre antes de ela ser atendida. Não inclui a criação dos modelos nessa medida.

| Linhas iniciais | Dispatcher p95 sem ordenação | Dispatcher p95 ordenado |
| ---: | ---: | ---: |
| 50 | 0,535 ms | 2,541 ms |
| 1.000 | 0,320 ms | 0,921 ms |
| 10.000 | 0,399 ms | 0,594 ms |

Todos os cenários ficaram abaixo do critério de 100 ms. Ordenar tem custo;
neste teste, ele permaneceu pequeno. As verificações preservaram seleção,
fixação e cesta. A ordenação compara datas tipadas, não os textos formatados.

Uma verificação adicional consumiu duas páginas da consulta real sobre a cópia,
ordenando a tabela entre elas. A primeira linha foi aplicada antes do término
da consulta, e os 100 preços não tiveram perdas ou duplicatas. A visualização
permaneceu ordenada enquanto a coleção e o cursor mantiveram a sequência de
descoberta. Uma pausa deliberada do produtor comprovou a entrega antecipada;
esse caso não é usado como benchmark de velocidade. Resultado: `ui.json`.

## Reprodução e entrega

A solução completa compilou em Release com zero avisos e zero erros. A suíte
completa aprovou **596 testes, sem falhas**, em 1 min 32 s, registrada em
`artifacts/discovery-validation/discovery-full-suite.trx`. Os 42 testes
selecionados de descoberta e calibração também passaram. O benchmark isolado
e a verificação WPF foram executados separadamente, sem sobreposição com a
suíte completa.

- Solução: `dotnet build PNCPKing.sln -c Release --no-restore -p:UseAppHost=false`.
- Testes: `dotnet test tests/PNCPKing.Tests/PNCPKing.Tests.csproj -c Release`.
- Benchmark optativo: definir `PNCPKING_DISCOVERY_COPY` para uma cópia em diretório
  de benchmark e `PNCPKING_DISCOVERY_REPORT` para o JSON; executar somente
  `FullyQualifiedName~MainSearchDiscoveryPerformanceTests`, sem outro teste ou
  benchmark concorrente. Sem essas variáveis, a suíte normal não abre a cópia.
- WPF: `dotnet run --project tests/PNCPKing.UiChecks/PNCPKing.UiChecks.csproj -c Release -- <relatorio.json> <copia-de-benchmark.db>`.
  Sem o segundo argumento, executa apenas as medições sintéticas da grade.

Os testes funcionais cobrem filtros, ausência de preços, fronteiras 49/50/51,
501 preços, vários resultados por item, candidatos válidos após o 600º item,
prioridade `C:`, expressões sem FTS, cursores incompatíveis e cancelamento após
o primeiro ou o 50º resultado. O novo projeto de verificação WPF não gera
executável próprio de aplicação (`UseAppHost=false`).

Não houve publicação nesta tarefa. O executável canônico em
`artifacts/win-x64/PNCPKing.exe` permanece na versão anteriormente publicada.
O banco do usuário não foi aberto para essas medições.
