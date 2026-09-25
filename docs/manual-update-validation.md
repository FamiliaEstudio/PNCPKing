# Atualização manual `.pncpupdate` v2

## Objetivo

O formato v2 foi desenhado para que o computador receptor, inclusive com HD lento, faça somente o trabalho indispensável. O exportador assume as verificações de completude, estrutura e integridade. O receptor lê o manifesto pequeno, consulta recibos e toca apenas os blocos ainda necessários.

## Janela e conteúdo

Cada exportação usa a data local do computador de origem e inclui exatamente hoje e os nove dias anteriores. Há um SQLite tipado por dia de publicação e, quando necessário, um décimo primeiro bloco para contratações publicadas antes da janela mas oficialmente alteradas nela.

Cada bloco pode conter contratações já recebidas, snapshots de listas completas de itens, itens dessas listas, snapshots de resultados concluídos, resultados e células de cobertura concluídas. A exportação parcial também leva checkpoints de páginas de publicação diária que já foram gravadas. Listas e resultados vazios são provas completas e atômicas. CATMAT/CATSER, cotações, cestas, evidências, configurações, FTS e estados operacionais particulares não são transportados.

O manifesto contém período, geração, validação, identidade do pacote, formato, esquema, contagens e, para cada bloco, chave, tipo, nome, tamanho, SHA-256 e digest lógico.

## Exportação

A exportação apenas empacota dados já salvos e pode ocorrer com o dia atual ou outros dias incompletos. Contratações disponíveis são incluídas; listas e resultados entram somente quando há prova de conclusão compatível com a versão da contratação e do item. Células de cobertura pendentes não são marcadas como concluídas no destino. Checkpoints diários permitem retomar a página seguinte já gravada; sem checkpoint, a modalidade recomeça pela primeira página. Checkpoints de verificação global não são transferidos porque dependem da base anterior de cada computador; o destino usa sua própria última sincronização. Relações, chaves e contagens do conteúdo concluído são verificadas. Cada SQLite gerado passa por `foreign_key_check` e `integrity_check` no exportador antes da compactação `SmallestSize`.

O arquivo final é recusado atomicamente se atingir 2 GiB.

## Importação

O esquema 29 remove a linhagem cumulativa e cria somente recibos pequenos de pacotes e blocos; a migração não reescreve as tabelas volumosas. Qualquer banco ou backup com esquema compatível pode receber imediatamente um pacote v2.

Para cada bloco sem recibo concluído, o receptor extrai uma única vez e calcula o SHA-256 durante essa leitura. Em seguida anexa o SQLite tipado em modo somente leitura e aplica uma transação. Não executa `integrity_check`, validação JSON, varredura semântica prévia, retenção, `VACUUM` ou consulta ao PNCP.

As regras de conciliação são:

- inserir registros oficiais ausentes;
- atualizar contratação somente quando o instante `global_updated_at` recebido for comprovadamente posterior;
- substituir listas e resultados somente com versões compatíveis e posteriores, ou completar dados comprovadamente ausentes;
- preservar o destino quando a versão recebida for igual, ausente, mais antiga ou não puder ordenar com segurança conteúdo divergente;
- gravar cobertura e recibo somente depois dos dados, na mesma transação.
- aplicar checkpoints de publicação parcial sem sobrescrever trabalho já concluído no destino; **Atualizar** retoma as lacunas, listas e resultados restantes.

Gatilhos normais mantêm FTS, estatísticas e índices derivados apenas para registros efetivamente alterados. Cancelamento ou falha reverte o bloco corrente; a retomada ignora os anteriores. Reimportar o mesmo pacote depende apenas do manifesto e dos recibos e não extrai bancos.

Pacotes v1 são rejeitados com mensagem explícita e não possuem caminho de adoção.

## Benchmark

`OfficialUpdatePerformanceTests` registra, quando `PNCPKING_UPDATE_REPORT` é definido, tamanho compactado e expandido, quantidade de extrações, bytes acrescentados ao banco e durações da exportação, primeira importação e reimportação. A repetição exige zero extrações.
