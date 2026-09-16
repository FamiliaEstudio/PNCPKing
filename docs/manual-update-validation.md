# Atualização manual e pacotes cumulativos

Implementação de setembro de 2026. O executável continua em `artifacts/win-x64/PNCPKing.exe`. Não há alterações nos contratos públicos da pesquisa, nos índices permanentes de pesquisa ou na estratégia progressiva anteriormente validada.

## Comportamento

- Abrir, pesquisar, importar bancos e o agendamento ocioso não iniciam downloads. A manutenção ociosa executa somente estatísticas SQLite e checkpoints. As configurações antigas de atualização periódica do catálogo são normalizadas para manual.
- **Atualizar** usa uma prévia e executa contratações, listas e preços, com concorrência adaptativa. Cada etapa precisa terminar antes da seguinte; falhas, indisponibilidade de disco e cancelamento deixam pendências explícitas. Pressão crítica de RAM interrompe o ciclo e conserva checkpoints.
- Pausar/Continuar atua nas três etapas. Pesquisar durante a atualização suspende o trabalho remoto e permite retomá-lo após a ociosidade; uma pausa manual continua manual.
- As lacunas de publicação são preenchidas das mais novas às mais antigas. A atualização global começa dois dias antes da última verificação concluída, limitada à janela. Sem histórico confiável, verifica os 11 meses. O identificador dos checkpoints separa ciclos concluídos; cliques repetidos no mesmo dia consultam novidades novamente.
- Respostas completas de listas e preços, inclusive vazias, são reutilizadas. CATMAT/CATSER permanece separado.

## Retenção

A importação não passa mais `force:true` e não prepara os índices nacionais. A comprovação usa data/corte do banco e `EXISTS` de vencidos, incluindo referências PNCP, evidências web e metadados vencidos. O manifesto não substitui essa conferência. Sem vencidos, atualiza somente os metadados necessários e informa “Nenhuma remoção necessária”.

A remoção de contratos, referências e mudanças nas cestas continua atômica. Cascatas e gatilhos mantêm os registros afetados. O marcador de conclusão é gravado na mesma transação, independentemente de downloads. As preparações concluídas sobrevivem à mudança normal de janela; quando há dados ainda não contabilizados entrando na janela, a preparação permanece pendente.

Abertura e importação não executam `VACUUM`. **Compactar banco** é explícito e mantém a reserva de espaço e verificação de integridade. A telemetria distingue `retention-validation`, `retention-removal` e `import-activation`; o total de retenção permanece disponível. A conferência e a poda usam interrupção SQLite vinculada ao cancelamento.

## Formato e conciliação

O esquema 28 acrescenta identidade da base, origem, revisão, registro compacto de entidades modificadas, provas de conclusão de preços, recibos de importação e conflitos. Não regrava a população antiga de itens na migração. Gatilhos registram alterações oficiais dentro da transação dos dados; a conclusão de resultados é registrada pelos caminhos de gravação de preços e do Guard.

`.pncpupdate` contém `manifest.json` e `updates.db`. O SQLite do pacote armazena unidades lógicas com JSON tipado e hash; não contém FTS, permissões, configurações, cotações nem estados de execução. A lista de tabelas/colunas é fixa no aplicativo, não derivada de SQL do arquivo. Chaves inválidas são rejeitadas inclusive em conjuntos vazios, antes da primeira transação de aplicação. A cobertura transportada contém somente células comprovadamente concluídas; elas são aplicadas depois dos dados, para evitar repetição da carga de publicação. Não são transportados checkpoints de downloads em andamento nem a autorização para continuá-los.

A exportação usa uma leitura consistente. A base inicial percorre os dados oficiais uma vez. As próximas exportações percorrem as chaves alteradas desde a base, mantendo apenas um registro por entidade. Remoções de cache e retenção não viram exclusões oficiais. Exportar um `.pncpking` não reinicia a linhagem; restaurar uma cópia recebe nova origem local.

Listas e conjuntos de resultados são completos e atômicos, inclusive quando vazios. Conteúdo idêntico não altera o destino. Datas oficiais ordenam versões; conteúdo divergente sem ordem demonstrável preserva o destino e registra conflito. **Atualizar** prepara a revalidação dessas entidades; o catálogo resolve seus conflitos em sua publicação manual. O relógio dos PCs não decide qual conteúdo oficial vence.

O arquivo inteiro é extraído, conferido por SHA-256, integridade SQLite e validação de unidades antes de aplicar dados. Cada lote aplica no máximo 32 unidades e termina após 250 ms quando possível, conservando a atomicidade de uma unidade maior. O checkpoint pertence à transação do lote; índices e contadores de preços são atualizados somente para contratações alteradas. O escritor é liberado entre lotes.

Reimportar é idempotente. Um pacote cumulativo posterior pode completar um delta interrompido. A base inicial precisa terminar antes de importar/exportar deltas; seu cancelamento permite retomada. A adoção inicial preserva versões mais recentes e registra as diferenças preexistentes do destino, sem transformar toda a base recebida no próximo delta. A conferência dos registros exclusivos do destino usa uma leitura consistente sem reservar o escritor; as gravações de diferenças ocorrem em lotes de até 32 chaves. Trocar a base é uma ação explícita nas opções de arquivo; a importação comum rejeita outra linhagem.

**Exportar backup** continua oferecendo o `.pncpking` completo, incluindo dados particulares. O novo formato é uma opção adicional.

## Evidências de validação

Resultados em `artifacts/manual-update-validation/`. A suíte completa final passou: **545 testes, zero falhas**, executada em Release com as classes em sequência. A suíte cobre migrações e sua retomada, retenção no mesmo dia e em datas diferentes, backup legado, compactação pendente, referências e cestas, ordenação e cursores de pesquisa, cancelamento/rollback, importação cumulativa e repetida, versões divergentes, catálogos, cobertura concluída, respostas vazias, troca de base, pausa/cancelamento das etapas e reutilização de respostas completas.

Falha anterior identificada durante a execução após 00:00 UTC: três cenários de `AiAutomationTests` criavam publicação com `UtcNow` (dia seguinte), mas limitavam a pesquisa ao dia local. O ajuste ficou restrito à data da fixture, usando `DateTimeOffset.Now`. Nenhuma alteração na automação de cotações foi necessária. Uma execução posterior encontrou bloqueio transitório de `test.db` no descarte da fixture de `NationalPriceIndexTests`, depois das verificações funcionais; o resultado foi preservado em `manual-updates-cleanup-failure.trx` para distinguir esse encerramento da lógica testada. Outra execução paralela encontrou `ObjectDisposedException` ao abrir uma conexão enquanto as fixtures utilizavam `ClearAllPools` globalmente (`manual-updates-parallel-pool-failure.trx`). A validação final executa as classes em sequência, mantendo a concorrência interna dos testes de downloads; a configuração do executor está registrada em `xunit.runner.json`. Não foram modificados os métodos de limpeza globais do aplicativo ou das fixtures. As expectativas de migração e menus foram atualizadas para o esquema 28 e os novos comandos.

### Medição sintética isolada

20.000 contratações; 100 alteradas. `benchmark.json` e `update-benchmark.trx`:

| Operação | Tempo |
|---|---:|
| Exportação da base inicial | 2,20 s |
| Importação da base inicial | 24,73 s |
| Exportação de 100 alterações | 13,3 ms |
| Importação das 100 alterações | 73,5 ms |
| Reimportação do mesmo pacote | 7,5 ms |
| Retenção já concluída no mesmo dia | 0,21 ms |

Pacote inicial: 1.812.750 bytes; cumulativo: 10.362 bytes. A gravação de 100 alterações passou de 6,1 ms sem registro ativo para 13,3 ms com registro ativo; o custo adicional medido foi de 7,2 ms nessa amostra. A base sintética tem 14,4 MB e não representa a população real do usuário.

Primeiro acesso e repetições têm cache do sistema não controlado. São medições no PC doméstico e não substituem a avaliação no HD do serviço.


### Medição em base real grande

Cópia exclusiva de 15.234.502.656 bytes no SSD, com **1.302.074 contratações, 13.690.558 itens e 7.068.861 resultados**, retida em 14/09/2026. O arquivo de uso foi aberto somente para leitura e protegido contra gravação enquanto era copiado; sua cópia levou 12,66 s. O benchmark usa explicitamente o perfil **Restrito** e estabelece uma linhagem de teste para medir somente o delta, sem exportar novamente a população inteira.

| Operação | Primeira medição | Repetição na mesma cópia |
|---|---:|---:|
| Conferência inicial de retenção no mesmo dia | 33,89 ms | 11,04 ms |
| Exportar 100 alterações | 36,79 ms | 36,58 ms |
| Importar 100 alterações | 211,21 ms | 152,25 ms |
| Reimportar o mesmo pacote | 6,89 ms | 7,35 ms |
| Nova conferência de retenção | 0,52 ms | 0,60 ms |

Os pacotes tiveram 42.529 e 42.653 bytes. As duas execuções aplicaram as 100 alterações, sem conflitos, e não removeram contratos nem referências. Evidências: `real-benchmark-first.json`, `real-benchmark.json` e `real-update-benchmark.trx`. As contagens explícitas do banco estão no segundo relatório. A cópia aquece o cache do sistema; nenhum desses tempos representa cache frio controlado ou o HD do serviço.

A preparação anterior de uma cópia antiga de 17,2 GB no HD foi interrompida após cerca de 57 minutos durante a remoção efetivamente necessária de 190.626 contratos vencidos. Não produziu medida de importação e não entra na comparação. O registro está em `old-base-preparation-interrupted.json`/`.trx`. Essa execução não demonstra que a poda volumosa tenha sido acelerada: o ganho validado desta mudança é evitar sua repetição quando não existem vencidos, além de aplicar somente as entidades alteradas. A atomicidade da remoção continua coberta pelos testes funcionais.


## Publicação

Release compilado sem avisos ou erros; suíte completa com 545 testes aprovados; benchmark da base grande aprovado. Publicação única em `artifacts/win-x64/PNCPKing.exe`, com 221,089,274 bytes. SHA-256: `edaad7c266dcb065d54b48062781925a7747b45a10a2badff8ff249a7cfcf58b`. A distribuição contém somente esse executável, sem PDBs ou banco. As duas cópias de benchmark criadas nesta validação foram removidas; os relatórios e a cópia antiga preexistente do repositório foram preservados. Detalhes em `artifacts/manual-update-validation/publication.json`.
