# Dimensionamento da carga nacional de listas e preços homologados

Medição realizada em 20/07/2026 na API oficial do PNCP, sem consultar documentos.

## Amostras

| Amostra | Contratações | Itens | Itens com `temResultado=true` | Resposta das listas de itens |
|---|---:|---:|---:|---:|
| Publicações de 13–19/07/2026, pregão eletrônico | 10 | 130 | 0 | 164.884 bytes |
| Publicações de 20–26/07/2025, pregão eletrônico | 20 | 568 | 303 | 623.224 bytes |

Também foram consultados 24 endpoints de resultado de item. Eles retornaram 24 resultados e 29.752 bytes, média de aproximadamente 1.240 bytes por chamada.

Essas amostras não são estatisticamente representativas de todas as modalidades. Elas servem para determinar a ordem de grandeza e mostram a diferença entre contratações recém-publicadas e contratações que já tiveram tempo para homologação.

## Projeção para 1.491.875 contratações

O PNCP exige uma chamada para obter a lista de itens de cada contratação e outra chamada para cada item marcado com resultado. Considerando cenários de 5 a 15,15 itens com resultado por contratação:

| Componente | Projeção |
|---|---:|
| Chamadas para listas de itens | 1.491.875 |
| Chamadas para resultados | 7,46–22,60 milhões |
| Total adicional | 8,95–24,09 milhões de chamadas |
| Tráfego adicional aproximado | 31,5–69,4 GiB |
| Cache SQLite estimado | 19,5–38,9 GiB |

Mesmo no ritmo ideal observado na amostra e com duas chamadas simultâneas, o piso teórico fica próximo de 13–35 dias contínuos. Limites da API, `429`, indisponibilidades, novas tentativas e respostas maiores podem transformar isso em vários meses.

## Índice nacional de itens implementado

A carga indiscriminada de resultados não integra mais a fila nacional. Depois da cobertura completa das contratações e de uma autorização separada, o PNCP King mantém somente as listas de itens da janela móvel de 365 dias.

Para as 1.491.875 contratações da projeção, a carga inicial passa a ter:

| Componente | Projeção |
|---|---:|
| Chamadas para listas de itens | 1.491.875 |
| Chamadas de resultados em segundo plano | 0 |
| Tráfego das listas, pelas duas amostras | aproximadamente 22,9–43,3 GiB |

O aplicativo calcula novamente contratos restantes, espaço, duração e reserva de disco antes da autorização. No modo normal, a fila mantém checkpoint por contratação, faz apenas uma chamada de lista por vez e cede imediatamente a API às pesquisas visíveis. O modo agressivo de itens usa a concorrência adaptativa disponível, mas continua sem consultar resultados.

Na pesquisa, o FTS local identifica itens compatíveis mesmo quando o objeto da contratação é genérico. Somente esses itens, quando `temResultado=true`, acionam o endpoint de resultados. Respostas bem-sucedidas, inclusive vazias ou canceladas, ficam concluídas no banco principal até uma mudança em `dataAtualizacaoGlobal` invalidá-las.

## Índice nacional de preços implementado

Depois da conclusão das listas, uma segunda autorização estima separadamente o índice de preços. Na base medida durante a implementação havia 8.054.429 itens elegíveis, 26.711 já consultados e aproximadamente 8.027.718 chamadas restantes.

| Componente | Estimativa inicial |
|---|---:|
| Chamadas restantes de resultados | aproximadamente 8,03 milhões |
| Crescimento físico esperado | próximo de 1,8 GiB |
| Planejamento conservador de disco | 3–5 GiB |
| Duração inicial | aproximadamente 1–10 dias, recalibrada pelo rendimento real |

A autorização persiste, mas não inicia nenhuma chamada. O download em massa ocorre exclusivamente enquanto o botão **Download agressivo** da barra de preços estiver ativo. Antes dos resultados, o mesmo ciclo conclui cobertura e listas eventualmente pendentes; os modos agressivos de itens e preços são mutuamente exclusivos.

O endpoint oficial retorna uma lista. O índice conserva somente registros com situação `1 — Informado` e `valorUnitarioHomologado > 0`. Na maioria dos itens há apenas uma vencedora útil. Há exceções oficiais de divisão do fornecimento com mais de uma vencedora positiva; nesses casos todas são preservadas, sem escolher arbitrariamente uma empresa. Respostas vazias, somente canceladas, com preço zero/ausente ou `404` concluem o item sem preço útil e não geram nova chamada até uma atualização global.

O controle adaptativo aumenta a concorrência após sucessos e a reduz diante de `429`, `Retry-After`, timeout, `5xx` ou latência alta. Resultados obtidos em massa são reconstruíveis e acompanham a janela móvel; preços consultados sob demanda ou usados em cotações permanecem fixados.

Referências oficiais:

- <https://pncp.gov.br/manual/pt-br/latest/contratacao/consultar_resultados_de_item_de_uma_contratacao.html>
- <https://pncp.gov.br/manual/pt-br/latest/contratacao/consultar_item_de_uma_contratacao.html>
