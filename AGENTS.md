# PNCP King — orientações permanentes do repositório

## Princípios de implementação

- Prefira sempre a solução mais simples que satisfaça completamente o requisito.
- Não aumente o escopo da tarefa sem necessidade.
- Não refatore código não relacionado à solicitação atual.
- Não crie novas abstrações, classes, módulos ou camadas arquiteturais sem benefício concreto.
- Reutilize a arquitetura e os padrões já existentes no projeto.
- Evite soluções especulativas destinadas a necessidades futuras ainda inexistentes.
- Preserve compatibilidade com comportamentos existentes salvo instrução contrária.
- Antes de criar uma nova abstração, verifique se a solução direta é suficientemente clara e sustentável.
- Ao corrigir bugs, procure a causa raiz, mas limite as alterações ao necessário para corrigi-la.
- Quando houver duas soluções igualmente corretas, prefira a de menor complexidade.
- Não faça melhorias "aproveitando a oportunidade" fora do escopo solicitado.
- Caso identifique melhorias relevantes, mencione-as ao final sem implementá-las.

## Política de alterações

Este é um projeto iterativo já funcional.

Preserve comportamentos existentes salvo quando a tarefa exigir explicitamente
sua alteração.

Priorize alterações incrementais sobre reescritas.

Não refatore código apenas porque outra implementação seria mais elegante.

Não introduza abstrações, camadas ou dependências sem benefício concreto para
um requisito atual.

Não implemente melhorias adjacentes não solicitadas.
Caso encontre oportunidades relevantes fora do escopo, apenas relate-as.

Sempre que possível:
- reduza complexidade;
- reutilize componentes existentes;
- reduza código em vez de aumentá-lo;
- evite duplicação;
- preserve APIs e contratos internos existentes.

Mudanças arquiteturais devem justificar explicitamente por que a solução atual
é insuficiente.

O objetivo não é maximizar a sofisticação do código.
O objetivo é maximizar clareza, confiabilidade, desempenho e facilidade de
manutenção com a menor complexidade necessária.


## Distribuição Windows

- Deve existir somente uma distribuição publicada e somente um executável do PNCP King no repositório.
- O caminho canônico é `artifacts/win-x64/PNCPKing.exe`.
- Nunca criar pastas de publicação com data, sufixo, versão, `update`, `dynamic` ou nomes semelhantes.
- Toda nova publicação deve substituir o conteúdo do caminho canônico.
- Se o Windows bloquear a substituição porque o executável está aberto, interromper a publicação e pedir ao usuário que feche o processo. Não contornar o bloqueio criando outra pasta ou outro executável.
- Não incluir arquivos `.pdb` na distribuição final.
- Após publicar, verificar que há exatamente um arquivo `PNCPKing.exe` sob `artifacts/` e atualizar a documentação somente se o caminho canônico mudar.

## Validação

- Antes da publicação, compilar em Release e executar os testes relevantes.
- Preservar sempre o banco de dados escolhido pelo usuário; artefatos de publicação não devem conter nem manipular o banco real.
