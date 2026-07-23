# PNCP King

Aplicativo desktop Windows para manter um índice local dos últimos 365 dias do PNCP e pesquisar preços homologados por item, sem baixar documentos ou PDFs.

## Executável pronto

A distribuição autocontida mais recente para Windows x64 está em `artifacts\win-x64\PNCPKing.exe`. Ela inclui o runtime do .NET 8 e, portanto, não exige instalação separada do .NET. O executável tem aproximadamente 166 MiB; o banco nacional será criado somente na pasta escolhida pelo usuário.

## O que está implementado

- prévia obrigatória com quantidade exata, estimativa de rede/banco/cache, tempo e espaço livre;
- recálculo da prévia e confirmação explícita imediatamente antes da carga nacional;
- sincronização por modalidade e período, com checkpoint estruturado, pausa, cancelamento e retomada;
- manutenção automática enquanto o aplicativo está aberto, preenchendo primeiro os dias novos e as lacunas do último ano;
- barra de cobertura com 365 segmentos, do dia mais antigo ao mais recente, e estados ausente, parcial, baixando, completo e falha;
- remoção de contratações vencidas somente depois que a nova borda da janela estiver comprovadamente completa;
- atualização por `dataAtualizacaoGlobal` com sobreposição de 48 horas;
- SQLite em WAL com FTS5 e pesquisa sem diferença entre acentos/maiúsculas, sempre por prefixo;
- sintaxe textual com E implícito ou `+`, OU por `OU`, `OR` ou `|`, frases entre aspas fechadas, exclusões globais por `-palavra` ou `-"frase"` e unidades aceitas por marcadores como `"pacote "unidade`;
- pesquisa em duas etapas: o objeto local seleciona candidatos por qualquer termo positivo e somente a descrição do item que satisfaz a expressão completa produz preços;
- filtros `Todos`, `Cidades Próximas`, `Sudeste` e UF, períodos de 7 a 365 dias ou personalizados e ordenação por relevância, data ou proximidade;
- catálogo nacional embutido das localidades oficiais de 2022 do IBGE, usado somente para distância e ordem geográfica, sem consultas remotas por município;
- percurso fixo de candidatos: Ribeirão Preto e os outros 49 municípios mais próximos por distância, restante de SP em amostra aleatória estável e depois cada UF pela proximidade de sua sede municipal mais próxima;
- sorteio estável durante cada pesquisa, paginação por cursor sem repetição e nova rotação aleatória ao iniciar outra pesquisa;
- pesquisa contínua pelo orçamento escolhido de 1 a 100 disparos, com até 50 consultas de resultado por disparo, sem paginação manual nem limite intermediário de listas;
- resultados acrescentados progressivamente em uma única grade virtualizada, com barra vertical, percentual, etapa geográfica, contratos e falhas atualizados durante a execução;
- biblioteca opcional Sweet Code, persistida no backup, com um crivo por linha e autocomplete por prefixo usando setas e `TAB`;
- banco temporário separado para os preços automáticos, apagado ao pesquisar novamente, fechar ou reabrir após encerramento inesperado;
- cache permanente separado para a atualização manual de uma contratação;
- faixa inclusiva de preço unitário homologado, aplicada somente a resultados ativos e sem conversão entre unidades;
- projetos persistentes de cotação que copiam a amostra já coletada, respeitando a faixa informada e sem novas chamadas ao PNCP;
- qualificação auditável por cobertura do descritivo solicitado, unidade/embalagem, quantidade em faixas graduais, proximidade e atualidade;
- elegibilidade de cotação determinada somente pela faixa de preço e compatibilidade descritiva; CNPJ, unidade, quantidade, proximidade, atualidade e índice permanecem informativos;
- formação local de cestas com três referências únicas, mantendo origem e dispersão visíveis para a decisão objetiva do usuário;
- classificação da cesta recomendada, mais barata e mais cara, com revisão e confirmação obrigatória pelo usuário;
- atualização incremental da amostra com versionamento e reconfirmação da escolha anterior;
- importação de cotações por `.xlsx` nas colunas A:G, fila sequencial retomável e escolha automática da cesta recomendada;
- gerenciamento para criar, renomear e excluir cotações ou itens, além de cancelar e retomar automações;
- exportação simplificada em uma única aba `.xlsx`, com empresa, CNPJ, `INCISO II`, valor e observação quando faltarem três preços;
- medição por sessão de chamadas, bytes, duração e médias de listas de itens e resultados;
- agendador único com no máximo duas chamadas ao PNCP e prioridade para ações visíveis do usuário;
- distinção entre preço encontrado, resultado cancelado, item sem resultado, pendência e falha;
- invalidação de listas e preços permanentes quando a contratação muda;
- link para a página oficial da contratação, sem baixar PDFs ou metadados documentais;
- backup/importação validado no formato `.pncpking`, incluindo migração segura de backups antigos do próprio PNCP King.

O aplicativo não fez a carga nacional durante a compilação. A medição atual será feita pela própria interface e nenhum download começará sem a confirmação dos números e da margem adicional de 20% de espaço livre.

## Requisitos para desenvolvimento

- .NET SDK 8;
- Windows 10/11 para executar a interface WPF;
- conexão com a internet para sincronização e consulta de preços.

## Compilar e testar

```powershell
dotnet restore PNCPKing.sln
dotnet build PNCPKing.sln --configuration Release
dotnet test tests\PNCPKing.Tests\PNCPKing.Tests.csproj --configuration Release
```

Para gerar uma distribuição Windows autocontida:

```powershell
.\scripts\publish-windows.ps1
```

O executável será criado em `artifacts\win-x64`.

A suíte automatizada cobre pesquisa por objeto e item, geografia, faixa de preço, valores homologados, rejeição de valores estimados, múltiplos resultados, falha parcial, `429`, timeout, cobertura diária, retomada por checkpoint e validação de backups.

## Uso

1. Na primeira abertura, escolha a pasta que armazenará o banco.
2. Clique em **Calcular tamanho** e aguarde a contagem das modalidades.
3. Revise o volume, o espaço e a duração estimados.
4. Clique em **Baixar/atualizar dados** e confirme os números exibidos.
5. Digite o objeto, escolha geografia, período e ordenação e clique em **Pesquisar**.
6. Você pode combinar termos: `café filtro` ou `café + filtro` exigem ambos; `café OU chá` aceita qualquer um; `"café torrado"` busca a frase; `café -cafeteira -"filtro de papel"` exclui descrições; `"pacote "unidade` aceita qualquer uma dessas unidades estruturadas do item.
7. Informe de 1 a 100 disparos e pesquise. A execução avançará automaticamente pela sequência 50 municípios próximos, restante de SP e demais UFs, enquanto a grade e o progresso crescem sem páginas manuais.
8. Para ampliar a sessão atual sem apagar o que já apareceu, use **Buscar mais com estes disparos**. Use **Parar preços** para interromper preservando os resultados concluídos.
9. Use os campos de preço mínimo/máximo para filtrar o valor unitário homologado ativo.
10. Para iniciar uma cotação, clique em **Usar esta amostra em uma cotação**, selecione ou crie um projeto e informe quantidade, unidade e faixa de preço opcional.
11. Na aba **Cotações**, compare todas as cestas válidas, examine a composição do índice e confirme a cesta escolhida.
12. Faça novas pesquisas e adicione outros itens ao mesmo projeto. Se ampliar a coleta de um item, use **Atualizar amostra com a pesquisa atual**; a escolha anterior ficará marcada para reconfirmação.
13. Use **Importar XLSX** para carregar vários itens pelas colunas A:G, escolher pesos globais e executar a cotação automaticamente; falhas podem ser retomadas. **Exportar Excel** gera a versão simples com `INCISO II`.
14. Para manter uma contratação no cache permanente, selecione-a na segunda aba e use **Buscar/atualizar todos os preços**.
15. Use **Abrir contratação no PNCP** para acessar a página oficial e baixar documentos manualmente, se houver interesse.

O total homologado geral mostrado na grade de contratações é apenas um resumo. Os preços dos itens vêm exclusivamente dos campos de resultado homologado do PNCP; valores estimados nunca são usados como substitutos.

Após a primeira carga autorizada, o programa verifica periodicamente se o calendário avançou ou se há lacunas. Ele baixa primeiro as publicações ausentes, faz a atualização global com sobreposição de 48 horas e só então ajusta a borda antiga da janela de 365 dias. Uma falha nunca antecipa a exclusão de registros.

O estudo de custo para uma eventual carga nacional de itens e resultados está em `docs/price-load-study.md`. Por exigir milhões de chamadas adicionais, essa carga permanece sob demanda nesta versão.

O Sweet Code pode ser aberto ao lado da pesquisa. Cole um crivo por linha, ative as sugestões e use ↑/↓ e `TAB` para preencher sem impedir a digitação livre.
