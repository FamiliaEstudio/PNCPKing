# Cotação com IA externa por arquivos

O Excel cria o projeto e seus itens. A coleta lê o banco local sem baixar documentos. A IA primeiro pode retirar candidatos claramente incompatíveis e depois devolver um guia com os preços escolhidos. Desde a versão 1.2.34, a importação traz também os demais candidatos do ZIP e cria as cestas guiadas válidas. Os candidatos disponíveis alimentam as alternativas do programa; a triagem não certifica sua adequação técnica.

## Entrada e coleta

Em **Cotações → Automação → IA externa — por arquivos**, salve o modelo Excel. Preencha a aba `PNCPKing_IA_v1`, mantendo o cabeçalho, e use **Importar XLSX**. A prévia permite conferir os itens, escolher pesos e selecionar/criar o projeto. O resultado é um ZIP.

| Coluna | Conteúdo |
| --- | --- |
| A | Pesquisa: `café torrado + café moído + café em grãos`. Cada trecho é independente, como na busca principal. |
| B | Nome de saída do item. |
| C–D | Quantidade inteira positiva e unidade solicitada. |
| E–F | Faixa mínima/máxima na unidade solicitada, opcional. |
| G | Páginas por expressão, de 1 a 100; até 50 preços por página. |
| H | Quantidade desejada de preços, de 3 a 10; vazio usa 3. |
| I–J | CATMAT e quantidade mínima nominal por pedido, opcionais. |
| K | Descritivo completo para comparação técnica; vazio usa B. |

Todas as expressões são executadas, alternando páginas de um item por vez. Encontrar três preços não encerra a coleta. Um preço encontrado em várias expressões aparece uma vez por item, com as buscas que o localizaram. A operação usa somente os dados disponíveis; não atualiza a base nacional.

O limite G é **por expressão**. Se ainda houver resultados, o pacote informa coleta parcial. A faixa E–F é validada depois sobre o preço efetivo: a coleta ampla não elimina embalagens pelo preço bruto nem aplica o corte lexical da formação de cestas.

Planilhas antigas, sem cabeçalho, conservam o formato A:J e a interpretação anterior. Para usar o novo `+`, utilize o modelo identificado. Não renomeie uma aba antiga sem conferir as expressões.

## Resultado e triagem

O ZIP contém `manifest.json`, `LEIA-ME.txt` e lotes JSONL UTF-8 autossuficientes. Cada lote traz a especificação e candidatos com IDs, descritivos integrais, preço, unidade, fornecedor, datas e origem PNCP. O alvo é 256 KiB ou 250 referências por lote; um descritivo grande é preservado sem truncamento. Contagens finais e cobertura estão no manifesto.

Extraia os lotes para fornecê-los separadamente à IA quando necessário. Compactação reduz espaço em disco, não a quantidade de texto que a IA precisa analisar. Compare todos os lotes de um item antes de escolher a cesta final.

Use **Gerar modelos de triagem e guia**, escolhendo o ZIP. Na aba `Triagem`, a IA informa `item_id`, `referencia_id`, `decisao` (`manter`, `rejeitar`, `revisar`) e `justificativa`. Somente `rejeitar` exclui o candidato. Omissões e `revisar` preservam os dados. Informação ausente não comprova incompatibilidade; não excluir por preço baixo nesta fase.

Use **Aplicar triagem** para gerar outro ZIP. O original é preservado. O resultado registra os hashes do pacote original e da planilha de triagem. Gere um **novo guia a partir do ZIP filtrado**: o guia antigo não corresponde ao novo pacote. Modelos já existentes não são sobrescritos ao gerar novamente.

## Guia por máxima similaridade

A IA deve comparar requisitos essenciais antes de ordenar por preço e pesos. Material, capacidade, medidas, composição, finalidade ou embalagem incompatíveis não podem ser compensados por pontuação. Dados ausentes não são atendimento comprovado. Entre candidatos compatíveis, usar os pesos do item e priorizar a maior correspondência técnica.

Na aba `Itens`, preencher `situacao` (`pronto`, `insuficiente`, `revisar`), `metodo` (`media`, `mediana`), justificativa e unidade final. Preservar IDs e hashes. Na aba `Selecoes`, informar os IDs escolhidos, ordem, fator ou divisor, justificativa técnica, eventual justificativa de conversão e um trecho literal da fonte. O programa recupera os valores do pacote; o guia não retranscreve preços.

Para fracionar uma embalagem, use a coluna opcional `divisor`: uma caixa de 12 unidades usa divisor `12`, fator `1` e numerador/denominador vazios. A divisão usa o cálculo exato já existente na versão 1.2.32, sem aproximar 1/12.

O fator **multiplica** o preço de origem e é uma alternativa ao divisor. Se diferente de 1, deve ter até seis casas e numerador/denominador que o representem exatamente: `fator × denominador = numerador`. Pacote de 10 para unidade também pode usar numerador 1, denominador 10 e fator 0,1. Fatores arredondados, como 0,083333 para 1/12, são recusados. Não combinar divisor com fator diferente de 1; ambos aceitam até seis casas.

A cesta deve atingir o alvo e conter pelo menos três preços. Não repetir **CNPJ + contratação PNCP na cesta do mesmo item**. O fornecedor pode aparecer em outros itens ou contratações. CNPJ ausente/inválido exige revisão.

## Aplicação e revisão

Use **Importar guia final**, selecione primeiro o ZIP utilizado pela IA e depois o Excel do guia. A prévia mostra cestas prontas, candidatos disponíveis, insuficiências e pendências, com mensagens copiáveis. **Importar preços e aplicar cestas válidas** traz os candidatos de todos os itens corretamente vinculados ao pacote, inclusive dos itens insuficientes ou em revisão. Candidatos de um item com divergência de identificação/especificação ou pacote corrompido não são importados.

Cada importação de candidatos é atômica por item; a aplicação da cesta e de sua eventual faixa também é atômica. Se uma cesta continuar pendente, os candidatos já importados ficam disponíveis para trabalho manual. Reimportar não duplica cestas ou preços e não sobrescreve edições manuais. Após a importação, a barra de cálculo acompanha a formação das alternativas, incluindo recomendada, mais barata e mais cara, segundo os pesos existentes. As alternativas não representam todas as combinações possíveis: o algoritmo conserva seus limites de trabalho.

Para aproveitar um preço de outra cesta, abra o item, selecione o preço e use **Copiar preço para outra cesta**. Escolha uma cesta do mesmo item. A origem permanece intacta; o fator ou divisor é preservado. Se o destino for automático, o programa cria uma cópia manual. Repetição de referência ou de empresa na mesma contratação e excesso de dez preços são recusados. A cesta resultante passa pela validação e não é adotada silenciosamente.

### Ampliação de faixa

Os modelos novos incluem a aba opcional `Faixas`, com os cabeçalhos `item_id`, `faixa_minima`, `faixa_maxima`, `justificativa`. Uma linha por item; informe os limites ampliados necessários e a justificativa técnica. Um limite em branco conserva o limite original. A proposta deve apenas ampliar a faixa, sem estreitar um lado ou criar um limite onde antes não havia. Sem ampliação, deixe a aba vazia. Preserve os hashes e todas as outras abas.

Para guias antigos, ou sem proposta explícita, marque **Permitir ampliar a faixa para os preços escolhidos no guia** na prévia. Clique em **Revalidar e mostrar alterações**, confira os limites e só então aplique. A expansão automática alcança apenas os preços efetivos selecionados. Uma proposta explícita na aba `Faixas` é respeitada; o programa não a amplia silenciosamente se ainda for insuficiente. A faixa e a cesta são gravadas juntas somente quando a cesta passa pelas demais validações. O relatório guarda a alteração e sua razão.

O ZIP original e seus hashes permanecem intactos. Não é necessário recolher os mesmos candidatos; uma nova pesquisa só é necessária quando faltarem dados. Ampliar a faixa não altera a regra de 25%, calculada contra a média truncada dos demais preços da cesta.

### Descrições resumidas e revisão técnica

Quando a IA selecionou preços tecnicamente comparáveis, mas a baixa coincidência de palavras bloqueia a importação, a prévia permite **Aceitar revisão técnica documentada no guia quando a pontuação textual for baixa**. Marque a opção e revalide antes de aplicar. Cada preço deve continuar acompanhado de justificativa técnica e trecho literal da origem; o programa mostra a exceção para conferência.

A decisão é preservada junto ao preço do item e vinculada aos dados técnicos de origem. Alterações que invalidem essa vinculação exigem nova revisão. A pontuação baixa permanece informativa; a exceção só vale para cestas manuais e não libera o candidato automaticamente nas alternativas da busca normal. Preço, faixa, conversão, CNPJ, repetição de origem e dispersão continuam sujeitos às conferências. Uma justificativa da IA torna a decisão revisável, sem provar por si só a equivalência técnica.

São conferidos pacote, IDs, especificação atual, fatores, preço efetivo, faixa, desvio de até 25% e validação vigente das cestas. Permanecem os valores de origem e o truncamento, com duas casas ou quatro para medicamentos. Uma cópia do guia identificado por hash e um relatório da aplicação ficam junto ao pacote para auditoria.

**Limitação:** a validação local não comprova equivalência técnica por si só. A justificativa e o trecho tornam a escolha revisável. Sem autorização da revisão técnica, o corte textual continua aplicado; as outras causas de inelegibilidade permanecem pendentes. Não se alteram descrição ou preço original para forçar aprovação.

Documentos podem ser obtidos pelos comandos de evidências depois da seleção. Nenhuma etapa deste fluxo dispara anexos, PDFs ou OCR automaticamente.

## Desempenho e retomada

- Um leitor do banco, páginas limitadas e comandos preparados na inserção. Sem migração do esquema 33, novos índices ou mudanças no SQL da pesquisa.
- Candidatos e checkpoints em arquivo auxiliar junto ao ZIP. Depois de exportação durável concluída, o auxiliar é removido; em pausa/falha, é preservado.
- Memória limitada por páginas e lotes; interface recebe contadores agregados a cada 150 ms.
- Inserção de candidatos em lotes de 100 e transações por item. Cancelar desfaz o item em processamento; itens concluídos permanecem disponíveis, sem duplicação na repetição.
- Visão geral compacta, cálculo em segundo plano com progresso e candidatos completos somente do item selecionado. Interromper o cálculo não apaga os preços importados.
- Use **Retomar automação** após pausar/fechar. Se a base mudar, somente o item incompleto reinicia; itens concluídos conservam suas capturas datadas.
- Mudanças na especificação/filtros exigem nova coleta; a ampliação de faixa autorizada usa o mecanismo específico acima. Atualizações e outras operações concorrentes devem terminar antes da coleta.

Os testes incluem bases sintéticas com 450 itens. Tempos e consumo no HD antigo precisam ser medidos na máquina de destino; testes e compilação não constituem benchmark desse hardware.
