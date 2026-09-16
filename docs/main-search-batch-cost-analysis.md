# Custo da pesquisa por lotes de correspondências — 10/09/2026

Este relatório registra o comportamento medido antes da remoção do corte de
20 mil correspondências. A alteração posterior está documentada em
`main-search-validation.md`. O protótipo de testes acompanha o repositório
atual: o antigo cenário `current-item-index`, que forçava o desvio do corte,
tornou-se redundante e foi retirado. Os números históricos abaixo e os arquivos
brutos das 75 medições foram preservados.

A proposta é viável: selecionar os IDs dos itens encontrados pelo índice textual,
processar os candidatos em lotes e carregar os registros completos somente para
os preços que serão exibidos. O requisito que encarece a primeira página é
preservar a ordem global por data ou proximidade. Essa ordem depende de campos
da contratação, mesmo quando a correspondência textual já foi encontrada no item.

O estudo acrescenta somente protótipos e verificações ao projeto de testes. Não
altera o aplicativo, o executável publicado ou o banco escolhido pelo usuário.

Foram reutilizados 17.222.520.832 bytes de uma cópia isolada no HD mecânico
ST1000VM002-1SD102. A máquina tem aproximadamente 32 GiB de RAM e 16 processadores
lógicos. A cópia contém 1.447.614 contratações, 15.296.101 itens e 8.225.720
resultados, com cobertura até 30/08/2026. Ela permaneceu no esquema 26, sem
inicialização, migração, limpeza ou compactação; as consultas aplicaram a janela
de 10/10/2025 a 10/09/2026. As conexões usam SQLite somente leitura e
`PRAGMA query_only=ON`.

As três consultas nacionais encontraram **41.600 correspondências textuais de
itens**, antes dos demais filtros. Esse número não é uma contagem de contratações
ou de preços utilizáveis. Na ordenação por candidatos, restaram **36.886 IDs**
após considerar período e hidratação completa.

Tempos em milissegundos, incluindo a preparação na primeira página. As medianas
abaixo consideram as execuções concluídas; a coluna de interrupções informa
quantas das três rodadas excederam 120 segundos. Não se deve interpretar uma
única execução concluída como uma mediana representativa das três tentativas.

| Consulta e estratégia | Primeiros 50 | Próximos 50 | Interrupções |
| --- | ---: | ---: | ---: |
| Com diária, recentes — atual | 1.500 | 1.402 | 0/3 |
| Com diária, recentes — lotes brutos de 1.000, ordem diferente | 1.119 | 609 | 0/3 |
| Com diária, recentes — lotes ordenados de 1.000 | 1.080 | 215 | 0/3 |
| Com diária, recentes — fila de todos os preços válidos | 1.492 | 2 | 0/3 |
| Com diária, proximidade — atual | 1.460 | 1.472 | 0/3 |
| Com diária, proximidade — lotes ordenados de 1.000 | 1.153 | 142 | 0/3 |
| Com diária, proximidade — fila de todos os preços válidos | 1.450 | 3 | 0/3 |
| Sem diária, recentes — atual | 60.338 | 32.364 | 2/3 |
| Sem diária, recentes — caminho atual forçado pelo índice de itens | 2.743 | 2.416 | 1/3 |
| Sem diária, recentes — lotes brutos de 250, ordem diferente | 104 | 2 | 0/3 |
| Sem diária, recentes — lotes ordenados de 250 | 694 | 2 | 0/3 |
| Sem diária, recentes — lotes ordenados de 1.000 | 746 | 2 | 0/3 |
| Sem diária, recentes — fila de todos os preços válidos | 2.463 | 3 | 2/3 |

Texto com diária: `Serviço limpeza -odontológico "diária`. Texto sem diária:
`Serviço limpeza -odontológico`. Foram comparadas as primeiras 100 identidades
de preço, na mesma ordem, em todas as execuções concluídas. Os lotes ordenados
e a fila de preços coincidiram integralmente com a referência. Os lotes brutos
não coincidiram. Na consulta ampla, os primeiros 100 preços dos lotes brutos
nem sequer intersectaram os primeiros 100 da ordenação por data.

Na execução concluída da estratégia atual para a consulta ampla, o progresso
contabilizou **14.336 exames de contratações** para duas páginas. Essa contagem
pode incluir a contratação de fronteira novamente na página seguinte. O
protótipo ordenado de 250 avaliou **500 itens candidatos** para as mesmas duas
páginas, além de preparar antecipadamente a ordem dos 36.886 IDs. Portanto,
o ganho não significa eliminar toda leitura dos dados das contratações: ele
evita expandir e testar os itens de milhares de contratações que não produzirão
os próximos preços da página.

Os resultados mostram por que não basta trocar a varredura de contratações por
uma paginação arbitrária do FTS. Na consulta com diária, os lotes em ordem de
`rowid` trouxeram somente **94 dos mesmos 100 preços** da ordenação por data e
**32 dos mesmos 100** por proximidade. Os preços retornados atendem aos filtros,
mas a seleção e a ordem das primeiras páginas mudam. Ordenar cada lote depois
de processá-lo não resolve a posição dos candidatos que ficaram para os lotes
seguintes.

O protótipo com candidatos previamente ordenados primeiro faz os relacionamentos
entre os IDs encontrados e os campos de ordenação das contratações. Depois,
filtra os itens em lotes, consulta os resultados apenas desses candidatos,
guarda os preços válidos excedentes e carrega os 51 campos dos 50 preços da
página. Ele não percorre todos os itens de cada contratação. Ainda acessa a
tabela de contratações, em conjunto, porque data, localização e atualização do
snapshot são necessárias à pesquisa correta. Nenhuma dessas etapas acessa o PNCP.

Os custos que precisam orientar uma implementação são:

- **Preparação e leitura do HD.** Ordenar os candidatos exige conhecer os campos
  de ordenação de todo o conjunto. No piloto exploratório, a primeira execução
  dessa etapa demorou **113,2 segundos**, com cerca de **2,3 segundos de CPU**
  na operação inteira. Execuções posteriores da preparação foram muito menores.
  Isso é compatível com espera por leitura, mas não houve medição direta de bytes
  físicos ou isolamento de outras cargas. O piloto não teve limpeza controlada
  do cache e não deve ser tratado como um teste frio reproduzível. O protótipo
  não elimina, por si só, a latência do primeiro acesso no HD.
- **Quantidade filtrada até obter uma página.** Um lote de 1.000 correspondências
  não significa 1.000 preços válidos. Na busca com diária, o protótipo ordenado
  examinou 14.000 candidatos para produzir as duas primeiras páginas por data,
  e 12.000 por proximidade. Na busca ampla, 1.000 candidatos já bastaram. O
  tamanho adequado depende do rendimento dos filtros e de quantos resultados
  cada item possui.
- **Memória da fila.** A lista de 36.886 IDs `Int64` utilizou **512 KiB** de
  capacidade alocada. Isso não inclui caches do SQLite, objetos dos preços,
  textos, buffers temporários de ordenação ou outros componentes do processo.
  Não é necessário colocar todo o banco em um `Dictionary`.
- **Alocações temporárias e CPU.** Na mediana das rodadas da consulta com diária
  por data, filtrar 41.600 itens uma vez acumulou cerca de **836 MiB de alocações
  gerenciadas**; os lotes ordenados de 1.000, que avaliaram 14.000 itens,
  acumularam cerca de **335 MiB**. O caminho atual acumulou **1.674 MiB** nas
  duas páginas. O tempo de CPU foi aproximadamente 2,75 s no caminho atual e
  1,30 s nos lotes ordenados. São volumes de alocação ao longo
  da execução, coletáveis pelo GC, e não memória que permanece ocupada nem pico
  de RAM. `MatchesItem` normaliza e divide descrições e unidades em palavras;
  reduzir o número de avaliações também reduz esse trabalho. Não foi feito um
  perfil que atribua cada byte a uma função.
- **Repetição nas próximas páginas.** O caminho atual pelo índice de itens
  reaplica a expressão completa e a seleção ordenada a cada página. Uma fila
  mantida durante a pesquisa evita repetir trabalho. A alternativa que filtra
  tudo de uma vez encontrou 456 preços na busca com diária e praticamente
  eliminou o custo de seleção da segunda página, mas paga toda a filtragem
  antes da primeira. Na busca ampla, houve execuções dessa alternativa que
  excederam o limite de 120 segundos.
- **Manutenção do estado.** Uma fila por pesquisa deve distinguir texto, filtros,
  período e ordenação; guardar candidatos ainda não examinados e preços já
  encontrados; e invalidar-se quando o banco ou os dados relevantes mudarem.
  Limpeza diária, sincronização, restauração e troca de banco precisam impedir
  que a fila reapresente dados antigos. Os `rowid` do protótipo não são um
  contrato adequado de persistência entre compactações. A retomada persistida
  deve conservar as identidades lógicas já usadas pelo sistema.

O custo de integração é moderado e concentra-se no controle dessa fila, no
cursor e na invalidação, além da consulta SQL. Os pontos existentes são o
repositório de preços, os contratos de paginação no Core e a continuação da
pesquisa no `MainViewModel`. O protótipo não exigiu alteração de esquema,
reconstrução do FTS ou download adicional. A implementação final ainda precisaria
validar cancelamento durante consultas longas e impedir que a continuação em
segundo plano dispute o HD sem limite com outras operações.

A recomendação é experimentar **lotes de candidatos na ordem solicitada, com
reaproveitamento da fila entre páginas**. Um lote inicial de 1.000 é um ponto de
partida razoável para a consulta com diária, não um tamanho ótimo universal:
lotes de 250 favorecem a consulta ampla e lotes de 5.000 podem avaliar itens
desnecessários antes de entregar a página. Uma adaptação pelo rendimento
observado seria uma hipótese para a implementação, ainda não uma melhoria
medida neste estudo.

O limite fixo de 20.000 correspondências não informa quantos candidatos
produzirão preços válidos nem quanto custará percorrer as contratações. A
pesquisa com unidade já evita esse desvio no código atual. A consulta ampla
continua sujeita a ele e apresentou o pior comportamento neste ensaio. Os dados
justificam rever essa escolha; não justificam substituir todos os caminhos de
pesquisa por uma única estratégia.

Um `Dictionary` pode auxiliar deduplicação ou associação por chave se surgir
essa necessidade concreta. Para consumir candidatos sequencialmente, lista e
fila são suficientes. O ganho medido vem de **reduzir leituras e avaliações
repetidas e conservar o trabalho entre páginas**, não do tipo de coleção por si
só. Um índice adicional que aproxime unidade/data dos candidatos poderia atacar
o primeiro acesso, mas seu tamanho, construção, migração e custo nas atualizações
não foram medidos e ele não foi criado.

As medições usam três rodadas, alternando a ordem das estratégias, com duas
páginas de 50 preços por execução e limite de 120 segundos para a operação.
O teste compara as identidades de contratação, item e resultado, incluindo sua
ordem, e verifica ausência de duplicatas. Para a busca ampla, o mesmo
repositório de produção foi também executado forçando seu caminho existente
pelo índice de itens; esse caminho fornece a referência quando a estratégia
padrão não termina dentro do prazo.

Há limites importantes de comparação: não foi esvaziado o cache do sistema
operacional; os protótipos reutilizam uma conexão entre páginas, enquanto o
repositório usa suas conexões habituais; a leitura dos 51 campos está incluída,
mas a construção dos modelos de produção e da interface não está incluída nos
protótipos. Todas as conexões de medição recebem um verificador nativo de
cancelamento a cada 1.000 instruções SQLite, que acrescenta custo. O aplicativo
não recebeu esse verificador. Portanto, os números comparam estratégias nesse
ensaio e não garantem o tempo que o usuário verá após uma implementação.

Os cenários medidos usam abrangência nacional, sem restrição de valor, e
ordenação por data ou proximidade. Relevância, cláusulas explícitas de
contratação, outros filtros geográficos, pesquisas concorrentes e invalidação
das filas exigiriam validação própria antes de integrar a solução ao aplicativo.

O código reproduzível está em
`tests/PNCPKing.Tests/MainSearchBatchCostTests.cs`. A execução optativa exige
`PNCPKING_BATCH_COST_COPY` apontando para uma cópia em diretório de benchmark,
`PNCPKING_BATCH_COST_REPORT` para o JSON e, opcionalmente,
`PNCPKING_BATCH_COST_ROUNDS` (padrão 3). O filtro do teste é
`FullyQualifiedName~MainSearchBatchCostTests.IsolatedCopy`, em Release. As
medições brutas, planos de consulta, resumo CSV, ambiente e relatórios TRX estão
em `artifacts/batch-cost-analysis/`. No JSON bruto, o campo `sqlite` identifica
a versão do pacote Microsoft.Data.Sqlite, não a versão do motor SQLite.

Validação: compilação do projeto de testes em Release; três testes sintéticos
aprovados (data, proximidade e interrupção SQLite); teste optativo aprovado com
75 medições e cinco interrupções registradas como resultados de desempenho.
As verificações finais confirmaram que o tamanho e a data de modificação da
cópia permaneceram iguais. Não houve publicação de executável neste estudo.
