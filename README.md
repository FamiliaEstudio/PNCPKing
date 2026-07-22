# PNCP King

Aplicativo desktop Windows para manter um índice local dos últimos 365 dias do PNCP e pesquisar preços homologados por item, sem baixar documentos ou PDFs.

## Executável pronto

A distribuição autocontida mais recente para Windows x64 está em `artifacts\win-x64\PNCPKing.exe`. Ela inclui o runtime do .NET 8 e, portanto, não exige instalação separada do .NET. O executável tem aproximadamente 157 MiB; o banco nacional será criado somente na pasta escolhida pelo usuário.

## O que está implementado

- prévia obrigatória com quantidade exata, estimativa de rede/banco/cache, tempo e espaço livre;
- recálculo da prévia e confirmação explícita imediatamente antes da carga nacional;
- sincronização por modalidade e período, com checkpoint estruturado, pausa, cancelamento e retomada;
- manutenção automática enquanto o aplicativo está aberto, preenchendo primeiro os dias novos e as lacunas do último ano;
- barra de cobertura com 365 segmentos, do dia mais antigo ao mais recente, e estados ausente, parcial, baixando, completo e falha;
- remoção de contratações vencidas somente depois que a nova borda da janela estiver comprovadamente completa;
- atualização por `dataAtualizacaoGlobal` com sobreposição de 48 horas;
- SQLite em WAL com FTS5, pesquisa sem acentos, por palavras, frases e prefixos;
- pesquisa em duas etapas: primeiro pelo objeto da contratação e depois pela descrição dos itens, mostrando somente os itens compatíveis;
- filtros `Todos`, `Cidades Próximas`, `Sudeste` e UF, períodos de 7 a 365 dias ou personalizados e ordenação por relevância, data ou proximidade;
- catálogo embutido de Ribeirão Preto e das 49 sedes municipais mais próximas, calculadas em linha reta com Haversine a partir da edição 2022 do IBGE;
- primeira página de até 50 itens com preços consultada automaticamente e preparação da página seguinte durante a navegação;
- controle de 1 a 100 lotes adicionais, com até 50 consultas de resultado por lote e confirmação acima de 500 itens;
- banco temporário separado para os preços automáticos, apagado ao pesquisar novamente, fechar ou reabrir após encerramento inesperado;
- cache permanente separado para a atualização manual de uma contratação;
- faixa inclusiva de preço unitário homologado, aplicada somente a resultados ativos e sem conversão entre unidades;
- medição por sessão de chamadas, bytes, duração e médias de listas de itens e resultados;
- agendador único com no máximo duas chamadas ao PNCP e prioridade para ações visíveis do usuário;
- distinção entre preço encontrado, resultado cancelado, item sem resultado, pendência e falha;
- invalidação de listas e preços permanentes quando a contratação muda;
- link para a página oficial da contratação, sem baixar PDFs ou metadados documentais;
- backup/importação validado no formato `.pncpking`, incluindo migração segura de backups antigos do próprio PNCP King.

O aplicativo não fez a carga nacional durante a compilação. A medição atual será feita pela própria interface e nenhum download começará sem a confirmação dos números e da margem adicional de 20% de espaço livre.

## Requisitos para desenvolvimento

- .NET SDK 8;
- Windows 10/11 para executar a interface WPF;
- conexão com a internet para sincronização e consulta de preços.

## Compilar e testar

```powershell
dotnet restore PNCPKing.sln
dotnet build PNCPKing.sln --configuration Release
dotnet test tests\PNCPKing.Tests\PNCPKing.Tests.csproj --configuration Release
```

Para gerar uma distribuição Windows autocontida:

```powershell
.\scripts\publish-windows.ps1
```

O executável será criado em `artifacts\win-x64`.

A suíte automatizada cobre pesquisa por objeto e item, geografia, faixa de preço, valores homologados, rejeição de valores estimados, múltiplos resultados, falha parcial, `429`, timeout, cobertura diária, retomada por checkpoint e validação de backups.

## Uso

1. Na primeira abertura, escolha a pasta que armazenará o banco.
2. Clique em **Calcular tamanho** e aguarde a contagem das modalidades.
3. Revise o volume, o espaço e a duração estimados.
4. Clique em **Baixar/atualizar dados** e confirme os números exibidos.
5. Digite o objeto, escolha geografia, período e ordenação e clique em **Pesquisar**.
6. A grade principal manterá somente os itens cuja descrição também corresponde ao texto e consultará automaticamente os preços dos primeiros 50 itens elegíveis.
7. Continue rolando ou use **Carregar próximos 50**. Para ampliar a amostra, informe de 1 a 100 em **Disparar lotes**.
8. Use os campos de preço mínimo/máximo para filtrar o valor unitário homologado ativo.
9. Para manter uma contratação no cache permanente, selecione-a na segunda aba e use **Buscar/atualizar todos os preços**.
10. Use **Abrir contratação no PNCP** para acessar a página oficial e baixar documentos manualmente, se houver interesse.

O total homologado geral mostrado na grade de contratações é apenas um resumo. Os preços dos itens vêm exclusivamente dos campos de resultado homologado do PNCP; valores estimados nunca são usados como substitutos.

Após a primeira carga autorizada, o programa verifica periodicamente se o calendário avançou ou se há lacunas. Ele baixa primeiro as publicações ausentes, faz a atualização global com sobreposição de 48 horas e só então ajusta a borda antiga da janela de 365 dias. Uma falha nunca antecipa a exclusão de registros.

O estudo de custo para uma eventual carga nacional de itens e resultados está em `docs/price-load-study.md`. Por exigir milhões de chamadas adicionais, essa carga permanece sob demanda nesta versão.
