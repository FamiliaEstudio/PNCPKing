# Calibração SQLite por PC — 14/09/2026

## Aplicabilidade do relatório recebido

O log e o relatório de desempenho sustentam a concentração do atraso no SQLite:
uma página chegou a 436,7 s, enquanto a aplicação dos lotes na interface teve
mediana de 0,2 ms. A manutenção também ultrapassou seu orçamento, com uma fatia
de 153,1 s. Esses pontos motivaram as correções. A hipótese de retenção concorrente
com aquela pesquisa não foi confirmada: a retenção longa registrada ocorreu
durante a importação, antes da consulta.

O documento da IA foi tratado como análise a conferir. A volta geral à varredura
por contratos não foi adotada: os benchmarks anteriores do projeto já mostravam
regressões nesse caminho. Também foram preservadas a ordenação por relevância,
a atomicidade da retenção/importação e os temporários em arquivo. O plano aprovado
orientou a alternativa de lotes do índice de itens e a comparação manual dos caches.

## Escopo e preservação

O perfil de pressão de recursos (Restrito, Balanceado ou Amplo) continua separado
da configuração escolhida pela avaliação. O cache anterior permanece
ativo até o usuário aplicar uma recomendação e reabrir o aplicativo. Após o pedido
adicional de otimizar a entrega inicial, o padrão de consulta do Restrito passou
a ser progressivo, ainda com 32 MiB. Essa correção não depende da calibração e
não altera a estratégia dos perfis Amplo e Balanceado. Não houve
migração, índice novo ou alteração dos contratos de busca e de cursores.

O menu **Opções → Diagnóstico → Avaliar desempenho deste PC** compara caches de
32, 64 e 96 MiB por conexão, dentro da reserva de memória. Somente no Restrito
compara também a seleção integral da página com a entrega progressiva na ordem
solicitada. Amplo e Balanceado conservam a estratégia de consulta; relevância
conserva sua janela e seus empates em todos os perfis. A consulta progressiva
percorre índices existentes na ordem da pesquisa e consulta preços por item.
Filtros muito seletivos podem preparar primeiro as chaves dos itens aceitos;
portanto, ainda não há garantia de início instantâneo em qualquer pesquisa.
A revisão e suas medições estão em [Entrega progressiva](query-stream-validation.md).

A avaliação usa conexões privadas, sem pooling, somente leitura e
`query_only=ON`. Não migra, compacta, limpa, duplica nem sincroniza o banco.
Mantém mmap, temporários em arquivo e quantidade de workers do perfil em uso.
Somente a preferência local é gravada quando o usuário aplica a recomendação.
Importação limpa essa preferência; banco diferente, criação do arquivo,
esquema, versão da calibração, RAM física ou processadores incompatíveis a
invalidam. A preferência não é incluída no backup do banco.

## Critérios

- Prazo global de cinco minutos; até 30 segundos para cada página consultada.
- Duas rodadas de café por data, limpeza com diária por data e limpeza ampla
  por proximidade, invertendo a ordem das configurações na segunda rodada.
- Comparação das primeiras duas páginas, com fingerprint das identidades em
  ordem, ausência de duplicatas e pelo menos 50 resultados na primeira página.
- Redução de pelo menos 20% nas medianas do primeiro e do décimo preço em cada
  cenário; página completa e página seguinte sem piora superior a 10%.
- Entre opções aprovadas com escores de velocidade até 10% próximos, preferência
  pelo menor pico de memória medido; em empate, pelo menor cache e pela estratégia atual.
- Orçamento de quatro conexões: cache total estimado até 25% da RAM livre e
  até 512 MiB. Abaixo de 768 MiB livres ou sob pressão crítica, avaliação
  interrompida e configuração preservada.
- Comparações incompletas, divergências, pressão de memória ou ausência de ganho
  suficiente não produzem recomendação. Os limites podem tornar o resultado
  inconclusivo em bancos/discos muito lentos; isso é informado, sem ampliar o
  prazo silenciosamente.
- Espera na fila SQLite acima de 100 ms e de 10% do tempo consultado invalida a
  amostra por interferência. Iniciar outra operação no aplicativo cancela a avaliação.

O pico de RAM é a memória privada do processo amostrada a cada 250 ms. A
comparação de picos não atribui todo o consumo ao cache. Outros programas,
coleta de lixo e cache do Windows podem afetar a medição; o primeiro acesso é
identificado, sem alegação de cache frio controlado. Os tempos da avaliação
medem a entrega pelo repositório, não a renderização WPF.

O resultado detalhado da última avaliação fica em
`%LOCALAPPDATA%\PNCP King\logs\sqlite-calibration-latest.json`, sem descrições
pesquisadas ou identificadores de preços em texto. Os nomes dos cenários e os
fingerprints permitem conferir comparações sem expor o conteúdo das linhas.

## Entrega progressiva e manutenção

A busca progressiva aplica unidade/descrição e consulta os resultados
apenas dos candidatos necessários para preencher a página. Cada entrega é um
prefixo definitivo; o cursor permanece na última linha entregue, inclusive
quando um item tem vários resultados. Nenhuma fila nacional de modelos é
mantida em memória ou persistida entre pesquisas.

As métricas existentes são preservadas. `local-first-visible-row` historicamente
marca a entrada no buffer; as novas `local-first-applied-row` e
`local-ten-applied-rows` marcam a inclusão efetiva na coleção da grade. Nenhuma
delas pretende medir o instante físico de apresentação do monitor.

O prazo global da manutenção abrange inspeções e etapas auxiliares. A cobertura
é preparada em transações de até sete dias, liberando o escritor entre elas.
Cada página de sincronização continua atômica com seu checkpoint. Checkpoints
ordinários têm sua própria oportunidade ociosa, sem serem acrescentados a uma
fatia já consumida nem duplicados pela otimização de estatísticas. As oportunidades
alternam com as demais tarefas, mesmo quando ainda existe manutenção pendente.

Consultas longas e operações retomáveis usam interrupção nativa e verificação
periódica de cancelamento SQLite, removidas antes de devolver a conexão ao
pool. Cancelamento nativo é convertido no contrato existente de cancelamento.
Retenção e referências de cotação preservam sua atomicidade; a importação não
foi repartida. Um prazo pede interrupção; não é garantia de preempção imediata
de I/O físico ou rollback.

A notificação de atividade sinaliza o cancelamento imediatamente, mantendo os
callbacks assíncronos. Isso remove a dependência anterior de uma thread livre
para sequer marcar a fatia como cancelada, observada na validação sob carga.

## Validação

Os testes cobrem equivalência entre estratégias, filtros, datas ausentes,
empates, vários resultados por item, progresso antes de completar a página,
retomada sem repetição, interrupção SQL com rollback e reutilização da conexão,
liberação do escritor por intervalo, limites de memória e recomendação,
persistência compatível, cancelamento e prazo da avaliação.

O benchmark opcional exige `PNCPKING_CALIBRATION_COPY` apontando para uma cópia
em diretório de benchmark; `PNCPKING_CALIBRATION_REPORT_DIR` recebe os JSONs.
Filtro: `SqliteCalibrationTests.IsolatedCopyEvaluatesProfilesWithoutChangingDatabase`.
Os perfis são configurados explicitamente como Restrito e Amplo; a amostragem
de memória acompanha o PC executor real. Isso não simula a RAM nem o HD do
computador do serviço. O teste verifica tamanho e data de modificação da cópia.

### Resultados da primeira entrega, antes da revisão da consulta

- Compilação Release da solução: zero avisos e zero erros.
- Suíte completa final: **521 aprovados, zero falhas**, em 1 min 8 s.
  Registro: `artifacts/report-review-validation/calibration-validated.trx`.
- Janela WPF verificada com dados sintéticos: apresentação, aplicação,
  restauração e proteção contra progresso atrasado aprovadas. O teste não abriu
  o banco real. A captura `ui-calibration.png` contém valores fictícios de teste,
  não resultados de desempenho.
- Benchmark na cópia isolada de 17.222.520.832 bytes: os dois testes de proteção
  do banco passaram, mas **nenhuma configuração foi recomendada**.

| Perfil explícito | Consultas registradas | Tempo das consultas interrompidas | Maior pico do processo | Menor RAM livre |
|---|---:|---:|---:|---:|
| Restrito | 9 | 30,018–30,052 s | 141,6 MiB | 16.374,1 MiB |
| Amplo | 9 | 30,017–30,082 s | 157,2 MiB | 16.322,9 MiB |

Nenhuma dessas consultas entregou a primeira linha antes do limite. Cada
avaliação terminou aos cinco minutos sem completar as duas rodadas exigidas.
Não houve pressão de memória; tamanho e data de modificação da cópia foram
preservados. Os picos da tabela agregam configurações distintas e **não** são
uma comparação aprovada entre caches. Resultados brutos em
`artifacts/report-review-validation/calibration-Restrito.json` e
`calibration-Amplo.json`; execução em `calibration-benchmark.trx`.

Esses resultados históricos verificaram a interrupção e a decisão conservadora,
sem comprovar ganho de velocidade naquela versão. A revisão posterior da busca
está registrada em [Entrega progressiva](query-stream-validation.md). A melhoria no computador do serviço deve ser medida nele,
pelo novo botão. Não foram usados os números ilustrativos do usuário como meta
atingida ou evidência de desempenho.

Durante a validação, foram corrigidas duas expectativas de testes: a cobertura
retorna também dias ainda não preparados, e o menu passou a ter um novo comando.
Separadamente, um teste existente expôs o atraso anterior no sinal de cancelamento
sob carga; a causa foi corrigida com sinalização imediata. Nenhuma falha ficou
pendente na suíte final. Os registros intermediários foram preservados.

A entrega usa exclusivamente `artifacts/win-x64/PNCPKing.exe`, sem PDBs ou banco
na distribuição. As alterações anteriores do repositório foram preservadas.
