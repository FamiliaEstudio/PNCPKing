# PNCP King

Aplicativo desktop Windows para manter um índice local dos últimos 11 meses do PNCP, pesquisar preços homologados por item e reunir as evidências documentais das cotações.

## Executável pronto

A distribuição autocontida mais recente para Windows x64 está em `artifacts\win-x64\PNCPKing.exe`. Ela inclui o runtime do .NET 8, o mecanismo OCR local e o modelo oficial de português, portanto não exige instalação separada. O banco nacional será criado somente na pasta escolhida pelo usuário.

## O que está implementado

- prévia obrigatória com quantidade exata, estimativa de rede/banco/cache, tempo e espaço livre;
- recálculo da prévia e confirmação explícita imediatamente antes da carga nacional;
- sincronização por modalidade e período, com checkpoint estruturado, pausa, cancelamento e retomada;
- atualização exclusivamente manual: **Atualizar** preenche dias novos e lacunas, verifica alterações e conclui as etapas contratações → listas de itens → preços;
- barra de cobertura com um segmento por dia da janela de 11 meses de calendário, do dia mais antigo ao mais recente, e estados ausente, parcial, baixando, completo e falha;
- remoção local de contratações vencidas na abertura e na mudança de dia, independentemente da conexão com o PNCP;
- atualização por `dataAtualizacaoGlobal` desde a última verificação concluída, com sobreposição de dois dias;
- SQLite em WAL com FTS5 e pesquisa sem diferença entre acentos/maiúsculas, sempre por prefixo;
- sintaxe textual com E implícito ou `+`, OU por `OU`, `OR` ou `|`, frases entre aspas fechadas, exclusões globais por `-palavra` ou `-"frase"` e unidades aceitas por marcadores como `"pacote "unidade`; expressões antigas com `C:(...)` continuam aceitas, mas o bloco não é mais gerado nem recomendado para novas pesquisas;
- pesquisa local primeiro: páginas de até 50 preços homologados atuais na ordem de descoberta, com eliminação de duplicidades e continuação por cursor; a tabela ordena somente os resultados carregados quando o usuário clica em uma coluna;
- pesquisa local e progressiva, sem downloads ao pesquisar, abrir ou importar bancos; **Atualizar**, **Ampliar pela API** e outras ações explícitas continuam disponíveis;
- filtros `Todos`, `Cidades Próximas`, `Sudeste` e UF, períodos de 7, 30, 90 e 180 dias, 10 e 11 meses, ou personalizados dentro dessa janela; o painel de contratações conserva a ordenação por relevância, data ou proximidade;
- catálogo nacional embutido das localidades oficiais de 2022 do IBGE, usado somente para distância e ordem geográfica, sem consultas remotas por município;
- percurso fixo de candidatos: Ribeirão Preto e os outros 49 municípios mais próximos por distância, restante de SP em amostra aleatória estável e depois cada UF pela proximidade de sua sede municipal mais próxima;
- sorteio estável durante cada pesquisa, paginação por cursor sem repetição e nova rotação aleatória ao iniciar outra pesquisa;
- ampliação explícita com seleção de 1 a 200 lotes; cada lote cobre até 50 contratações ainda não resolvidas e avança gratuitamente pelas já completas no cache, processadas automaticamente em parcelas locais de até 20 lotes (1.000 contratações);
- resultados acrescentados progressivamente em uma única grade virtualizada, com percentual, contratações solicitadas/processadas, itens compatíveis, preços revelados e chamadas reais de listas/resultados;
- grade de preços inicialmente enxuta com as nove colunas principais e layouts de visibilidade, ordem e largura persistidos por grade; o seletor permite restaurar o padrão;
- biblioteca opcional Sweet Code, persistida no backup, com um crivo por linha e autocomplete por prefixo usando setas e `TAB`;
- sessão retomável separada para a última pesquisa geral, com cursor, resultados e falhas preservados ao fechar; a automação continua usando armazenamento temporário isolado;
- listas de itens dos últimos 11 meses, com checkpoint por contratação, pausa, retomada e reserva de disco, como segunda etapa de **Atualizar**;
- resultados homologados consultados somente para itens compatíveis com a pesquisa e preservados no banco principal para reutilização até a contratação receber uma atualização global ou sair da janela de 11 meses;
- preços dos últimos 11 meses como terceira etapa de **Atualizar**, com concorrência agressiva adaptativa; ele consulta somente itens com `temResultado=true`, conserva todos os resultados `Informado` com valor unitário homologado positivo e conclui sem repetir respostas vazias, canceladas, sem valor útil ou `404`;
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
- geração opcional de `{planilha}_evidencias.pdf` ao exportar uma cotação, cobrindo todas as referências efetivamente exportadas sem realçar os crivos internos nas páginas, além da opção de exportar somente o Excel;
- pacote portátil `.pncpcotacao` para transferir uma cotação sem substituir o banco inteiro, preservando itens, preços PNCP/web, cestas, escolhas confirmadas, pesquisas e retomadas, rascunhos e prints com validação SHA-256;
- extração de texto e coordenadas diretamente do PDF sempre que a camada nativa for utilizável; OCR português local é acionado somente nas páginas escaneadas, sem enviar imagens para serviços externos;
- medição por sessão de chamadas, bytes, duração e médias de listas de itens e resultados;
- agendador único com concorrência adaptativa; no ciclo agressivo, `429`, timeouts e falhas temporárias reduzem a concorrência por rajada, sem cair diretamente para uma chamada após um erro isolado. Sem prazo informado pelo PNCP, novas tentativas começam em 250–349 ms e aumentam até 2–2,1 s em falhas consecutivas; a recuperação sobe um nível após oito sucessos. O ciclo de **Atualizar** repete falhas temporárias automaticamente até concluir ou ser pausado/cancelado, preservando os limites do perfil escolhido e os prazos `Retry-After` enviados pelo servidor;
- distinção entre preço encontrado, resultado cancelado, item sem resultado, pendência e falha;
- invalidação de listas e preços permanentes quando a contratação muda;
- pesquisa principal por descoberta em todos os perfis: leitura de 250 identificadores de itens por lote interno, validação dos filtros e carregamento dos preços necessários à página; expressões sem termos indexáveis percorrem os itens em lotes limitados; não há ordenação global antecipada dos preços;
- `PRAGMA optimize` marcado após atualizações concluídas e executado somente em ociosidade, com registro de sucesso para evitar manutenção concorrente com a primeira página;
- link para a página oficial da contratação e comandos explícitos para obter seus documentos quando solicitado;
- backup/importação validado no formato `.pncpking`, com **Completo — recomendado** como perfil padrão (contratações, itens, preços, snapshots e checkpoints), prévia detalhada de espaço, progresso cancelável por etapa, ativação atômica e migração segura de backups antigos do próprio PNCP King;
- logs de diagnóstico por execução em `%LOCALAPPDATA%\PNCP King\logs`, acessíveis pelo botão **Logs de diagnóstico**, incluindo abertura, pasta do banco, fases da importação e exceções completas.
- escolha em **Opções → Uso de recursos** entre Automático (padrão), Restrito, Médio e Amplo, salva neste PC e aplicada ao reabrir o programa. O perfil define os padrões de cache e processamento do banco e limita as consultas simultâneas ao PNCP; pouca memória e respostas da API podem reduzir o uso efetivo. Trocar o perfil remove a calibração anterior;
- avaliação opcional em **Opções → Diagnóstico → Avaliar desempenho deste PC**, com limite de cinco minutos, leitura sem alterar o banco e comparação de caches de 32, 64 e 96 MiB usando a pesquisa por descoberta; recomendações anteriores à versão 3 da calibração são invalidadas; recomendações comprovadas valem para o perfil avaliado e podem ser aplicadas na próxima abertura, com opção de restaurar o padrão desse perfil.

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

Somente `artifacts\win-x64\PNCPKing.exe` será publicado. O projeto PNCP Guard também é compilado em Release pela solução, mas não integra essa distribuição.

A suíte automatizada cobre pesquisa por objeto e item, geografia, faixa de preço, valores homologados, rejeição de valores estimados, múltiplos resultados, falha parcial, `429`, timeout, cobertura diária, retomada por checkpoint e validação de backups.

## Backup e importação de bancos grandes

Em **Opções → Arquivo → Exportar backup**, o perfil **Completo com cotações** preserva o banco integral, inclusive relações de itens, todos os resultados homologados, snapshots de atualização, índices nacionais, checkpoints, cotações, cestas, CATMAT/CATSER, Sweet Codes e referências de evidências web. **Completo sem cotações** mantém o histórico e o cache de preços, mas remove projetos, itens, cestas, referências e evidências das cotações somente da cópia exportada; o banco de origem permanece intacto. O perfil **Compacto** preserva os dados permanentes, inclusive cotações, mas remove do snapshot descartável os itens, preços, FTS e checkpoints de cache que podem ser reconstruídos.

Antes da exportação, a prévia mostra perfil, contagens materializadas, tamanho descompactado, evidências, unidades usadas e espaço necessário. O perfil completo precisa de espaço para o snapshot e para o arquivo `.partial`; o compacto e o completo sem cotações também reservam espaço adicional para o `VACUUM`. A exportação cria um único `.pncpking` com ZIP64, calcula SHA-256 durante a própria compactação e executa uma única verificação integral `PRAGMA integrity_check` no snapshot. Destinos FAT32 são recusados quando o arquivo pode ultrapassar 4 GiB; use NTFS ou exFAT.

Na importação, o banco é extraído em staging ao lado do banco ativo, portanto a troca final ocorre por renomeação na mesma unidade. Backups atuais que já foram integralmente validados na origem passam por SHA-256 e conferência estrutural, sem repetir a longa varredura integral no HDD de destino. O receptor nunca executa `PRAGMA integrity_check`, inclusive após migração de esquema; a migração é transacional e, ao final, confirma-se apenas a versão interna. Backups legados sem prova de validação integral na origem são recusados e devem ser reexportados por uma versão atual do PNCP King.

No instante da ativação, o banco atual é renomeado para `nome-do-banco.db.before-import-...bak`, o banco validado assume o caminho oficial e é aberto para confirmação. Se essa etapa falhar, a base anterior é restaurada automaticamente. O cancelamento fica disponível até o começo dessa curta troca. Depois de confirmar que não precisa mais da recuperação, use **Opções → Limpeza → Excluir bases anteriores de importação**; a ação mostra quantidade e tamanho e não alcança arquivos fora do padrão de recuperação do banco atual.

O backup **Completo sem cotações** serve para iniciar um banco em outro PC e exige a versão 1.2.11 ou posterior. Sua importação é recusada quando o banco de destino já contém cotações, para não substituir trabalhos locais. Em PCs em uso, instale a release do programa e importe atualizações PNCP; essas operações preservam as cotações locais.

## Transferência de atualizações entre PCs

O botão **Atualizar pelo GitHub**, ao lado de **Opções**, verifica novas versões do programa e o pacote móvel de preços publicado em `FamiliaEstudio/PNCPKing`. Ele apresenta uma prévia e reinicia somente quando instala um executável novo. Preços podem ser atualizados independentemente do programa. Preparação das releases, requisitos e retomada: [atualizações pelo GitHub](docs/github-updates.md).

**Exportar backup** mantém a opção de banco completo `.pncpking`. A transferência `.pncpupdate` é adicional e incorpora apenas dados oficiais, preservando cotações, cestas, evidências particulares e configurações do destino, observada a retenção de 11 meses.

1. Execute **Atualizar** no PC de origem até onde for possível. A exportação não consulta o PNCP e aceita dias ainda incompletos; leva somente listas e resultados comprovadamente concluídos, além dos checkpoints de páginas diárias já gravadas.
2. Use **Opções → Arquivo → Exportar atualizações PNCP**. O arquivo contém hoje e os nove dias anteriores, além de contratações antigas oficialmente alteradas nesse período.
3. No outro PC, inclusive depois de restaurar qualquer backup compatível, use **Importar atualizações PNCP** e depois **Atualizar** para continuar as pendências. Não é necessária uma base inicial nem uma linhagem comum.
4. Uma importação cancelada conserva os blocos diários já concluídos. Reimporte o arquivo para continuar; blocos idênticos são ignorados sem extração.
5. Registros ausentes são inseridos. Somente versões oficiais comprovadamente mais novas substituem as locais; versões iguais, ausentes, mais novas no destino ou sem ordem segura são preservadas. A importação não inicia revalidação no PNCP.

CATMAT/CATSER não faz parte do `.pncpupdate` e continua sendo atualizado separadamente. Detalhes técnicos e medições: [validação da atualização manual](docs/manual-update-validation.md).

## Uso

1. Na primeira abertura, escolha a pasta que armazenará o banco.
2. Clique em **Calcular tamanho** e aguarde a contagem das modalidades.
3. Revise o volume, o espaço e a duração estimados.
4. Clique em **Atualizar** e confirme a prévia unificada. O ciclo executa contratações, listas de itens e preços nessa ordem.
5. Use **Pausar/Continuar** e **Cancelar** para controlar o ciclo. Falhas e cancelamento preservam checkpoints; a retomada após fechar exige outro clique em **Atualizar**.
6. CATMAT/CATSER é atualizado separadamente pelo comando manual do catálogo.
7. Digite o objeto, escolha geografia e período e clique em **Pesquisar**. Os preços aparecem na ordem de descoberta; clique nos cabeçalhos para ordenar os resultados carregados por publicação, valor ou outra coluna. Novos preços seguem a ordenação escolhida. Os primeiros encontrados não são necessariamente os mais recentes do banco.
8. Você pode combinar termos: `café filtro` ou `café + filtro` exigem ambos; `café OU chá` aceita qualquer um; `"café torrado"` busca a frase; `café -cafeteira -"filtro de papel"` exclui descrições; `"pacote "unidade` aceita qualquer uma dessas unidades estruturadas do item. O parser continua aceitando `C:(...)` em expressões antigas, mas novas pesquisas e sugestões da IA não precisam nem recebem esse bloco.
9. **Pesquisar** entrega os preços locais progressivamente e procura um 51º preço válido para confirmar se há outra página. Essa confirmação pode continuar após os 50 aparecerem. A lista de contratações é consultada somente quando seu painel for aberto; o seletor de ordenação nesse painel afeta apenas essa lista. Use **Atualizar** ou a ampliação explícita quando precisar consultar o PNCP.
10. Para alcançar itens ausentes, falhas ou contratações ainda não indexadas, informe de 1 a 200 lotes e use **Ampliar pela API**. A escolha é aditiva e corresponde a até 50 novas contratações por lote; as já resolvidas no cache não consomem essa cota e são percorridas automaticamente em parcelas de até 1.000 para reduzir a pressão sobre discos mecânicos. Todos os itens que ainda exigirem rede usam a concorrência adaptativa do PNCP. Use **Carregar mais resultados** para exibir páginas de até 50 preços salvos, **Reiniciar pesquisa** para criar nova rotação e **Parar preços** para interromper preservando o checkpoint. O limite de 200 pertence à ação, não à capacidade em bytes ou registros do cache persistente; **Esgotar pela API** percorre todo o conjunto confirmado somente quando a ampliação explícita é iniciada.
11. Use os campos de preço mínimo/máximo para filtrar o valor unitário homologado ativo.
12. Para iniciar uma cotação, clique em **Usar esta amostra em uma cotação**, selecione ou crie um projeto e informe quantidade, unidade, alvo automático de 3 a 10 preços e faixa opcional.
13. Para montar sua composição, clique e arraste pelas linhas e use **Marcar selecionados para cesta**, depois **Criar/adicionar à cesta**. Um clique seleciona um preço; segurar parado por um segundo alterna sua marcação para cesta; triplo clique fixa; clique direito remove fixação e marcação. Os botões de ações coletivas atuam sobre todas as linhas selecionadas. Na aba **Cotações**, você pode ampliar, renomear, revisar, confirmar ou excluir essas cestas.

Os preços ocupam uma linha compacta. Dê duplo clique para ler o descritivo no painel inferior; **Dados da linha** permite selecionar e copiar outros campos. `Ctrl+C` na grade copia as linhas selecionadas com as colunas visíveis; no painel copia somente o trecho selecionado, para colar com `Ctrl+V` em outro aplicativo. Nas tabelas de itens e cestas, use **Ler/copiar dados**. `Ctrl+F` procura nos descritivos completos da lista já carregada, sem consultar o banco ou carregar mais páginas; `Enter`/`F3` avança e `Shift+Enter`/`Shift+F3` retrocede. `Esc` fecha a busca ou a leitura. Nas grades de preços de Cotações, os documentos continuam disponíveis pelo menu de ações.
14. Faça novas pesquisas e adicione outros itens ao mesmo projeto. Se ampliar a coleta de um item, use **Atualizar amostra com a pesquisa atual**; a escolha anterior ficará marcada para reconfirmação.
15. Use **Importar XLSX** para carregar vários itens pelas colunas A:G e, opcionalmente, o alvo da cesta em H. H vazia usa 3. A automação interpreta a coluna G como lotes de 50 contratações; falhas podem ser retomadas. **Exportar Excel e evidências** preenche o modelo de avaliação e salva o PDF na mesma pasta; **Exportar somente Excel** não gera documentos de evidência.

Para cotar medicamentos, marque **Cotações → Cotação de medicamentos — 4 casas decimais**. A opção fica salva por projeto e aplica quatro casas aos preços efetivos, às cestas e ao Excel, incluindo conversões, médias e medianas. Casas excedentes são truncadas. Ao alterar a opção, reconfirme as cestas escolhidas. As demais cotações continuam com duas casas nos valores efetivos; os preços PNCP originais permanecem preservados.
16. Na aba **Cotações**, use **Exportar pacote** para criar um `.pncpcotacao` portátil com a cotação selecionada e seus prints. **Importar pacote** mostra uma prévia e, se o mesmo identificador já existir, permite importar como cópia, substituir com recuperação automática ou cancelar.
17. Para fixar uma contratação no cache enquanto estiver na janela de 11 meses, selecione-a na segunda aba e use **Buscar/atualizar todos os preços**.
18. Use **Abrir contratação no PNCP** para acessar a página oficial. Use **Acessar documentos** para baixar, extrair e consolidar os PDFs; o arquivo será salvo em `Downloads` e somente será aberto se você escolher **Abrir PDF** ao final.
19. Use **Escolher colunas** para ajustar cada grade uma vez. Visibilidade, ordem e largura são restauradas nos usos seguintes; **Restaurar padrão** volta ao layout original.
20. Se ocorrer uma falha de abertura ou importação, use **Logs de diagnóstico**, copie o arquivo `.log` mais recente e envie-o para análise. Mesmo quando a janela principal não abre, a mensagem de erro informa o caminho exato do log.

## PNCP Guard

Em **Opções → PNCP Guard**, escolha uma pasta local sincronizada pelo Google Drive, informe um computador por linha no formato `Nome|Peso` e gere a campanha. O PNCP King usa somente seu índice atual, exclui snapshots já completos e cria um arquivo `.pncpguardplan` imutável por trabalhador em `plans`.

Em cada computador, abra `PNCPGuard.exe` uma vez, selecione o plano correspondente e a mesma raiz local do Google Drive. Ao salvar, a tarefa opcional do usuário inicia o Guard dez minutos após o logon e repete a cada trinta minutos, sem segunda instância. O Guard usa `%LOCALAPPDATA%\PNCP Guard`, faz uma chamada por vez, preserva 2 GiB livres e encerra a coleta se o PNCP King estiver aberto.

Os pacotes prontos aparecem em `packages` e contêm somente listas de itens, nunca uma varredura de resultados homologados. No PNCP King mestre, volte a **Opções → PNCP Guard** e use **Importar pasta do PNCP Guard**. A importação valida versão e SHA-256, é idempotente e gera confirmações em `acks`; nenhum banco SQLite é colocado na pasta sincronizada.

O total homologado geral mostrado na grade de contratações é apenas um resumo. Os preços dos itens vêm exclusivamente dos campos de resultado homologado do PNCP; valores estimados nunca são usados como substitutos.

A janela inclusiva começa no mesmo dia de 11 meses de calendário atrás e termina hoje. O corte PNCP continua usando a publicação da contratação; a data do resultado/homologação aparece na coluna **Data Homologação/Obtenção do Preço** do Excel. Para preços web, essa coluna usa a captura já registrada. Datas de resultado ausentes são exportadas como **Não informada**, e os links permanecem na aba **Referências**.

A limpeza ocorre ao abrir e quando o dia muda, inclusive offline. Ela remove contratações vencidas mesmo que fixadas e referências antigas das cotações, preservando os projetos e itens e exigindo reconfirmação das cestas afetadas. Contratações sem publicação mantêm o tratamento anterior: não são excluídas por ausência de data e ficam fora das buscas por período. Backups e pacotes antigos passam pela mesma regra ao serem importados; os arquivos originais permanecem preservados.

A abertura e a importação validam os metadados da retenção e procuram dados vencidos. Sem remoções necessárias, a poda e a reconciliação nacional são dispensadas. A compactação só ocorre em **Opções → Arquivo → Compactar banco**, com reserva de espaço de duas vezes o banco.

As medições em cópias isoladas e os cenários de validação estão em [docs/retention-validation.md](docs/retention-validation.md).

O estudo histórico de custo está em `docs/price-load-study.md`. A atualização atual usa uma única prévia e reutiliza listas e resultados completos da versão vigente, inclusive respostas vazias.

O Sweet Code pode ser aberto ao lado da pesquisa. Cole um crivo por linha, ative as sugestões e use ↑/↓ e `TAB` para preencher sem impedir a digitação livre.

## Licença e direitos autorais

**PNCP King — Copyright © 2026 Felipe Arcencio. Todos os direitos reservados.**

Este é um projeto de **código-fonte publicamente disponível, mas não open source**. A disponibilização do código neste repositório não concede autorização para utilização, modificação, distribuição ou criação de trabalhos derivados.

O uso depende de autorização prévia por escrito. Solicitações de licença: **felipearcencio@gmail.com**.

Consulte o arquivo [`LICENSE`](LICENSE) para as condições completas.

O PNCP King é um projeto independente e não oficial. Não pertence, não é mantido e não representa a Prefeitura Municipal de Ribeirão Preto.

---

## Aviso de independência institucional

O PNCP King é um projeto independente e não oficial, desenvolvido e mantido por iniciativa de seu autor. Não é um software da Prefeitura Municipal de Ribeirão Preto, não é mantido pela Prefeitura e não representa manifestação, produto ou serviço oficial do Município, sendo realizado como projeto pessoal. A Prefeitura de Ribeirão Preto possui, ainda assim, autorização de uso gratuito e livre de custos para sempre, ainda que o programa se torne posteriormente comercial.

Eventuais referências à Prefeitura Municipal de Ribeirão Preto, inclusive seu Brasão de Armas, aparecem exclusivamente quando necessárias à reprodução ou compatibilidade com modelos oficiais de documentos utilizados no fluxo administrativo. A presença desses elementos não implica patrocínio, aprovação, certificação ou vínculo institucional do PNCP King com a Prefeitura.

O programa utiliza dados públicos disponibilizados por fontes oficiais, especialmente o Portal Nacional de Contratações Públicas — PNCP. O código, funcionalidades, documentação e decisões de desenvolvimento do PNCP King são de responsabilidade de seus mantenedores, ressalvados os direitos sobre símbolos, documentos, dados e demais conteúdos pertencentes aos respectivos titulares.
