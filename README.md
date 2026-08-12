# PNCP King

Aplicativo desktop Windows para manter um índice local dos últimos 365 dias do PNCP, pesquisar preços homologados por item e reunir as evidências documentais das cotações.

## Executável pronto

A distribuição autocontida mais recente para Windows x64 está em `artifacts\win-x64\PNCPKing.exe`. Ela inclui o runtime do .NET 8, o mecanismo OCR local e o modelo oficial de português, portanto não exige instalação separada. O banco nacional será criado somente na pasta escolhida pelo usuário.

## O que está implementado

- prévia obrigatória com quantidade exata, estimativa de rede/banco/cache, tempo e espaço livre;
- recálculo da prévia e confirmação explícita imediatamente antes da carga nacional;
- sincronização por modalidade e período, com checkpoint estruturado, pausa, cancelamento e retomada;
- manutenção automática enquanto o aplicativo está aberto, preenchendo primeiro os dias novos e as lacunas do último ano;
- barra de cobertura com 365 segmentos, do dia mais antigo ao mais recente, e estados ausente, parcial, baixando, completo e falha;
- remoção de contratações vencidas somente depois que a nova borda da janela estiver comprovadamente completa;
- atualização por `dataAtualizacaoGlobal` com sobreposição de 48 horas;
- SQLite em WAL com FTS5 e pesquisa sem diferença entre acentos/maiúsculas, sempre por prefixo;
- sintaxe textual com E implícito ou `+`, OU por `OU`, `OR` ou `|`, frases entre aspas fechadas, exclusões globais por `-palavra` ou `-"frase"`, unidades aceitas por marcadores como `"pacote "unidade` e até dez títulos prioritários de contratações em `C:(material escolar, materiais de expediente)`;
- pesquisa local primeiro: itens e preços permanentes atuais são paginados e entregues antes da ampliação pela API, com contagens separadas e eliminação de duplicidades;
- filtros `Todos`, `Cidades Próximas`, `Sudeste` e UF, períodos de 7 a 365 dias ou personalizados e ordenação por relevância, data ou proximidade;
- catálogo nacional embutido das localidades oficiais de 2022 do IBGE, usado somente para distância e ordem geográfica, sem consultas remotas por município;
- percurso fixo de candidatos: Ribeirão Preto e os outros 49 municípios mais próximos por distância, restante de SP em amostra aleatória estável e depois cada UF pela proximidade de sua sede municipal mais próxima;
- sorteio estável durante cada pesquisa, paginação por cursor sem repetição e nova rotação aleatória ao iniciar outra pesquisa;
- três lotes automáticos de 50 contratações e pesquisa contínua de 1 a 100 lotes adicionais; cada lote examina 50 contratações e consulta todos os itens compatíveis encontrados nelas;
- resultados acrescentados progressivamente em uma única grade virtualizada, com percentual, contratações solicitadas/processadas, itens compatíveis, preços revelados e chamadas reais de listas/resultados;
- grade de preços inicialmente enxuta com as nove colunas principais e layouts de visibilidade, ordem e largura persistidos por grade; o seletor permite restaurar o padrão;
- biblioteca opcional Sweet Code, persistida no backup, com um crivo por linha e autocomplete por prefixo usando setas e `TAB`;
- banco temporário separado para os preços automáticos, apagado ao pesquisar novamente, fechar ou reabrir após encerramento inesperado;
- cache permanente opcional e móvel das listas de itens e resultados homologados dos 90 dias mais recentes, autorizado somente após estimativa de espaço/tempo, com checkpoint por contratação, pausa, retomada, poda seletiva e reserva mínima de disco;
- faixa inclusiva de preço unitário homologado, aplicada somente a resultados ativos e sem conversão entre unidades;
- projetos persistentes de cotação que copiam a amostra já coletada, respeitando a faixa informada e sem novas chamadas ao PNCP;
- qualificação auditável por cobertura do descritivo solicitado, unidade/embalagem, quantidade em faixas graduais, proximidade e atualidade;
- elegibilidade de cotação determinada somente pela faixa de preço e compatibilidade descritiva; CNPJ, unidade, quantidade, proximidade, atualidade e índice permanecem informativos;
- formação local de cestas automáticas com alvo configurável de 3 a 10 preços, redução até 2 quando necessário e até 100 opções curadas e determinísticas;
- criação de várias cestas manuais persistentes por seleção múltipla dos preços na grade, com renomeação, exclusão e remoção individual de referências;
- classificação da cesta recomendada, mais barata e mais cara, com situação textual e fundos suaves verde/vermelho para automáticas e azul/roxo para manuais;
- atualização incremental da amostra com versionamento e reconfirmação da escolha anterior;
- importação de cotações por `.xlsx` compatível com A:G e alvo opcional em H (`Número de preços na cesta`), fila sequencial retomável e escolha automática da cesta recomendada;
- gerenciamento para criar, renomear e excluir cotações ou itens, além de cancelar e retomar automações;
- exportação em uma única aba `.xlsx` baseada na planilha oficial de avaliação de preços, preservando cabeçalho e imagem da prefeitura, repetindo um bloco formatado por item e calculando por fórmulas os preços excessivos, inexequíveis e a média dos preços válidos;
- acesso aos anexos do PNCP pela grade de preços, pelo cache da contratação e pelas referências das cestas/auditoria, com extração segura de PDF, ZIP, 7z e RAR, deduplicação e consolidação sob demanda em `Downloads`;
- cache documental separado de até 2 GiB, com manifestos atômicos, remoção LRU e comando próprio de limpeza que não altera o banco nem os arquivos exportados;
- geração automática de `{planilha}_evidencias.pdf` ao exportar uma cotação, cobrindo todas as referências efetivamente exportadas e registrando ocorrências, ausências e falhas parciais;
- pacote portátil `.pncpcotacao` para transferir uma cotação sem substituir o banco inteiro, preservando itens, preços PNCP/web, cestas, escolhas confirmadas, pesquisas e retomadas, rascunhos e prints com validação SHA-256;
- extração de texto e coordenadas diretamente do PDF sempre que a camada nativa for utilizável; OCR português local é acionado somente nas páginas escaneadas, sem enviar imagens para serviços externos;
- medição por sessão de chamadas, bytes, duração e médias de listas de itens e resultados;
- agendador único com no máximo duas chamadas ao PNCP, prioridade para ações visíveis e índice, e apenas uma chamada de cache nacional quando não houver trabalho prioritário ativo ou enfileirado;
- distinção entre preço encontrado, resultado cancelado, item sem resultado, pendência e falha;
- invalidação de listas e preços permanentes quando a contratação muda;
- link para a página oficial da contratação e comandos explícitos para obter seus documentos quando solicitado;
- backup/importação validado no formato `.pncpking`, com perfil compacto (sem cache reconstruível) ou completo, prévia de espaço, progresso por etapa, execução sem bloquear a interface e migração segura de backups antigos do próprio PNCP King;
- logs de diagnóstico por execução em `%LOCALAPPDATA%\PNCP King\logs`, acessíveis pelo botão **Logs de diagnóstico**, incluindo abertura, pasta do banco, fases da importação e exceções completas.

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
5. Opcionalmente, no painel **Itens e preços homologados — últimos 90 dias**, use **Estimar e ativar**. Revise a projeção — atualmente da ordem de vários GiB — e autorize somente se quiser manter essa janela local; pesquisas e índice sempre interrompem novas chamadas dessa carga de fundo.
6. Digite o objeto, escolha geografia, período e ordenação e clique em **Pesquisar**.
7. Você pode combinar termos: `café filtro` ou `café + filtro` exigem ambos; `café OU chá` aceita qualquer um; `"café torrado"` busca a frase; `café -cafeteira -"filtro de papel"` exclui descrições; `"pacote "unidade` aceita qualquer uma dessas unidades estruturadas do item. Acrescente `C:(alimentação escolar, gêneros alimentícios)` para examinar primeiro contratações cujos títulos correspondam a esses crivos; o bloco `C:` seleciona contratos, mas não substitui os termos que identificam o item.
8. Ao clicar em **Pesquisar**, confira o resumo local exibido na própria tela. O aplicativo examinará automaticamente os três lotes visíveis — até 150 contratações candidatas — e revelará todos os itens compatíveis encontrados.
9. Para ampliar a sessão atual sem repetir contratações, informe de 1 a 100 lotes e use **Mostrar valores das próximas contratações**. Cada lote contém 50 contratações. Use **Parar preços** para interromper preservando os resultados concluídos.
10. Use os campos de preço mínimo/máximo para filtrar o valor unitário homologado ativo.
11. Para iniciar uma cotação, clique em **Usar esta amostra em uma cotação**, selecione ou crie um projeto e informe quantidade, unidade, alvo automático de 3 a 10 preços e faixa opcional.
12. Para montar sua própria composição, selecione uma ou mais linhas homologadas com `Ctrl`/`Shift` e use **Criar/adicionar à cesta manual**. Na aba **Cotações**, você pode ampliar, renomear, revisar, confirmar ou excluir essas cestas.
13. Faça novas pesquisas e adicione outros itens ao mesmo projeto. Se ampliar a coleta de um item, use **Atualizar amostra com a pesquisa atual**; a escolha anterior ficará marcada para reconfirmação.
14. Use **Importar XLSX** para carregar vários itens pelas colunas A:G e, opcionalmente, o alvo da cesta em H. H vazia usa 3. A automação interpreta a coluna G como lotes de 50 contratações; falhas podem ser retomadas. **Exportar Excel** preenche o modelo de avaliação com a cesta atual, links PNCP/site e fórmulas ajustadas ao número real de preços; o PDF de evidências é salvo na mesma pasta.
15. Na aba **Cotações**, use **Exportar pacote** para criar um `.pncpcotacao` portátil com a cotação selecionada e seus prints. **Importar pacote** mostra uma prévia e, se o mesmo identificador já existir, permite importar como cópia, substituir com recuperação automática ou cancelar.
16. Para manter uma contratação no cache permanente, selecione-a na segunda aba e use **Buscar/atualizar todos os preços**.
17. Use **Abrir contratação no PNCP** para acessar a página oficial. Use **Acessar documentos** para baixar, extrair e consolidar os PDFs; o arquivo será salvo em `Downloads` e somente será aberto se você escolher **Abrir PDF** ao final.
18. Use **Escolher colunas** para ajustar cada grade uma vez. Visibilidade, ordem e largura são restauradas nos usos seguintes; **Restaurar padrão** volta ao layout original.
19. Se ocorrer uma falha de abertura ou importação, use **Logs de diagnóstico**, copie o arquivo `.log` mais recente e envie-o para análise. Mesmo quando a janela principal não abre, a mensagem de erro informa o caminho exato do log.

O total homologado geral mostrado na grade de contratações é apenas um resumo. Os preços dos itens vêm exclusivamente dos campos de resultado homologado do PNCP; valores estimados nunca são usados como substitutos.

Após a primeira carga autorizada, o programa verifica periodicamente se o calendário avançou ou se há lacunas. Ele baixa primeiro as publicações ausentes, faz a atualização global com sobreposição de 48 horas e só então ajusta a borda antiga da janela de 365 dias. Uma falha nunca antecipa a exclusão de registros.

O estudo de custo da carga nacional de itens e resultados está em `docs/price-load-study.md`. Por exigir milhões de chamadas adicionais, ela permanece opcional, limitada à janela móvel de 90 dias e nunca começa antes da estimativa e autorização explícita.

O Sweet Code pode ser aberto ao lado da pesquisa. Cole um crivo por linha, ative as sugestões e use ↑/↓ e `TAB` para preencher sem impedir a digitação livre.
