# Validação da janela de 11 meses — 10/09/2026

A implementação usa a janela inclusiva de 10/10/2025 a 10/09/2026. A publicação do executável permanece para a etapa posterior de implantação.

## Banco e ambiente

O benchmark usou duas cópias isoladas do mesmo banco de teste, com esquema inicial 26 e cobertura declarada de 31/08/2025 a 30/08/2026. As duas cópias foram colocadas no SSD NVMe XPG GAMMIX S70 BLADE, com Windows e .NET 8. O banco original foi preservado.

| Medida | Antes | Depois |
| --- | ---: | ---: |
| Tamanho físico | 17.222.520.832 bytes | 14.256.984.064 bytes |
| Contratações | 1.447.614 | 1.269.236 |
| Itens PNCP | 15.296.101 | 13.304.420 |
| Resultados de itens | 8.225.720 | 7.072.216 |

Foram liberados 2.965.536.768 bytes, aproximadamente 2,97 GB decimais. Uma referência de cotação expirou e um item foi marcado para reconfirmação. Projetos e itens das cotações foram preservados.

A migração, limpeza, reconciliação dos índices, compactação e verificações levaram 1.141,5 segundos, aproximadamente 19 minutos, sem incluir a preparação das cópias nem as pesquisas comparativas. A compactação e suas verificações consumiram 507,5 segundos desse total. Integridade do SQLite, chaves estrangeiras e índices FTS foram validados antes de marcar a compactação como concluída.

O snapshot terminava em agosto, enquanto a validação ocorreu em setembro. A redução observada corresponde aos dados efetivamente vencidos nessa cópia; não representa uma projeção percentual para outros bancos.

## Pesquisas equivalentes

As duas cópias receberam a mesma janela de 11 meses, os mesmos termos, ordenação por data mais recente e limite de 50 resultados. Foram executadas três rodadas por termo, alternando a ordem entre as cópias. A tabela apresenta medianas, sem limpeza do cache do sistema operacional entre rodadas.

| Termo | Contratações antes | Contratações depois | Preços antes | Preços depois |
| --- | ---: | ---: | ---: | ---: |
| café | 38,16 ms | 29,80 ms | 10,72 s | 9,86 s |
| papel | 47,15 ms | 36,35 ms | 3,78 s | 3,51 s |
| luva | 13,53 ms | 9,93 ms | 6,64 s | 6,20 s |
| manutenção | 775,26 ms | 609,26 ms | 0,97 s | 0,90 s |

Todas as rodadas devolveram 50 contratações e 50 preços. A sequência dos identificadores das primeiras 50 contratações foi comparada e permaneceu igual nas duas cópias. São medições locais em SSD, sem extrapolação para outros equipamentos ou para o HD mecânico.

## Interrupção e validações funcionais

A solução compilou em Release sem avisos nem erros. A suíte final aprovou os 471 casos, além da execução efetiva do benchmark em banco grande. O modelo Excel mais recente, com o cabeçalho em maiúsculas, foi incorporado e validado.

A primeira execução em uma cópia no HD mecânico foi interrompida durante a transação de limpeza. Ao reabrir essa cópia, as três contagens originais permaneceram intactas, a data da limpeza continuava pendente e a compactação estava marcada como não concluída. As cópias temporárias foram descartadas após a validação.

Os testes cobrem datas reais e ausentes no Excel, captura web anterior à exportação, preservação do modelo, calendário e anos bissextos, exclusão de preços fixados, cotações mistas, publicação ausente, migração 26→27, rollback, falta de espaço, cancelamento e repetição única da compactação. Também cobrem importação de backups e cotações, pacotes atrasados do Guard e invalidação da sessão persistente de pesquisa.

Os relatórios locais estão em `artifacts/retention-validation/retention-comparison.json`, `hdd-interruption.json` e `test-results/`. Para repetir o benchmark, coloque uma cópia isolada chamada `retention-baseline.db` em uma pasta nova, informe seu caminho na variável `PNCPKING_RETENTION_BENCHMARK_BASELINE` e execute o teste `RetentionPerformanceTests`. A candidata é criada automaticamente; um arquivo existente com esse nome nunca é sobrescrito.
