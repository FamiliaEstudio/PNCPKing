# Base completa sem cotações: versão 1.2.45

## Contrato da transferência

A partir da 1.2.45, `FullWithoutQuotations` (formato 3) e `PricesPreservingLocalData` (formato 5, compatibilidade com 1.2.44) usam o mesmo importador. O atalho de exportação gera o perfil completo sem cotações. Não há um segundo banco permanente nem uma nova modalidade de mesclagem.

O snapshot público recebido é definitivo. Contratos, itens, resultados, snapshots, cobertura e catálogo exclusivos do destino são descartados da base ativa. Não se comparam versões nem se juntam árvores públicas; o usuário deve escolher uma origem completa e atualizada.

O emissor cria o snapshot, remove dados particulares, compacta e executa `integrity_check`. Contadores públicos de catálogo são calculados nessa máquina e exportados em duas linhas de estado neutro, sem progresso privado de sincronização.

O receptor extrai o banco com SHA-256 no mesmo fluxo, valida manifesto/esquema e copia somente tabelas locais para o staging. Preserva IDs, cotações, cestas, divisores, evidências, rascunhos, buscas, trabalhos locais e configurações. Compara o conteúdo local copiado nos dois sentidos e valida suas referências. Controles de coleta são preservados; marcações por contrato usam consultas pelos intervalos do índice de propriedade, somente para contratos presentes na base recebida. Não são criados contratos ou preços ausentes.

O esquema 34 remove apenas a FK `quotation_processed_contracts.contract_id → contracts.pncp_id`. A FK para a execução local permanece. A migração transacional copia essa tabela local e recria seu índice, sem percorrer preços. Os valores e metadados em `quotation_references` já eram snapshots independentes: cálculos e relatórios não precisam reencontrar o preço na base pública. Detalhes e novas pesquisas públicas dependem da base recebida.

O gate por caminho e a guarda de arquivo Windows protegem a ativação. O staging é consolidado e descarregado antes de `File.Replace`; o arquivo anterior permanece como recuperação. Cancelamento ou falha anterior à troca não substitui o banco ativo. A troca é a fronteira de commit; falhas posteriores de limpeza/progresso não invalidam o sucesso.

Não há `VACUUM`, `integrity_check` ou retenção no fluxo receptor. Um pacote legado da 1.2.44 sem contadores exige contagem apenas do catálogo recebido, nunca comparação de contratos/itens/resultados do destino. A rotina normal de retenção de 11 meses continua fora dessa operação.

## Validação

Os testes cobrem substituição de bases divergentes e vazias, remoção de árvores exclusivas, preservação literal das tabelas locais, referências a contrato/item/resultado ausente nos dois perfis, histórico processado sem contrato, cálculo/relatório, evidências, reimportação, recuperação, corrupção, cancelamento, falha antes da troca e concorrência. Testes de migração verificam os dados locais e a integridade do esquema 34.

As medidas de desempenho são sintéticas no computador de desenvolvimento, não previsão para HD antigo. A mesma fixture de 1 milhão de itens usa 50 mil contratos, cinco UFs, 200 municípios e 500 fornecedores. O ensaio anterior da 1.2.44 mediu 1,19 s na primeira importação para destino pequeno e 13,85 s na reimportação com destino completo.

### Medição final executada

Com 1 milhão de itens / 50 mil contratos, banco de 306.933.760 bytes e pacote de 53.739.911 bytes:

- Exportação: 10,549 s.
- Primeira importação, destino com 7 itens e trabalhos locais: 1,323 s.
- Reimportação, destino já com 1 milhão de itens: 1,312 s.
- Ensaio anterior da 1.2.44 no mesmo computador: reimportação de 13,85 s. A nova medição foi 90,5% menor. Esse comparativo sintético não prevê o tempo de um HD antigo.

Foram aprovados 38 casos focados, além do benchmark de 1 milhão efetivamente habilitado. Incluem pacotes legados reais no esquema 33, recusa do esquema 32 antes da extração e reaplicação real do mesmo `.pncpupdate` após a substituição: o reset dos recibos permite recuperar preços antes descartados, nos dois perfis.

## Limites

O custo mínimo permanece: ler/descompactar o arquivo inteiro, gravar o novo banco e copiar/conferir o trabalho local. Não é uma renomeação instantânea de um arquivo já pronto. A base anterior ocupa espaço até a remoção deliberada da recuperação.

Não houve acesso ao banco real nem teste físico no HD de 16 anos. Não foram simulados corte real de energia ou falta física de espaço. A durabilidade final depende do Windows, sistema de arquivos e hardware. Testes usam verificações integrais para aferir seus resultados; essas verificações não fazem parte do receptor do aplicativo.

Para esta substituição, pacotes de esquema anterior a 33 devem ser reexportados no PC de origem. Isso evita migrações antigas que reconstruíam índices ou aplicavam retenção no receptor. Backups de recuperação com cotações mantêm seu suporte de migração.

`official_update_packages`, `official_update_chunks`, `guard_imported_packages` e `sync_partitions` são zerados no snapshot e no receptor: são recibos/checkpoints da base pública anterior. Preservá-los faria atualizações futuras ignorarem preços descartados. `sync_runs` permanece como histórico/intenção de tarefa; retomadas reiniciam as partições com segurança. Campanhas e configurações Guard permanecem.

Validação final local: solução compilada em Release sem avisos/erros, 1.068 testes aprovados (zero falhas/ignorados) e checks de interface aprovados. O benchmark foi executado separadamente com `PNCPKING_FULL_TRANSFER_BENCHMARK=1`.
