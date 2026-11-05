# Importação de itens pelo Excel

Use um arquivo `.xlsx`. O PNCP King lê a primeira aba visível, a partir da linha 1, **sem linha de cabeçalho**, com um item por linha. Planilhas antigas A:G ou A:H continuam funcionando. A planilha de avaliação de preços exportada pelo programa não é uma planilha de entrada.

| Coluna | Informação | Preenchimento |
| --- | --- | --- |
| A | Pesquisa restritiva | Obrigatória. Use `+` para separar pesquisas independentes, como na pesquisa normal. |
| B | Descrição de saída | Nome do item, obrigatório; suas maiúsculas e minúsculas serão mantidas no Word. |
| C | Quantidade total | Inteiro maior que zero, obrigatório. |
| D | Unidade | Unidade de medida, obrigatória. |
| E | Faixa mínima de preço | Opcional; não é quantidade mínima por pedido. |
| F | Faixa máxima de preço | Opcional; deve ser igual ou maior que E quando ambas forem preenchidas. |
| G | Páginas locais por prompt | Inteiro de 1 a 100; cada expressão separada por `+` pode ler até G páginas de 50 preços. |
| H | Número de preços na cesta | Inteiro de 3 a 10; vazio usa 3. |
| I | CATMAT | Opcional. Formate a célula como **Texto** antes de digitar para preservar zeros iniciais. |
| J | Quantidade mínima por pedido | Opcional; quantidade nominal inteira maior que zero, **não percentual**. Vazio usa o percentual escolhido na exportação Word. |
| K | Pesquisas intermediárias | Opcional; vários prompts separados por `+`. |
| L | Pesquisas amplas | Opcional; vários prompts separados por `+`. |

O `+` reúne os resultados das pesquisas independentes e elimina preços repetidos. Para exigir duas palavras no mesmo resultado, escreva-as juntas com espaço: `café torrado`. Aspas, unidades, exclusões e aproximações continuam seguindo a sintaxe da pesquisa normal; o `+` dentro de uma frase entre aspas ou de um bloco `C:(...)` não separa prompts.

Exemplo: A = `café torrado 500 g`; K = `café torrado + café moído`; L = `café + bebida café`. Todos os prompts preenchidos são pesquisados. A cesta recomendada dá prioridade a uma cesta completa e regular formada apenas por preços do restritivo; se isso não for possível, admite intermediários e, por último, amplos. Dentro do nível, preserva a preferência pelos preços mais restritos e aplica os pesos já configurados. As outras referências permanecem disponíveis para revisão. Quando não existe cesta completa e regular, as alternativas continuam identificadas com seus avisos; a prioridade de prompt não transforma uma cesta irregular em regular.

Cada item é processado em segundo plano, com uma leitura por vez e páginas de até 50 preços. O progresso aparece na aba Cotações e desaparece ao terminar, cancelar ou falhar. Use **Automação → Cancelar automação** para interromper; **Retomar automação** preserva os preços já salvos e não repete itens concluídos. Ao finalizar, o programa monta as cestas e exporta somente o Excel. Para coletar os documentos depois, use **Exportar Excel e evidências**.

Na janela do item, **Pesquisar nos preços coletados** filtra todos os candidatos já vinculados ao item, inclusive os que estão fora da cesta. **Limpar** restaura a visualização; nenhuma dessas ações altera a composição das cestas. Na linha do preço, **Ações → Copiar preço(s) para outra cesta** permite escolher o destino ou criar uma cesta manual. Uma cesta automática de destino recebe uma cópia manual para edição; a origem é preservada. Divisores e conversões acompanham os preços novos, sem substituir ajustes de preços já presentes no destino.

Exemplo de uma linha de dados, nas colunas A:J:

| A | B | C | D | E | F | G | H | I | J |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| café | Café Premium | 100 | pacote | | | 2 | 3 | 001234 | 7 |

O programa mostra CATMAT e quantidade mínima na prévia. Erros de preenchimento indicam a aba, a linha e a célula a corrigir. A importação não consulta o catálogo para validar o código informado.

## Edição dos valores

Use **Editar dados** na janela do item para alterar ou limpar quantidade e unidade, inclusive de itens importados do Excel. A quantidade deve ser inteira positiva ou ficar vazia. Pause pesquisas e automação antes de editar. Preços, cestas, evidências e pesquisas são preservados; alterações efetivas exigem reconfirmar a cesta e, havendo grupos, reorganizar os itens antes de exportar. Salvar os mesmos valores mantém a confirmação.

Na janela de qualquer item, inclusive importado, use **CATMAT e mínimo**. O CATMAT informado tem prioridade no Word; limpar esse campo permite usar o CATMAT já vinculado pela busca do catálogo. O vínculo existente não é apagado. Sem código informado ou CATMAT vinculado, a célula ficará vazia; um vínculo CATSER não preencherá a coluna CATMAT.

A quantidade mínima importada em J é um valor fixo. Uma edição posterior substitui esse valor. Limpar o campo permite que o percentual da próxima exportação volte a ser aplicado. Essas alterações preservam cestas confirmadas e a organização dos itens.

## Exportação da Tabela 1.1

Em **Cotações → Exportar/Importar → Exportar Tabela 1.1 em Word**, escolha o local do descritivo: item 3.2, item 5, Memorial Descritivo ou outro número, como 7.1. A janela mostra a frase que acompanhará o nome de cada item.

Selecione se é Registro de Preços. Quando for, informe o percentual geral maior que zero e até 100%, que será aplicado aos itens sem mínimo nominal. O mínimo é arredondado para cima: 100 unidades com 10% resultam em 10; 101 unidades com 10% resultam em 11. No exemplo da planilha acima, prevalece o mínimo nominal de 7.

O cálculo percentual usa a quantidade total original do item. Se 100 unidades estiverem divididas em cotas de 75 e 25, com percentual de 10%, ambas terão mínimo 10. O mínimo nominal também é repetido nas cotas. Se algum mínimo superar a quantidade de uma linha/cota, a exportação será interrompida indicando o item; corrija ou limpe o mínimo nominal, ou reduza o percentual. Valores não serão reduzidos automaticamente.

Sem Registro de Preços, a coluna de quantidade mínima será removida. Sem grupos, a coluna de grupo também será removida. Havendo grupos, use **Organizar Itens** antes de exportar; grupos e itens conservarão os números da organização, com células de grupo mescladas. Itens avulsos permanecerão com o grupo vazio.

As opções escolhidas valem somente para aquela exportação. Os mínimos calculados não substituem os valores cadastrados. O arquivo `.docx` pode ser editado no Word e sua geração não exige Word instalado.
