# Atualização pelo GitHub

**Atualizar pelo GitHub** consulta as releases públicas de `FamiliaEstudio/PNCPKing` somente após o clique. A prévia separa o programa do pacote oficial de preços e mostra, com rolagem, as notas de cada versão do programa entre a instalada e a oferecida. Se o histórico estiver indisponível, a prévia avisa e ainda permite atualizar. Se houver executável novo, ele é conferido antes do encerramento e a importação confirmada continua depois da reinicialização sem outro download.

## Publicar o programa

1. Atualize `Version` em `src/PNCPKing.App/PNCPKing.App.csproj`, usando `X.Y.Z`.
2. Escreva `docs/releases/vX.Y.Z.md` com um resumo breve em português, em tópicos, explicando as mudanças para o usuário. Esse texto será exibido na prévia de **Atualizar pelo GitHub**. Notas vazias ou somente um link para `Full Changelog` são recusadas.
3. Feche o PNCP King e execute `scripts/publish-windows.ps1` no PowerShell.
4. O script valida as notas, compila em Release, executa os testes e substitui `artifacts/win-x64/PNCPKing.exe`. Nenhum PDB ou banco deve ser publicado.
5. Envie as alterações para `main` com `[release]` na mensagem do commit. O fluxo automático publica `PNCPKing.exe` e `app-update.json` na release estável `vX.Y.Z`, marcada como **Latest**, usando `--notes-file docs/releases/vX.Y.Z.md`.
6. Confira os anexos e o texto publicado no GitHub. Em publicações manuais, use o mesmo arquivo de notas; o histórico completo pode complementar o resumo, mas nunca substituí-lo.

A versão do formato móvel v2 é `1.2.0` e o esquema correspondente é 29.

## Publicar os preços

No computador exportador, execute **Atualizar** até onde for possível e use **Exportar atualizações PNCP**. A exportação pode ser parcial: contém os dados oficiais já concluídos de hoje e dos nove dias anteriores, mais contratações antigas cujo `global_updated_at` entrou nessa janela. CATMAT/CATSER e dados particulares não entram no arquivo.

Prepare a publicação com um único pacote:

```powershell
.\scripts\prepare-github-prices.ps1 `
  -UpdatePackage 'E:\Exportados\PNCP-atualizacoes-20260921.pncpupdate' `
  -OutputDirectory 'E:\Anexos PNCP' `
  -MinimumAppVersion '1.2.0'
```

O script aceita somente formato 2, esquema 29, janela exata de dez dias e arquivo menor que 2 GiB. Ele confere o registro de validação do exportador, descritores, tamanhos e SHA-256 dos blocos; não divide um pacote excessivo e não abre o banco real.

Crie ou atualize a release dedicada com tag **`precos`**, sem marcar **Latest**. Envie o `.pncpupdate` indicado e, por último, `prices-update.json`. O manifesto externo tem `format`, `publishedAt`, `minimumAppVersion`, `schema` e `update`; não possui `base` ou `cumulative`.

O corpo da release de preços também deve conter um resumo breve em português, informando o período e o conteúdo atualizado.

O download calcula o SHA-256 externo enquanto grava o arquivo. A importação confere cada SHA-256 interno durante a única extração necessária. O pacote completo do cache possui um marcador local de verificação e é removido após o sucesso.

## Preservação e retomada

O pacote independe da origem, `baseId` ou linhagem do banco e pode ser aplicado diretamente depois da restauração de um backup compatível. Recibos ficam no banco selecionado. Blocos já concluídos, inclusive de outra janela móvel com o mesmo digest, são ignorados antes da extração.

Cancelar ou encontrar erro reverte somente o bloco corrente. Blocos anteriores permanecem concluídos. Cotações, cestas, evidências, configurações e CATMAT/CATSER não são alterados. A importação também não consulta o PNCP nem cria pendência de revalidação.

O instalador do programa continua incorporado ao executável. Ele substitui somente o caminho canônico após o encerramento normal e preserva o executável anterior se a troca falhar.
