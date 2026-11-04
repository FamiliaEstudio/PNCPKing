# Cotação com IA externa por arquivos

O Excel cria o projeto e seus itens. A coleta lê o banco local sem baixar documentos. A IA primeiro pode retirar candidatos claramente incompatíveis e depois devolver um guia com os preços escolhidos. O PNCP King valida o guia e cria cestas manuais persistentes.

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

Na aba `Itens`, preencher `situacao` (`pronto`, `insuficiente`, `revisar`), `metodo` (`media`, `mediana`), justificativa e unidade final. Preservar IDs e hashes. Na aba `Selecoes`, informar os IDs escolhidos, ordem, fator, justificativa técnica, eventual justificativa de conversão e um trecho literal da fonte. O programa recupera os valores do pacote; o guia não retranscreve preços.

O fator **multiplica** o preço de origem. Se diferente de 1, deve ter até seis casas e numerador/denominador que o representem exatamente: `fator × denominador = numerador`. Pacote de 10 para unidade: numerador 1, denominador 10, fator 0,1. Não arredondar 1/12 para 0,083333; esse caso exige revisão, pois pode mudar o valor truncado.

A cesta deve atingir o alvo e conter pelo menos três preços. Não repetir **CNPJ + contratação PNCP na cesta do mesmo item**. O fornecedor pode aparecer em outros itens ou contratações. CNPJ ausente/inválido exige revisão.

## Aplicação e revisão

Use **Importar guia final**, selecione o ZIP utilizado pela IA e seu guia. A prévia mostra cestas prontas, insuficiências e pendências, com mensagens copiáveis. Cada item é aplicado em uma transação curta. Reimportar não duplica cestas e não sobrescreve edições manuais.

São conferidos pacote, IDs, especificação atual, fatores, preço efetivo, faixa, desvio de até 25% e validação vigente das cestas. Permanecem os valores de origem e o truncamento, com duas casas ou quatro para medicamentos. Uma cópia do guia identificado por hash e um relatório da aplicação ficam junto ao pacote para auditoria.

**Limitação:** a validação local não comprova equivalência técnica por si só. A justificativa e o trecho tornam a escolha revisável. Nesta versão, seleções divergentes do filtro lexical ou da elegibilidade atual ficam pendentes; não se altera descrição, preço original ou regra global para contornar isso.

Documentos podem ser obtidos pelos comandos de evidências depois da seleção. Nenhuma etapa deste fluxo dispara anexos, PDFs ou OCR automaticamente.

## Desempenho e retomada

- Um leitor do banco, páginas limitadas e comandos preparados na inserção. Sem migração do esquema 34, novos índices ou mudanças no SQL da pesquisa.
- Candidatos e checkpoints em arquivo auxiliar junto ao ZIP. Depois de exportação durável concluída, o auxiliar é removido; em pausa/falha, é preservado.
- Memória limitada por páginas e lotes; interface recebe contadores agregados a cada 150 ms.
- Use **Retomar automação** após pausar/fechar. Se a base mudar, somente o item incompleto reinicia; itens concluídos conservam suas capturas datadas.
- Mudanças na especificação/filtros exigem nova coleta. Atualizações e outras operações concorrentes devem terminar antes da coleta.

Os testes incluem bases sintéticas com 450 itens. Tempos e consumo no HD antigo precisam ser medidos na máquina de destino; testes e compilação não constituem benchmark desse hardware.
