# Importação de itens pelo Excel

Use um arquivo `.xlsx`. O PNCP King lê a primeira aba visível, a partir da linha 1, **sem linha de cabeçalho**, com um item por linha. Planilhas antigas A:G ou A:H continuam funcionando. A planilha de avaliação de preços exportada pelo programa não é uma planilha de entrada.

| Coluna | Informação | Preenchimento |
| --- | --- | --- |
| A | Pesquisa | Expressão de pesquisa do item, obrigatória. |
| B | Descrição de saída | Nome do item, obrigatório; suas maiúsculas e minúsculas serão mantidas no Word. |
| C | Quantidade total | Inteiro maior que zero, obrigatório. |
| D | Unidade | Unidade de medida, obrigatória. |
| E | Faixa mínima de preço | Opcional; não é quantidade mínima por pedido. |
| F | Faixa máxima de preço | Opcional; deve ser igual ou maior que E quando ambas forem preenchidas. |
| G | Páginas locais | Inteiro de 1 a 100; cada página reúne até 50 preços. |
| H | Número de preços na cesta | Inteiro de 3 a 10; vazio usa 3. |
| I | CATMAT | Opcional. Formate a célula como **Texto** antes de digitar para preservar zeros iniciais. |
| J | Quantidade mínima por pedido | Opcional; quantidade nominal inteira maior que zero, **não percentual**. Vazio usa o percentual escolhido na exportação Word. |

Exemplo de uma linha de dados, nas colunas A:J:

| A | B | C | D | E | F | G | H | I | J |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| café | Café Premium | 100 | pacote | | | 2 | 3 | 001234 | 7 |

O programa mostra CATMAT e quantidade mínima na prévia. Erros de preenchimento indicam a aba, a linha e a célula a corrigir. A importação não consulta o catálogo para validar o código informado.

## Edição dos valores

Na janela de qualquer item, inclusive importado, use **CATMAT e mínimo**. O CATMAT informado tem prioridade no Word; limpar esse campo permite usar o CATMAT já vinculado pela busca do catálogo. O vínculo existente não é apagado. Sem código informado ou CATMAT vinculado, a célula ficará vazia; um vínculo CATSER não preencherá a coluna CATMAT.

A quantidade mínima importada em J é um valor fixo. Uma edição posterior substitui esse valor. Limpar o campo permite que o percentual da próxima exportação volte a ser aplicado. Essas alterações preservam cestas confirmadas e a organização dos itens.

## Exportação da Tabela 1.1

Em **Cotações → Exportar/Importar → Exportar Tabela 1.1 em Word**, escolha o local do descritivo: item 3.2, item 5, Memorial Descritivo ou outro número, como 7.1. A janela mostra a frase que acompanhará o nome de cada item.

Selecione se é Registro de Preços. Quando for, informe o percentual geral maior que zero e até 100%, que será aplicado aos itens sem mínimo nominal. O mínimo é arredondado para cima: 100 unidades com 10% resultam em 10; 101 unidades com 10% resultam em 11. No exemplo da planilha acima, prevalece o mínimo nominal de 7.

O cálculo percentual usa a quantidade total original do item. Se 100 unidades estiverem divididas em cotas de 75 e 25, com percentual de 10%, ambas terão mínimo 10. O mínimo nominal também é repetido nas cotas. Se algum mínimo superar a quantidade de uma linha/cota, a exportação será interrompida indicando o item; corrija ou limpe o mínimo nominal, ou reduza o percentual. Valores não serão reduzidos automaticamente.

Sem Registro de Preços, a coluna de quantidade mínima será removida. Sem grupos, a coluna de grupo também será removida. Havendo grupos, use **Organizar Itens** antes de exportar; grupos e itens conservarão os números da organização, com células de grupo mescladas. Itens avulsos permanecerão com o grupo vazio.

As opções escolhidas valem somente para aquela exportação. Os mínimos calculados não substituem os valores cadastrados. O arquivo `.docx` pode ser editado no Word e sua geração não exige Word instalado.
