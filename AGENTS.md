# PNCP King — orientações permanentes do repositório

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
