# Dimensionamento da carga nacional de preços homologados

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

## Recomendação

Não incluir a carga nacional de preços automaticamente na sincronização principal. Implementar uma segunda fila, opcional e confirmada separadamente, priorizando uma destas abrangências:

1. contratações encontradas pela pesquisa do usuário;
2. uma UF ou a região Sudeste;
3. um período menor;
4. contratações já homologadas ou atualizadas recentemente.

A fila deve manter checkpoint por contratação e item, limitar-se a duas chamadas simultâneas e exibir sua própria estimativa de tráfego, tempo e espaço antes de começar.

Referências oficiais:

- <https://pncp.gov.br/manual/pt-br/latest/contratacao/consultar_resultados_de_item_de_uma_contratacao.html>
- <https://pncp.gov.br/manual/pt-br/latest/contratacao/consultar_item_de_uma_contratacao.html>
