# Atualização pelo GitHub

**Atualizar pelo GitHub**, ao lado de **Opções**, consulta as releases públicas de
`FamiliaEstudio/PNCPKing`. A prévia separa o programa dos preços, informa o tamanho
dos downloads e pede uma única confirmação. O botão **Atualizar** em manutenção
continua consultando o PNCP diretamente.

Somente o clique consulta o GitHub. Se houver programa novo, os arquivos são
baixados e conferidos antes de encerrar e reabrir o aplicativo. A nova versão
continua a importação já confirmada, sem novos downloads na abertura. Preços
podem ser publicados e importados mesmo quando o executável não mudou.

## Publicar o programa

1. Atualize `Version` em `src/PNCPKing.App/PNCPKing.App.csproj`, usando `X.Y.Z`.
2. Feche o PNCP King e execute `scripts/publish-windows.ps1` no PowerShell.
   O parâmetro opcional `-Version X.Y.Z` permite definir a versão do build.
3. O script compila em Release, executa os testes e substitui a distribuição
   canônica em `artifacts/win-x64/PNCPKing.exe`. Também gera `app-update.json`.
4. Crie uma release em rascunho com tag `vX.Y.Z`, envie **os dois arquivos**,
   confira tamanhos e hashes dos anexos e só então publique a release estável
   com a marcação **Latest**. Não envie os ZIPs de código-fonte como programa.

A primeira versão com o botão é `1.1.0` e precisa ser instalada manualmente.
Não publicar builds diferentes com a mesma versão; aumentar a versão a cada
mudança distribuída. Pré-releases, versões iguais e inferiores são ignoradas.

`app-update.json` usa este contrato (os números e hashes são gerados pelo script):

```json
{
  "format": 1,
  "version": "1.1.0",
  "platform": "win-x64",
  "schema": 28,
  "file": { "name": "PNCPKing.exe", "size": 123, "sha256": "SHA256_DO_EXECUTAVEL" }
}
```

## Publicar os preços

Exporte a **base inicial** uma vez pelo comando **Exportar atualizações PNCP**
do programa e conserve esse arquivo. As exportações posteriores da mesma origem
e linhagem são cumulativas: basta publicar a base inicial e o último cumulativo.
O arquivo contém dados oficiais relacionados aos preços, incluindo contratações,
itens e catálogo; não contém cotações, cestas ou configurações particulares.

```powershell
.\scripts\prepare-github-prices.ps1 `
  -BasePackage 'E:\Exportados\base.pncpupdate' `
  -CumulativePackage 'E:\Exportados\mais-recente.pncpupdate' `
  -OutputDirectory 'E:\Anexos PNCP' `
  -MinimumAppVersion '1.1.0'
```

Na primeira publicação, omita `-CumulativePackage`. O script lê somente os
pacotes fornecidos, valida a estrutura e o SHA-256 interno e prepara os anexos.
Ele não abre nem exporta o banco real. Arquivos menores que 2 GiB são copiados
como `.pncpupdate`; os demais são divididos em partes de 1 GiB. O limite do
GitHub é de arquivos **menores que 2 GiB** e até 1.000 anexos por release.
[Referência do GitHub](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases#storage-and-bandwidth-quotas).

Crie a release dedicada com tag **`precos`**, sem marcar **Latest**. Envie todos
os arquivos listados no manifesto gerado, confira os anexos e envie
**`prices-update.json` por último**. Em publicações seguintes, envie os novos
anexos primeiro e só então substitua esse manifesto. Não altere os bytes de um
anexo existente nem reutilize sua identidade para outro conteúdo. A release
`precos` precisa ser criada sem a imutabilidade de releases habilitada: uma
release imutável não permite substituir seus anexos depois de publicada.
[Imutabilidade de releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases).

Não remova a base inicial referenciada. Preserve também arquivos de publicações
anteriores enquanto puderem existir downloads em andamento. O script não exclui
anexos remotos; ao chegar ao limite de anexos, remova manualmente os antigos não
referenciados, considerando esses downloads.

`prices-update.json` contém `format`, `publishedAt`, `minimumAppVersion`,
`schema`, `base` e `cumulative` (opcional). Cada pacote contém:

- `manifest`: os mesmos campos do manifesto interno do `.pncpupdate`, inclusive
  `baseId`, `origin`, `revision`, `initialBase`, `packageId`, `sha256` e `units`;
- `expandedSize`: tamanho do `updates.db` extraído;
- `download`: `size`, `sha256` do arquivo completo e `parts`, a lista **ordenada**
  de anexos com `name`, `size` e `sha256`. Um arquivo inteiro tem uma única parte.

O SHA-256 interno identifica `updates.db`; o de `download` identifica o ZIP
`.pncpupdate`. São valores diferentes. A base e o cumulativo devem ter a mesma
linhagem, origem e esquema. Após uma mudança incompatível de esquema, publique
uma nova base e informe a versão mínima compatível do programa.

## Preservação e retomada

O banco escolhido continua no mesmo lugar. Recibos de importação são consultados
nesse banco, portanto atualizar um banco não marca outros como atualizados.
Quando faltar a base, ela será incluída na prévia. Uma troca de linhagem exige
concordância explícita e preserva dados particulares e versões oficiais mais
recentes. Permanecem as regras de retenção de 11 meses e de revalidação de conflitos.

Downloads usam `%LOCALAPPDATA%\PNCP King\updates`. Partes completas verificadas
são reaproveitadas após interrupções. Cancelar a importação conserva os lotes
concluídos; outro clique retoma pelo último pacote disponível. Os pacotes completos
importados com sucesso são removidos do cache. Restos de operações interrompidas
podem ser excluídos dessa pasta com o programa fechado, se não for necessária retomada.

O script de instalação vem incorporado ao aplicativo; o GitHub fornece somente
o executável e dados. A substituição ocorre após o encerramento normal, no mesmo
caminho, com arquivo temporário `.new` e recuperação `.bak`. O instalador não
manipula o banco. Falhas de substituição preservam ou restauram o executável
anterior; depois de abrir a nova versão, não há downgrade automático, pois ela
pode ter migrado o banco. Falha posterior dos preços não desfaz o programa instalado.

Se a pasta não permitir escrita, a atualização não encerra o programa. Se outro
processo bloquear o executável, o instalador informa a falha e pede seu fechamento;
não cria outra pasta de instalação. Os detalhes da falha ficam no arquivo
`*.install.json.result` do cache e os erros do fluxo aparecem nos logs existentes.

## Validação da versão 1.1.0

Compilação Release sem avisos ou erros; suíte completa com **666 testes aprovados**,
incluindo **41 verificações relacionadas ao GitHub**. O transporte foi testado com
HTTP simulado; o instalador foi executado no Windows sobre executáveis descartáveis,
incluindo substituição, bloqueio, corrupção, alteração durante o encerramento e
cancelamento. A prévia WPF também foi verificada quanto à concordância de troca
de base e ao cancelamento. Nenhum banco do usuário foi aberto nessas verificações.

Relatórios locais: `artifacts/github-validation/`. A distribuição final contém
somente `PNCPKing.exe` e `app-update.json`. A publicação das releases e de um pacote
de preços real no GitHub continua sendo feita pelo procedimento acima.
