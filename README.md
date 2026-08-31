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
- sintaxe textual com E implícito ou `+`, OU por `OU`, `OR` ou `|`, frases entre aspas fechadas, exclusões globais por `-palavra` ou `-"frase"` e unidades aceitas por marcadores como `"pacote "unidade`; expressões antigas com `C:(...)` continuam aceitas, mas o bloco não é mais gerado nem recomendado para novas pesquisas;
- pesquisa local primeiro: a primeira página entrega imediatamente até 50 preços homologados atuais exclusivamente do banco, com ordenação exata, eliminação de duplicidades e paginação por cursor, sem iniciar uma ampliação silenciosa pela API;
- revalidação automática, visível e cancelável somente das contratações anteriormente carregadas cujos itens foram invalidados por uma nova `dataAtualizacaoGlobal`; itens ausentes ou nunca indexados continuam exclusivos da ação **Ampliar pela API**, e a grade só é substituída depois da revalidação completa;
- filtros `Todos`, `Cidades Próximas`, `Sudeste` e UF, períodos de 7 a 365 dias ou personalizados e ordenação por relevância, data ou proximidade;
- catálogo nacional embutido das localidades oficiais de 2022 do IBGE, usado somente para distância e ordem geográfica, sem consultas remotas por município;
- percurso fixo de candidatos: Ribeirão Preto e os outros 49 municípios mais próximos por distância, restante de SP em amostra aleatória estável e depois cada UF pela proximidade de sua sede municipal mais próxima;
- sorteio estável durante cada pesquisa, paginação por cursor sem repetição e nova rotação aleatória ao iniciar outra pesquisa;
- ampliação explícita com seleção de 1 a 200 lotes; cada lote cobre até 50 contratações ainda não resolvidas e avança gratuitamente pelas já completas no cache, processadas automaticamente em parcelas locais de até 20 lotes (1.000 contratações);
- resultados acrescentados progressivamente em uma única grade virtualizada, com percentual, contratações solicitadas/processadas, itens compatíveis, preços revelados e chamadas reais de listas/resultados;
- grade de preços inicialmente enxuta com as nove colunas principais e layouts de visibilidade, ordem e largura persistidos por grade; o seletor permite restaurar o padrão;
- biblioteca opcional Sweet Code, persistida no backup, com um crivo por linha e autocomplete por prefixo usando setas e `TAB`;
- sessão retomável separada para a última pesquisa geral, com cursor, resultados e falhas preservados ao fechar; a automação continua usando armazenamento temporário isolado;
- índice nacional opcional e móvel das listas de itens dos últimos 365 dias, autorizado somente após estimativa de espaço/tempo, com checkpoint por contratação, pausa, retomada, poda seletiva e reserva mínima de disco; a carga de fundo não consulta resultados homologados;
- resultados homologados consultados somente para itens compatíveis com a pesquisa e preservados no banco principal para reutilização até a contratação receber uma atualização global;
- segundo índice nacional opcional de preços dos últimos 365 dias, com autorização própria e download exclusivamente pelo botão agressivo; ele consulta somente itens com `temResultado=true`, conserva todos os resultados `Informado` com valor unitário homologado positivo e conclui sem repetir respostas vazias, canceladas, sem valor útil ou `404`;
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
- agendador único com concorrência adaptativa; no modo normal o índice usa baixa prioridade e uma lista por vez, enquanto os modos agressivos mutuamente exclusivos podem ocupar os limites recomendados da máquina, reduzir progressivamente até uma chamada diante de `429`, timeouts repetidos ou latência alta e retomar por checkpoint sem repetição HTTP agressiva;
- distinção entre preço encontrado, resultado cancelado, item sem resultado, pendência e falha;
- invalidação de listas e preços permanentes quando a contratação muda;
- plano de consulta adaptado ao volume: FTS primeiro para relevância ou até 20 mil ocorrências e contratos ordenados em chunks ajustáveis de 64 a 512 para termos amplos, sem `OFFSET` e mantendo somente uma página mais um resultado em memória;
- `PRAGMA optimize` marcado após atualizações concluídas e executado somente em ociosidade, com registro de sucesso para evitar manutenção concorrente com a primeira página;
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

Somente `artifacts\win-x64\PNCPKing.exe` será publicado. O projeto PNCP Guard também é compilado em Release pela solução, mas não integra essa distribuição.

A suíte automatizada cobre pesquisa por objeto e item, geografia, faixa de preço, valores homologados, rejeição de valores estimados, múltiplos resultados, falha parcial, `429`, timeout, cobertura diária, retomada por checkpoint e validação de backups.

## Uso

1. Na primeira abertura, escolha a pasta que armazenará o banco.
2. Clique em **Calcular tamanho** e aguarde a contagem das modalidades.
3. Revise o volume, o espaço e a duração estimados.
4. Clique em **Baixar/atualizar dados** e confirme os números exibidos.
5. Opcionalmente, depois de concluir a cobertura das contratações, use **Índice nacional de itens — últimos 365 dias → Estimar e ativar**. Revise chamadas de listas, espaço e duração antes de autorizar. A carga armazena somente listas de itens; no modo normal ela cede o PNCP às ações visíveis, e o botão **Download agressivo** dedica a sessão às listas.
6. Depois que as listas estiverem completas, você também pode usar **Índice nacional de preços — últimos 365 dias → Estimar e ativar**. A autorização não chama o PNCP: as consultas em massa só começam ao ativar o **Download agressivo** dessa segunda barra. Normalmente cada item produz uma vencedora positiva; quando o PNCP registrar várias vencedoras válidas para o mesmo item, todas são preservadas.
7. Digite o objeto, escolha geografia, período e ordenação e clique em **Pesquisar**.
8. Você pode combinar termos: `café filtro` ou `café + filtro` exigem ambos; `café OU chá` aceita qualquer um; `"café torrado"` busca a frase; `café -cafeteira -"filtro de papel"` exclui descrições; `"pacote "unidade` aceita qualquer uma dessas unidades estruturadas do item. O parser continua aceitando `C:(...)` em expressões antigas, mas novas pesquisas e sugestões da IA não precisam nem recebem esse bloco.
9. Ao clicar em **Pesquisar**, a primeira página de até 50 preços homologados atuais vem exclusivamente do banco local. Se houver contratações previamente carregadas e invalidadas por uma atualização oficial, o aplicativo mostra **Revalidando preços alterados no PNCP**, consulta somente essas contratações e atualiza a grade de uma vez ao concluir. **Parar preços** cancela essa revalidação sem apagar os preços locais nem seus checkpoints.
10. Para alcançar itens ausentes, falhas ou contratações ainda não indexadas, informe de 1 a 200 lotes e use **Ampliar pela API**. A escolha é aditiva e corresponde a até 50 novas contratações por lote; as já resolvidas no cache não consomem essa cota e são percorridas automaticamente em parcelas de até 1.000 para reduzir a pressão sobre discos mecânicos. Todos os itens que ainda exigirem rede usam a concorrência adaptativa do PNCP. Use **Carregar mais resultados** para exibir páginas de até 50 preços salvos, **Reiniciar pesquisa** para criar nova rotação e **Parar preços** para interromper preservando o checkpoint. O limite de 200 pertence à ação, não à capacidade em bytes ou registros do cache persistente; **Esgotar pela API** percorre todo o conjunto confirmado somente quando a ampliação explícita é iniciada.
11. Use os campos de preço mínimo/máximo para filtrar o valor unitário homologado ativo.
12. Para iniciar uma cotação, clique em **Usar esta amostra em uma cotação**, selecione ou crie um projeto e informe quantidade, unidade, alvo automático de 3 a 10 preços e faixa opcional.
13. Para montar sua própria composição, selecione uma ou mais linhas homologadas com `Ctrl`/`Shift` e use **Criar/adicionar à cesta manual**. Na aba **Cotações**, você pode ampliar, renomear, revisar, confirmar ou excluir essas cestas.
14. Faça novas pesquisas e adicione outros itens ao mesmo projeto. Se ampliar a coleta de um item, use **Atualizar amostra com a pesquisa atual**; a escolha anterior ficará marcada para reconfirmação.
15. Use **Importar XLSX** para carregar vários itens pelas colunas A:G e, opcionalmente, o alvo da cesta em H. H vazia usa 3. A automação interpreta a coluna G como lotes de 50 contratações; falhas podem ser retomadas. **Exportar Excel** preenche o modelo de avaliação com a cesta atual, links PNCP/site e fórmulas ajustadas ao número real de preços; o PDF de evidências é salvo na mesma pasta.
16. Na aba **Cotações**, use **Exportar pacote** para criar um `.pncpcotacao` portátil com a cotação selecionada e seus prints. **Importar pacote** mostra uma prévia e, se o mesmo identificador já existir, permite importar como cópia, substituir com recuperação automática ou cancelar.
17. Para manter uma contratação no cache permanente, selecione-a na segunda aba e use **Buscar/atualizar todos os preços**.
18. Use **Abrir contratação no PNCP** para acessar a página oficial. Use **Acessar documentos** para baixar, extrair e consolidar os PDFs; o arquivo será salvo em `Downloads` e somente será aberto se você escolher **Abrir PDF** ao final.
19. Use **Escolher colunas** para ajustar cada grade uma vez. Visibilidade, ordem e largura são restauradas nos usos seguintes; **Restaurar padrão** volta ao layout original.
20. Se ocorrer uma falha de abertura ou importação, use **Logs de diagnóstico**, copie o arquivo `.log` mais recente e envie-o para análise. Mesmo quando a janela principal não abre, a mensagem de erro informa o caminho exato do log.

## PNCP Guard

Em **Opções → PNCP Guard**, escolha uma pasta local sincronizada pelo Google Drive, informe um computador por linha no formato `Nome|Peso` e gere a campanha. O PNCP King usa somente seu índice atual, exclui snapshots já completos e cria um arquivo `.pncpguardplan` imutável por trabalhador em `plans`.

Em cada computador, abra `PNCPGuard.exe` uma vez, selecione o plano correspondente e a mesma raiz local do Google Drive. Ao salvar, a tarefa opcional do usuário inicia o Guard dez minutos após o logon e repete a cada trinta minutos, sem segunda instância. O Guard usa `%LOCALAPPDATA%\PNCP Guard`, faz uma chamada por vez, preserva 2 GiB livres e encerra a coleta se o PNCP King estiver aberto.

Os pacotes prontos aparecem em `packages` e contêm somente listas de itens, nunca uma varredura de resultados homologados. No PNCP King mestre, volte a **Opções → PNCP Guard** e use **Importar pasta do PNCP Guard**. A importação valida versão e SHA-256, é idempotente e gera confirmações em `acks`; nenhum banco SQLite é colocado na pasta sincronizada.

O total homologado geral mostrado na grade de contratações é apenas um resumo. Os preços dos itens vêm exclusivamente dos campos de resultado homologado do PNCP; valores estimados nunca são usados como substitutos.

Após a primeira carga autorizada, o programa verifica periodicamente se o calendário avançou ou se há lacunas. Ele baixa primeiro as publicações ausentes, faz a atualização global com sobreposição de 48 horas e só então ajusta a borda antiga da janela de 365 dias. Uma falha nunca antecipa a exclusão de registros.

O estudo de custo está em `docs/price-load-study.md`. Os índices de listas e preços são opcionais, limitados à janela móvel de 365 dias e exigem estimativas e autorizações independentes. Autorizar preços não inicia tráfego; somente seu modo agressivo consulta um endpoint de resultado por item elegível ainda incompleto.

O Sweet Code pode ser aberto ao lado da pesquisa. Cole um crivo por linha, ative as sugestões e use ↑/↓ e `TAB` para preencher sem impedir a digitação livre.
