# Pesquisas locais independentes com `+`

## Implementação

A pesquisa principal separa trechos antes de chamar o parser existente. Cada trecho mantém sua expressão, seus filtros textuais e seu cursor de descoberta. As páginas de até 50 preços alternam sequencialmente; trechos vazios são rejeitados antes de cancelar a pesquisa anterior ou trocar as linhas. Trechos esgotados são pulados e cada ação visita no máximo uma vez cada trecho. Uma página sobreposta pode acrescentar menos de 50 preços, inclusive zero, e conserva sua continuação.

O repositório, o SQL, os índices e os blocos internos de 250 candidatos permanecem iguais. Não houve migração, dependência nova ou mudança na interpretação compartilhada por Cotações e catálogo. O antigo `+` de conjunção é convertido em espaço somente ao transferir um critério de cotação para a pesquisa principal. A ajuda e o editor de Sweet Codes da pesquisa principal usam a nova sintaxe.

A sequência guarda somente expressões, consultas, cursores e estados em memória. As linhas usam a deduplicação e o buffer WPF existentes. Cancelamento conserva o cursor das linhas entregues; troca de pesquisa ou invalidação do banco impede a aplicação de respostas antigas. Reaplicar a faixa de preço ou atualizar os dados recompõe as páginas com cursores novos. O painel de contratações identifica o trecho ao qual suas páginas e contagens pertencem.

A pesquisa composta não inicia revalidação nem ampliação pela API. Amostras e cestas podem usar os preços locais carregados, sem recuperar acidentalmente resultados de uma sessão de API anterior. A remoção definitiva da API não faz parte desta alteração.

## Verificação funcional

- Compilação da solução em Release: zero avisos e zero erros.
- Suíte completa: **766 testes aprovados**, sem falhas.
- Comparação de desempenho optativa: teste adicional aprovado, usando bancos sintéticos temporários.
- Cobertura de sintaxe: múltiplos trechos com/sem espaços, acentos, exclusões e unidades independentes, `OU`, números aproximados, frases e blocos `C:(...)`, erros e transferência de critérios legados.
- Cobertura do leitor SQLite real: união dos resultados de buscas individuais, páginas de 49/50/51 e mais preços, sobreposição, trechos vazios, cancelamento após a primeira e a 50ª linha, retomada, reinício e filtros de preço, período e UF.
- Verificador WPF `--main-search`: métodos reais da interface com repositório sintético, sem serviços de rede; alternância, identidade/marcas, duplicatas, entrada inválida sem substituir resultados, cancelamento, invalidação e resposta atrasada de uma pesquisa anterior.

Nenhuma dessas verificações acessou o banco escolhido pelo usuário.

## Custo do carregamento

Comparação entre duas chamadas diretas ao leitor atual e duas páginas alternadas pela sequência, sobre os mesmos bancos sintéticos com descrições extensas. Medianas de três rodadas, invertendo a ordem das estratégias. As páginas retornadas foram idênticas, inclusive suas identidades e ordem.

| Linhas no banco | Duas páginas, leitor atual | Duas páginas, sequência | CPU atual / sequência | Alocações atuais / sequência |
| ---: | ---: | ---: | ---: | ---: |
| 50 | 27,633 ms | 31,790 ms | 31,250 / 31,250 ms | 16,271 / 16,270 MB |
| 1.000 | 25,977 ms | 26,890 ms | 31,250 / 31,250 ms | 30,100 / 30,099 MB |
| 10.000 | 26,511 ms | 26,815 ms | 15,625 / 15,625 ms | 30,098 / 30,099 MB |

A primeira página da sequência teve medianas de 15,360 / 13,466 / 13,733 ms, respectivamente. O working set mediano do processo foi aproximadamente 111 / 139 / 252 MB, semelhante entre as estratégias dentro de cada volume; inclui a infraestrutura dos testes e não representa a memória exclusiva da pesquisa. CPU tem resolução grosseira nessa medição. Alocações acumuladas não representam memória retida.

O relatório completo está em `artifacts/main-search-validation/comparison.json`. O cache do sistema operacional não foi limpo e o computador de validação não representa um HD antigo. Essas medições verificam equivalência e custo do mecanismo de alternância; não garantem ausência de impacto em todo hardware ou conjunto de critérios. Consultar mais trechos pode exigir mais leitura total.

## Interface e leitura

Medições no Windows, com descrições longas, dispatcher e DataGrid WPF reais. O teste de inserção usa 30 amostras por cenário; o teste de leitura também verifica busca no texto completo, cópia, cancelamento, virtualização e janela pequena com escala lógica de 150%.

| Linhas | Inserção p95 sem ordenar | Inserção p95 ordenada | Ctrl+F p95 |
| ---: | ---: | ---: | ---: |
| 50 | 0,531 ms | 13,834 ms | 20,967 ms |
| 1.000 | 0,242 ms | 2,096 ms | 20,456 ms |
| 10.000 | 0,301 ms | 0,399 ms | 0,059 ms |

Todos ficaram abaixo de 100 ms. A leitura manteve apenas 17 linhas materializadas em cada volume.

**Limitação da verificação de mouse:** duas execuções de `--reading` falharam na asserção de arrasto com o mouse nativo. A causa não foi determinada nesta tarefa. O modo explícito `--reading-routed` executou os testes existentes com eventos WPF controlados e aprovou leitura, cópia, busca, cliques, pressão prolongada e cancelamento. Ele não confirma arrasto nativo nem rolagem nas bordas. O verificador padrão continua exercitando o mouse nativo e não oculta essa falha. Os controles de gestos de produção não foram alterados.

## Reprodução

- `dotnet build PNCPKing.sln -c Release --no-restore -p:UseAppHost=false`
- `dotnet test tests/PNCPKing.Tests/PNCPKing.Tests.csproj -c Release --no-build -- xUnit.ParallelizeTestCollections=false`
- `dotnet run --project tests/PNCPKing.UiChecks/PNCPKing.UiChecks.csproj -c Release -- --main-search`
- `dotnet run --project tests/PNCPKing.UiChecks/PNCPKing.UiChecks.csproj -c Release -- artifacts/main-search-validation/ui.json`
- `dotnet run --project tests/PNCPKing.UiChecks/PNCPKing.UiChecks.csproj -c Release -- --reading-routed`

Para repetir a comparação do leitor, definir `PNCPKING_MAIN_CRITERIA_REPORT` com um caminho de saída JSON e executar somente `FullyQualifiedName~MainSearchLocalSequencePerformanceTests`, sem outras medições simultâneas. O teste cria e remove seus próprios bancos sintéticos; a variável indica somente o relatório. Não há argumento para abrir um banco real.

Os relatórios locais ficam em `artifacts/main-search-validation/`. A versão de distribuição desta alteração é `1.2.15`, com executável canônico em `artifacts/win-x64/PNCPKing.exe` e resumo em `docs/releases/v1.2.15.md`.
