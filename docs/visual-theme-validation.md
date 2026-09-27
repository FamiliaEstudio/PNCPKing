# Validação do refinamento visual

Tema claro e compacto aplicado às 18 janelas XAML e às duas janelas construídas
em C#. A referência anterior é o commit `ef1c998`.

## Implementação

- Cores e estilos em `src/PNCPKing.App/Resources/Theme.xaml`, carregado pelo
  aplicativo e pelos verificadores WPF. Azul royal, fundos claros e um filete
  dourado no cabeçalho principal; o logo original foi preservado.
- Botões continuam sendo `Button`, com os mesmos comandos, eventos, conteúdo,
  dimensões e papéis de Enter/Escape. O destaque de “Pesquisar” não altera
  `IsDefault`. Templates simples somente para botões e cabeçalhos das abas.
- Tabelas mantêm virtualização, reciclagem, altura de linha de 32 pixels no
  `GridReader`, seleção, ordenação, cópia e configuração de colunas. As cores
  funcionais de fixação, cesta, cancelamento e alertas foram preservadas.
- Nenhuma dependência, fonte ou imagem adicional, efeito visual pesado, animação
  nova, consulta ao banco ou atividade periódica foi acrescentada.
- Nenhuma alteração de modelos, regras, serviços de dados ou configurações
  persistidas. O tema compõe a versão 1.2.17.

## Verificações

- Compilação da solução e dos verificadores WPF em Release: sem erros ou avisos.
- `--layout`: posicionamento das janelas, agrupamentos, escolha e persistência
  das colunas, Enter/Escape, navegação de foco, texto informativo selecionável,
  independência entre a cor da aba e seu conteúdo, e contraste dos botões
  habilitados/desabilitados aprovados.
- `--main-search`: alternância, deduplicação, identidade, cancelamento,
  invalidação e respostas atrasadas aprovados, com serviços sintéticos.
- `--reading-routed`: leitura, cópia, busca, seleção e gestos controlados
  aprovados. Foram realizados 17 contêineres de linha tanto para 50 quanto para
  1.000 e 10.000 registros; o maior p95 de resposta durante a busca foi 29,39 ms.
- `--reading`: falha na asserção de seleção por arrasto nativo. A mesma asserção
  já consta em [validação anterior](main-search-plus-validation.md). O modo
  controlado não valida arrasto nativo nem rolagem nas bordas; a falha não foi
  ocultada e o código dos gestos não foi alterado.
- Suíte geral: 775 testes aprovados e uma falha em
  `ManualBatch_ConsultsNextFiftyAndLargeRequestRequiresConfirmation`, ao remover
  um arquivo temporário `BATCHES.DB-SHM.tmp` bloqueado, em `TestDatabase.DisposeAsync`.
  A repetição isolada desse teste foi aprovada. Não foi feita alteração nessa
  infraestrutura de testes.
- Na preparação da publicação 1.2.17, o script padrão repetiu a suíte completa:
  **776 testes aprovados, nenhuma falha**. As verificações WPF de layout e do
  tema também passaram antes de gerar o executável canônico, sem arquivos PDB.
- Comparação estrutural dos XAML contra a referência: comandos, eventos,
  bindings funcionais, textos, dimensões declaradas e ordem dos controles
  preservados. As diferenças são estilos, cores e o filete decorativo.

## Evidências visuais e desempenho

As capturas e relatórios ficam em `artifacts/theme-validation/`:

- [Tela principal anterior](../artifacts/theme-validation/before/MainWindow.png)
  e [tela principal atual](../artifacts/theme-validation/after/MainWindow.png).
- `before/` e `after/`: 36 apresentações de janelas e abas em cada versão,
  incluindo a tela principal em 1366×768, 1920×1080 e 800×520, com escalas
  lógicas de 100%, 125% e 150%.
- `appearance.json` em cada pasta: dimensões dos controles, tempo de abertura
  do layout e memória do processo. `comparison.json`: comparação de dimensões.
- `before-performance.json` e `after-performance.json`: 30 amostras por cenário
  de inclusão de páginas, com 50, 1.000 e 10.000 linhas, com e sem ordenação.
  Todos os cenários ficaram abaixo do limite de p95 de 100 ms do verificador.

As 860 medições de controles nos 36 layouts mantiveram a mesma quantidade e
ordem; a maior diferença dimensional foi de 0,04 pixel lógico. No ensaio de
inclusão de resultados, os p95 de resposta do dispatcher foram:

| Linhas | Antes, sem ordenar | Depois, sem ordenar | Antes, ordenado | Depois, ordenado |
| --- | ---: | ---: | ---: | ---: |
| 50 | 0,63 ms | 0,50 ms | 16,45 ms | 7,02 ms |
| 1.000 | 0,26 ms | 0,27 ms | 1,73 ms | 2,01 ms |
| 10.000 | 0,25 ms | 0,32 ms | 0,44 ms | 0,38 ms |

Na captura isolada da tela principal, a abertura do layout foi de 460,20 ms
antes e 411,52 ms depois; o working set do verificador foi de 204,77 MiB e
206,34 MiB, respectivamente. São amostras indicativas, sujeitas a cache, JIT e
variação do ambiente; não demonstram aceleração do programa.

As capturas leem os layouts XAML com o tema compilado e dados fictícios, sem
executar o ciclo de serviços das janelas. Os testes de interação usam os
controles de produção. Os tempos de abertura capturados incluem interpretação
do layout sintético; a memória inclui o processo do verificador e as capturas.
Esses números não medem a inicialização completa do programa nem o custo de
acesso ao banco. A comparação anterior usa os XAML e recursos originais
exportados em `source-before/`.

As escalas são simuladas com `LayoutTransform`, sem mudar a configuração do
Windows. A combinação extrema de 800×520 com 150% já deixa a área de resultados
sem espaço na versão anterior; reorganizar essa tela está fora desta alteração
estética. As medições não garantem ausência de impacto no HD antigo: permanece
necessária a observação no equipamento de destino, em uso real.

## Reprodução no Windows

```powershell
dotnet build PNCPKing.sln -c Release
dotnet build tests/PNCPKing.UiChecks/PNCPKing.UiChecks.csproj -c Release
dotnet run --project tests/PNCPKing.UiChecks -c Release --no-build -- --layout
dotnet run --project tests/PNCPKing.UiChecks -c Release --no-build -- --main-search
dotnet run --project tests/PNCPKing.UiChecks -c Release --no-build -- --reading
dotnet run --project tests/PNCPKing.UiChecks -c Release --no-build -- --reading-routed
dotnet run --project tests/PNCPKing.UiChecks -c Release --no-build -- --appearance src/PNCPKing.App artifacts/theme-validation/after
dotnet run --project tests/PNCPKing.UiChecks -c Release --no-build -- artifacts/theme-validation/after-performance.json
```

Para repetir as imagens da referência, usar `--appearance-baseline` com a pasta
dos XAML anteriores (incluindo seu `App.xaml`) e uma pasta de saída separada.
Os verificadores acima não recebem caminho de banco de dados e não iniciam o
aplicativo real.
