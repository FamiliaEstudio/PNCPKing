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


## Integridade de backups — regra não negociável

- `PRAGMA integrity_check` de arquivos `.pncpking` deve ser executado exclusivamente no computador emissor, durante a exportação.
- O computador receptor nunca deve executar `PRAGMA integrity_check` sobre um backup importado, inclusive antes da instalação, depois de migração de esquema, após retenção, durante ativação ou durante recuperação.
- No receptor são permitidas somente verificações de transporte e estrutura que não façam varredura integral: SHA-256/hashes, manifesto, versão interna do esquema e referências/evidências.
- Migrações de esquema não autorizam nova verificação integral no receptor. O sucesso da transação de migração é a fronteira de recuperação; depois dela, confirme somente a versão interna esperada.
- Backup legado sem declaração de validação integral na origem deve ser recusado e reexportado por uma versão compatível. Não substitua essa recusa por uma verificação integral local.
- Não reintroduza verificações integrais no receptor como medida adicional de segurança, robustez ou defesa em profundidade sem alteração explícita desta regra pelo usuário.

## Distribuição Windows

- Deve existir somente uma distribuição publicada e somente um executável do PNCP King no repositório.
- O caminho canônico é `artifacts/win-x64/PNCPKing.exe`.
- Nunca criar pastas de publicação com data, sufixo, versão, `update`, `dynamic` ou nomes semelhantes.
- Toda nova publicação deve substituir o conteúdo do caminho canônico.
- Se o Windows bloquear a substituição porque o executável está aberto, interromper a publicação e pedir ao usuário que feche o processo. Não contornar o bloqueio criando outra pasta ou outro executável.
- Não incluir arquivos `.pdb` na distribuição final.
- Após publicar, verificar que há exatamente um arquivo `PNCPKing.exe` sob `artifacts/` e atualizar a documentação somente se o caminho canônico mudar.

## Notas das releases no GitHub

- Toda release deve conter, no corpo da publicação, um descritivo breve em português do que mudou e do efeito para o usuário.
- Nas releases do programa, registrar o resumo em tópicos no arquivo `docs/releases/vX.Y.Z.md` correspondente à versão e publicar esse conteúdo com `--notes-file`.
- Nunca publicar somente um link para `Full Changelog`, uma lista automática de commits ou notas vazias. Links para o histórico podem complementar o resumo, mas não substituí-lo.
- A prévia de **Atualizar pelo GitHub** lê o corpo das releases; o resumo deve ser compreensível diretamente nessa janela, sem depender de abrir links.
- Antes de concluir a publicação, conferir no GitHub a versão, os anexos e o corpo da release. Aplicar a mesma exigência de descrição às publicações do canal de preços.

## Validação

- Antes da publicação, compilar em Release e executar os testes relevantes.
- Preservar sempre o banco de dados escolhido pelo usuário; artefatos de publicação não devem conter nem manipular o banco real.
