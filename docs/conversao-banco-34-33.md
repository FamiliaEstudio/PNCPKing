# Conversor PNCP King v34 → v33

Ferramenta externa para permitir abrir uma **cópia** do banco v34 no PNCP King 1.2.27 (esquema 33).

## Utilização

1. Feche o PNCP King e demais processos que utilizem o banco.
2. Preserve o banco original e eventuais arquivos `pncpking.db-wal` e `pncpking.db-shm`. Nunca apague o WAL para tentar resolver a migração.
3. Instale Python 3, se necessário. Arraste `pncpking.db` sobre `scripts/converter-banco-34-33.cmd`, ou execute:

```powershell
py -3 scripts\downgrade_schema_34_to_33.py "C:\Dados\pncpking.db" "D:\Recuperacao\pncpking-v33.db"
```

4. A ferramenta cria uma cópia SQLite consistente, incluindo dados confirmados no WAL, e altera somente `schema_info.version` na cópia. Ela **não** modifica o banco original nem sobrescreve destinos existentes.
5. Teste a cópia `pncpking-v33.db` com o aplicativo 1.2.27, numa pasta de dados separada, com o nome `pncpking.db`. Nunca associe os arquivos WAL/SHM do banco original ao banco convertido.

## Por que essa conversão é suficiente?

A migração v34 acrescentou apenas `last_contract_id` e `processed_contracts` na tabela `official_update_chunks`, substituiu os gatilhos `contracts_fts_update` e `items_fts_update` por versões que só disparam quando `search_text` muda e registrou versão 34 em `schema_info`. Não há mudança destrutiva das tabelas de cotações. O aplicativo 1.2.27 rejeita v34 ao inicializar, mas não precisa remover fisicamente essas colunas para trabalhar com uma cópia cujo marcador seja 33.

Por prudência, a ferramenta conserva essas duas colunas extras e os gatilhos novos: não faz `VACUUM`, `PRAGMA integrity_check` ou reconstrução FTS. Confirma versão, estrutura e as contagens das três tabelas principais de cotações na cópia.

## Limitações

- É uma **conversão de compatibilidade**, não uma reconstrução física idêntica ao esquema v33.
- **Não** recupera páginas já corrompidas nem converte backups `.pncpking`.
- **Não** combina automaticamente cotações existentes com um banco de preços PNCP mais recente.
- O arquivo resultante pode precisar de espaço equivalente ao tamanho completo do SQLite mais 512 MiB.
- Preserve também as pastas `document-cache` e `internet-evidence` ao mover a pasta de dados: não são incorporadas ao arquivo .db.
- Testes reais no Windows com um banco de produção devem anteceder qualquer substituição definitiva.
