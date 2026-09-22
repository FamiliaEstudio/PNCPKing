# Atualização pelo GitHub

**Atualizar pelo GitHub** consulta as releases públicas de `FamiliaEstudio/PNCPKing` somente após o clique. A prévia separa o programa do pacote oficial de preços. Se houver executável novo, ele é conferido antes do encerramento e a importação confirmada continua depois da reinicialização sem outro download.

## Publicar o programa

1. Atualize `Version` em `src/PNCPKing.App/PNCPKing.App.csproj`, usando `X.Y.Z`.
2. Feche o PNCP King e execute `scripts/publish-windows.ps1` no PowerShell.
3. O script compila em Release, executa os testes e substitui `artifacts/win-x64/PNCPKing.exe`. Nenhum PDB ou banco deve ser publicado.
4. Publique `PNCPKing.exe` e `app-update.json` na release estável `vX.Y.Z`, marcada como **Latest**.

A versão do formato móvel v2 é `1.2.0` e o esquema correspondente é 29.

## Publicar os preços

No computador exportador, conclua **Atualizar** e use **Exportar atualizações PNCP**. A exportação contém hoje e os nove dias anteriores, mais contratações antigas cujo `global_updated_at` entrou nessa janela. CATMAT/CATSER e dados particulares não entram no arquivo.

Prepare a publicação com um único pacote:

```powershell
.\scripts\prepare-github-prices.ps1 `
  -UpdatePackage 'E:\Exportados\PNCP-atualizacoes-20260921.pncpupdate' `
  -OutputDirectory 'E:\Anexos PNCP' `
  -MinimumAppVersion '1.2.0'
```

O script aceita somente formato 2, esquema 29, janela exata de dez dias e arquivo menor que 2 GiB. Ele confere o registro de validação do exportador, descritores, tamanhos e SHA-256 dos blocos; não divide um pacote excessivo e não abre o banco real.

Crie ou atualize a release dedicada com tag **`precos`**, sem marcar **Latest**. Envie o `.pncpupdate` indicado e, por último, `prices-update.json`. O manifesto externo tem `format`, `publishedAt`, `minimumAppVersion`, `schema` e `update`; não possui `base` ou `cumulative`.

O download calcula o SHA-256 externo enquanto grava o arquivo. A importação confere cada SHA-256 interno durante a única extração necessária. O pacote completo do cache possui um marcador local de verificação e é removido após o sucesso.

## Preservação e retomada

O pacote independe da origem, `baseId` ou linhagem do banco e pode ser aplicado diretamente depois da restauração de um backup compatível. Recibos ficam no banco selecionado. Blocos já concluídos, inclusive de outra janela móvel com o mesmo digest, são ignorados antes da extração.

Cancelar ou encontrar erro reverte somente o bloco corrente. Blocos anteriores permanecem concluídos. Cotações, cestas, evidências, configurações e CATMAT/CATSER não são alterados. A importação também não consulta o PNCP nem cria pendência de revalidação.

O instalador do programa continua incorporado ao executável. Ele substitui somente o caminho canônico após o encerramento normal e preserva o executável anterior se a troca falhar.
