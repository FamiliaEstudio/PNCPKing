# PNCP King

[![Versão 1.2.50](https://img.shields.io/badge/vers%C3%A3o-1.2.50-28754D)](https://github.com/FamiliaEstudio/PNCPKing/releases/tag/v1.2.50)
![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4)
[![Compilação e testes no Windows](https://github.com/FamiliaEstudio/PNCPKing/actions/workflows/publish-windows.yml/badge.svg?branch=main)](https://github.com/FamiliaEstudio/PNCPKing/actions/workflows/publish-windows.yml)
[![Licença proprietária](https://img.shields.io/badge/licen%C3%A7a-propriet%C3%A1ria-70549B)](LICENSE)

O PNCP King é um aplicativo para Windows voltado à pesquisa de preços em compras públicas. Você consulta os valores unitários homologados no Portal Nacional de Contratações Públicas (PNCP), escolhe as referências de cada item e gera a planilha de avaliação, as tabelas do Termo de Referência e o relatório de evidências.

**[Baixar PNCPKing.exe para Windows de 64 bits](https://github.com/FamiliaEstudio/PNCPKing/releases/download/v1.2.50/PNCPKing.exe)**

## O que mudou na versão 1.2.50

- Se uma imagem de evidência falhar, a exportação continua. O relatório registra “Não foi possível obter a imagem pelo arquivo.” junto ao preço afetado. O item, o preço e a etapa da falha ficam identificados em **Opções → Diagnóstico → Logs de diagnóstico**.
- Assim que inclui duas imagens distintas de um preço, o programa passa à próxima referência sem abrir os PDFs restantes daquela contratação. Cada arquivo do relatório tem até 49 páginas e mantém juntas as evidências de um mesmo preço.
- As páginas continuam em 300 dpi. Para reaproveitar as imagens recentes, o programa mantém um cache de até 32 MiB em memória, sem gravá-lo no HD.

## Primeiros passos

O executável já inclui o .NET 8. Na primeira abertura, escolha a pasta onde quer guardar a base de preços. Depois de carregar os dados, você pode pesquisar sem conexão os preços das contratações publicadas nos últimos 11 meses.

- **Para começar com a base pronta**, abra **Atualizar pelo GitHub**, marque **Atualizar os preços** e confirme a importação do pacote do Google Drive. Em um PC já em uso, essa opção substitui a base pública e preserva suas cotações, cestas e evidências. [Guia de atualização](docs/github-updates.md)
- **Para coletar os dados diretamente do PNCP**, clique em **Calcular tamanho**, confira o espaço e o tempo estimados e selecione **Atualizar**. Você pode pausar a coleta e retomá-la depois.

Para atualizar o aplicativo, use **Atualizar pelo GitHub → Atualizar o programa**.

## Da pesquisa à cotação

1. Digite o que procura, ajuste o período, a localização ou a faixa de preço e clique em **Pesquisar**. Por exemplo, `"café torrado" -solúvel` busca a expressão entre aspas e exclui descrições com a palavra “solúvel”.
2. Clique em **Usar esta amostra em uma cotação** e escolha ou crie um projeto. Para começar com uma lista de itens, use **Cotações → Importar XLSX**, com um item por linha e sem cabeçalho. [Como preencher a planilha](docs/importacao-excel.md)
3. Revise a cesta sugerida para cada item ou monte a sua. Para incluir um preço de loja, use **Inserir preço da internet** na janela do item e registre a página e as capturas de tela do preço e do CNPJ. Ao terminar, confirme a cesta.
4. Em **Organizar Itens**, você pode ordenar por nome ou definir a ordem manualmente, arrastando um item ou digitando a posição final. Ao organizar por grupos e cotas, o programa respeita essa sequência dentro de cada grupo.

## Arquivos da cotação

Na aba **Cotações**, abra **Exportar/Importar**.

| Comando | O que você obtém |
| --- | --- |
| **Exportar somente Excel** | Planilha de Avaliação de Preços (`.xlsx`). Identifica preços excessivos ou inexequíveis e calcula a média ou a mediana dos válidos, conforme o método da cesta. |
| **Exportar Excel e evidências** | A mesma planilha e o relatório em PDF com as evidências de cada preço. |
| **Exportar Tabela 1.1 em Word** | Tabela editável com itens, unidades e quantidades. Em registro de preços, inclui a quantidade mínima por pedido. |
| **Exportar Tabela 9.1 em Word** | Tabela editável com o preço unitário adotado e o total por item ou cota. O valor estimado da contratação aparece também por extenso. |
| **Exportar pacote** | Uma cotação em `.pncpcotacao`, com suas cestas e evidências, para abrir em outro computador pelo comando **Importar pacote**. |

Para medicamentos, ative **Cotação de medicamentos — 4 casas decimais** no menu **Cotações** antes de confirmar as cestas.

## Compilar no Windows

Com o SDK do .NET 8 instalado, execute o script no PowerShell, a partir da raiz do repositório. Ele compila a solução em Release, executa os testes e gera `artifacts\win-x64\PNCPKing.exe`.

```powershell
.\scripts\publish-windows.ps1
```

## Licença e contato

O PNCP King é um projeto pessoal de Felipe Venturini Arcêncio, independente da Prefeitura Municipal de Ribeirão Preto. A Prefeitura tem autorização permanente de uso gratuito, inclusive se o programa passar a ser comercializado. Os modelos de documentos podem exibir o brasão municipal.

O código está disponível para consulta e avaliação. O uso do programa depende de autorização prévia e expressa do titular, nos termos da [licença proprietária](LICENSE).

Para tratar de licenciamento ou solicitar uma cópia atualizada da base de preços, escreva para [felipearcencio@gmail.com](mailto:felipearcencio@gmail.com).
