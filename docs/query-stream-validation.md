# Entrega progressiva da pesquisa local — 14/09/2026

## Alteração e aplicabilidade

O pedido adicional de otimizar o início da pesquisa motivou a revisão da
consulta, depois de a primeira implementação da calibração não entregar preços
antes dos limites do benchmark. Preparar todos os candidatos na ordem global
continuava caro: dividir apenas a consulta seguinte em lotes de 32 não resolvia
a espera inicial.

O padrão **Restrito** passa a usar a consulta progressiva, com os mesmos **32 MiB
por conexão**. Amplo e Balanceado mantêm sua estratégia. A ampliação do cache
continua manual, condicionada à avaliação; as proteções de memória permanecem
independentes. A versão da calibração passou a 2, invalidando recomendações
medidas com a implementação anterior. Restaurar o padrão retorna a essa consulta
progressiva e ao cache original do perfil, na próxima abertura.

A consulta percorre os índices existentes de publicação e de itens. O FTS
restringe as chaves candidatas, sem expandir antecipadamente todos os registros
de itens, contratações e preços. Uma verificação pelo índice de resultados evita
carregar itens sem preço ativo positivo. Cada item consultado pode fornecer
vários resultados, entregues individualmente em ordem definitiva. A página
conserva o resultado excedente necessário para decidir se há continuação.

Na ordenação por proximidade, os grupos geográficos são visitados na mesma
ordem anterior; cada grupo conserva data, contratação, item e sequência do
resultado como desempates. As datas nulas e vazias continuam equivalentes.
O cursor mantém o item de fronteira elegível para seus resultados restantes e
permite ao índice de datas avançar nas páginas seguintes.

Unidade e aproximação não estão integralmente representadas no FTS. Quando
existem, uma amostra limitada a 128 itens verifica quanto o filtro descarta.
Se aceitar no máximo um quarto da amostra, a consulta filtra os itens antes
de ordenar suas chaves; do contrário, percorre as chaves pela ordem da pesquisa.
Isso evita decidir pela quantidade de unidades escritas ou pela contagem bruta
do FTS. É uma estimativa de seletividade, sujeita à distribuição dos dados;
não é um benchmark do hardware e não altera o conjunto nem a ordem dos resultados.
Filtros seletivos ainda podem exigir preparação antes do primeiro preço.

A consulta de relevância e as cláusulas explícitas de contratação conservam o
tratamento anterior, inclusive os empates. Os contratos públicos de busca e
cursores, o esquema, os índices permanentes, mmap, concorrência e
`temp_store=FILE` foram preservados. Não há lista nacional de modelos mantida
entre pesquisas.

## Sugestões avaliadas

- **Voltar aos lotes de contratos no Restrito:** comparados explicitamente por
  data, com unidade, no mesmo banco e perfil. Não foram adotados como caminho
  geral; a comparação abaixo registra o atraso para obter dez preços e páginas.
- **Produtores progressivos:** a seleção por data/proximidade e a expansão de
  preços foram separadas. Não é necessário acumular 32 preços para entregar o
  primeiro; cada prefixo publicado já tem sua ordem garantida.
- **Manutenção:** mantidas as correções de prioridade Background nas gravações
  ordinárias, prazo abrangendo inspeção e atualizações auxiliares, cancelamento
  nativo, cobertura em transações de sete dias e oportunidade própria para WAL.
  Retenção com referências e cestas permanece atômica; importação e reconciliação
  integral não receberam uma divisão arbitrária. Checkpoints explícitos do backup
  são distintos dos checkpoints ordinários da manutenção.
- **Temporários na RAM:** não adotados. O trabalho reduz expansão de registros e
  leituras desnecessárias sem aumentar o cache por conta própria.

## Método de validação

`MainSearchStreamingPerformanceTests.IsolatedCopyComparesProgressAndTwoPagePrefixes`
compara a consulta anterior, a progressiva e, para data, os lotes de contratos.
São duas rodadas, invertendo a ordem das estratégias, duas páginas de até 50
preços, perfil Restrito explícito, cache de 32 MiB e conexões privadas somente
leitura. O limite exploratório é de 45 segundos por página; o botão do aplicativo
continua limitado a 30 segundos por consulta e cinco minutos por avaliação.

A cópia de 17.222.520.832 bytes não recebe inicialização, migração, escrita,
compactação nem downloads. Tamanho e data de modificação são verificados. Os
resultados registram hashes das identidades; todas as sequências obtidas,
inclusive prefixos interrompidos, devem coincidir, sem repetições. Uma consulta
interrompida não conta como página concluída nem como prova de equivalência do
restante que não foi lido.

Variáveis optativas: `PNCPKING_STREAM_COPY` deve apontar para a cópia em um
diretório de benchmark; `PNCPKING_STREAM_REPORT` recebe o JSON. Sem essas
variáveis, a suíte normal não abre esse banco. O teste reproduzível está no
projeto de testes; as medições desta execução estão em
`artifacts/query-stream-validation/comparison.json`.

Não houve esvaziamento controlado do cache do sistema operacional. As execuções
foram precedidas por investigação e consultas experimentais: nem mesmo a rodada
zero representa cache frio. O computador executor é diferente do PC do serviço.
Os tempos medem a entrega pelo repositório; a aplicação à coleção da grade é
verificada separadamente com o dispatcher e o buffer WPF reais.

As experiências intermediárias foram preservadas. A preparação integral das
chaves para café e a materialização antecipada de grupos de proximidade foram
rejeitadas porque atrasaram o primeiro preço. Uma primeira tentativa de comparação
iniciou dois processos de teste concorrentes; ambos foram encerrados e suas
amostras descartadas (`comparison-discarded-overlap.json`). A comparação válida
foi reiniciada e executada por um único processo.

## Resultados da comparação isolada

Tempos em segundos, mediana das duas rodadas. “>45” indica que a marca da
coluna não foi atingida em nenhuma das duas consultas antes do limite; não é
uma mediana estimada.

| Cenário | Consulta | Primeiro preço | Dez preços | Página 1 | Página 2 |
|---|---|---:|---:|---:|---:|
| cafe-recente | Anterior | >45 | >45 | >45 | — |
| cafe-recente | Progressiva | 0,038 | 0,094 | 0,334 | 6,960 |
| cafe-recente | Contratos | 0,262 | >45 | >45 | — |
| limpeza-diaria-recente | Anterior | 3,324 | 3,324 | 3,324 | 0,738 |
| limpeza-diaria-recente | Progressiva | 0,762 | 0,765 | 0,776 | 0,771 |
| limpeza-diaria-recente | Contratos | >45 | >45 | >45 | — |
| limpeza-proximidade | Anterior | >45 | >45 | >45 | — |
| limpeza-proximidade | Progressiva | 0,332 | 0,620 | 7,042 | 26,423 |

A progressiva concluiu 100 preços em cada uma das seis medições, sem duplicatas.
Na pesquisa com diária, as 100 identidades e sua ordem coincidiram também com a
consulta anterior. Café e proximidade tiveram a consulta anterior interrompida;
a comparação real nesses casos fica limitada à estabilidade entre rodadas e
aos prefixos disponíveis dos lotes de contratos. Os testes sintéticos verificam
a equivalência completa entre consultas nesses ordenamentos e filtros.

Os lotes de contratos devolveram apenas oito preços de café na rodada zero e
um na rodada um; nenhum de diária. Não completaram uma página em qualquer das
quatro medições. Portanto, a troca geral para esse caminho não foi adotada.

A consulta com diária já foi rápida na repetição anterior: 0,754 s por página,
contra 0,782 s na progressiva. A melhora não é uniforme em toda execução. A
segunda página por proximidade na progressiva variou de 22,650 a 30,195 s; a
avaliação de cinco minutos pode continuar inconclusiva por esse limite. A
correção da consulta não constitui uma recomendação para ampliar o cache.

O benchmark isolado passou, com 16 medições, em 7 min 33 s. As interrupções são
resultados de desempenho registrados, não páginas concluídas. Registro:
`artifacts/query-stream-validation/stream-comparison-isolated.trx`.

## Compilação e testes

A solução completa compilou em Release sem avisos nem erros. A suíte final
aprovou **533 testes, sem falhas**, em 1 min 12 s. Registro:
`artifacts/query-stream-validation/stream-full-suite.trx`. Nenhuma falha de teste
ficou pendente. O encerramento da tentativa de benchmark concorrente está
registrado separadamente; não foi uma falha funcional da suíte.

Os testes incluem as duas estratégias com/sem unidade, data e proximidade,
filtros de preço/estado, snapshot desatualizado, datas ausentes/vazias, empate de
resultados, páginas de uma linha e cancelamento imediatamente após o primeiro
preço — inclusive quando os resultados pertencem ao mesmo item. Verificam
retomada sem perdas ou duplicatas, limites de memória, restauração do padrão e
invalidação de calibração antiga. As verificações de interrupção nativa, rollback
e reutilização de conexões da primeira entrega permanecem aprovadas.

A verificação WPF utilizou o `UiBatchBuffer` de produção, `Progress<T>`, dispatcher
e uma grade vinculada à coleção, consumindo a consulta real sobre a cópia somente
leitura. A primeira linha foi aplicada enquanto a consulta ainda estava em
andamento; as 50 linhas finais coincidiram integralmente com a página retornada.
O produtor foi pausado deliberadamente após a primeira entrega para provar que
a interface não depende do término da consulta. Por isso, o tempo desse teste
não foi incluído no benchmark. Resultado: `ui-progress.json`, no mesmo diretório
de validação. Ele verifica esse caminho da interface, sem iniciar o aplicativo
com o banco real do usuário.

## Publicação

Publicado exclusivamente em `artifacts/win-x64/PNCPKing.exe`, em 14/09/2026 às
20:20 (America/Sao_Paulo). A distribuição contém apenas o executável, sem PDBs
ou banco. Foi verificada a existência de exatamente um `PNCPKing.exe` no
repositório, após remover cópias geradas pela compilação. Tamanho: 220.955.130
bytes. Hash, validações e verificação final da cópia do banco estão em
`artifacts/query-stream-validation/publication.json`.
