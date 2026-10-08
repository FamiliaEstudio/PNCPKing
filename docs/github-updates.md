# Atualização pelo GitHub

**Atualizar pelo GitHub** consulta as releases públicas de `FamiliaEstudio/PNCPKing` somente após o clique. A prévia permite escolher **Atualizar o programa**, **Atualizar os preços** ou ambos, e mostra, com rolagem, as notas de cada versão do programa entre a instalada e a oferecida. Opções sem atualização disponível ficam desabilitadas. Se um pacote exigir uma versão mais recente, os preços só podem ser selecionados junto com o programa. Se o histórico estiver indisponível, a prévia avisa e ainda permite atualizar. Quando selecionado, o executável novo é baixado, conferido e instalado primeiro. Somente depois da reinicialização o novo programa baixa e importa os preços também selecionados na prévia. Escolher somente o programa não inicia a etapa de preços após o reinício. Uma falha no download dos preços não impede a atualização do programa. Selecionar somente os preços faz a importação diretamente, sem reiniciar.

## Publicar o programa

1. Atualize `Version` em `src/PNCPKing.App/PNCPKing.App.csproj`, usando `X.Y.Z`.
2. Escreva `docs/releases/vX.Y.Z.md` com um resumo breve em português, em tópicos, explicando as mudanças para o usuário. Esse texto será exibido na prévia de **Atualizar pelo GitHub**. Notas vazias ou somente um link para `Full Changelog` são recusadas.
3. Feche o PNCP King e execute `scripts/publish-windows.ps1` no PowerShell.
4. O script valida as notas, compila em Release, executa os testes e substitui `artifacts/win-x64/PNCPKing.exe`. Nenhum PDB ou banco deve ser publicado.
5. Envie as alterações para `main` com `[release]` na mensagem do commit. O fluxo automático publica `PNCPKing.exe` e `app-update.json` na release estável `vX.Y.Z`, marcada como **Latest**, usando `--notes-file docs/releases/vX.Y.Z.md`.
6. Confira os anexos e o texto publicado no GitHub. Em publicações manuais, use o mesmo arquivo de notas; o histórico completo pode complementar o resumo, mas nunca substituí-lo.

A versão do formato móvel v2 é `1.2.0` e o esquema correspondente é 29.

## Publicar os preços

No computador exportador, execute **Atualizar** até onde for possível e use **Exportar atualizações PNCP**. A exportação pode ser parcial: contém os dados oficiais já concluídos de hoje e dos 19 dias anteriores, mais contratações antigas cujo `global_updated_at` entrou nessa janela. CATMAT/CATSER e dados particulares não entram no arquivo.

Prepare a publicação com um único pacote:

```powershell
.\scripts\prepare-github-prices.ps1 `
  -UpdatePackage 'E:\Exportados\PNCP-atualizacoes-20260921.pncpupdate' `
  -OutputDirectory 'E:\Anexos PNCP' `
  -MinimumAppVersion '1.2.24'
```

O script aceita somente formato 2, esquema 29, janela exata de 10 ou 20 dias e arquivo menor que 2 GiB. Pacotes de 20 dias exigem o programa 1.2.24 ou posterior; pacotes anteriores de 10 dias continuam compatíveis. Ele confere o registro de validação do exportador, descritores, tamanhos e SHA-256 dos blocos; não divide um pacote excessivo e não abre o banco real. As verificações integrais SQLite são executadas exclusivamente pelo exportador, nunca pelo receptor.

Crie ou atualize a release dedicada com tag **`precos`**, sem marcar **Latest**. Envie o `.pncpupdate` indicado e, por último, `prices-update.json`. O manifesto externo tem `format`, `publishedAt`, `minimumAppVersion`, `schema` e `update`; não possui `base` ou `cumulative`.

O corpo da release de preços também deve conter um resumo breve em português, informando o período e o conteúdo atualizado.

O download calcula o SHA-256 externo enquanto grava o arquivo. A importação confere cada SHA-256 interno durante a única extração necessária. O pacote completo do cache possui um marcador local de verificação e é removido após o sucesso.

## Preservação e retomada

O pacote independe da origem, `baseId` ou linhagem do banco e pode ser aplicado diretamente depois da restauração de um backup compatível. Recibos ficam no banco selecionado. Blocos já concluídos, inclusive de outra janela móvel com o mesmo digest, são ignorados antes da extração.

A partir da versão 1.2.26, uma exportação concluída também registra o pacote no banco de origem, evitando que ele baixe e reimporte o próprio pacote. Exportações anteriores e bancos que receberam os dados por sincronização podem precisar de uma primeira conciliação; ter os dados não equivale a já ter o recibo desse pacote.

Cancelar ou encontrar erro reverte somente o bloco corrente. Blocos anteriores permanecem concluídos. Cotações, cestas, evidências, configurações e CATMAT/CATSER não são alterados. A importação também não consulta o PNCP nem cria pendência de revalidação.

O instalador do programa continua incorporado ao executável. Ele substitui somente o caminho canônico após o encerramento normal e preserva o executável anterior se a troca falhar.

Se uma versão antiga não reconheceu a janela de 20 dias na prévia, a instalação do programa continua disponível. Após esse reinício, o aplicativo atualizado consulta novamente somente o canal de preços e aplica o pacote compatível ao mesmo banco escolhido. Falhas de rede permitem tentar novamente em **Atualizar pelo GitHub**, preservando a atualização do programa e os blocos já concluídos.
