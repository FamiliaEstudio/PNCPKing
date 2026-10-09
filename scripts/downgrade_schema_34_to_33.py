#!/usr/bin/env python3
"""Cria uma cópia do banco PNCP King v34 compatível com o aplicativo v33.

Uso: py -3 pncpking_schema34_to_33.py ORIGEM.db DESTINO.db
Não altera a origem, não remove o WAL e não faz varredura integral de integridade.
"""

import argparse
from contextlib import closing
import os
from pathlib import Path
import shutil
import sqlite3
import sys
from uuid import uuid4

ORIGIN_VERSION = 34
TARGET_VERSION = 33
QUOTATION_TABLES = ("quotation_projects", "quotation_lines", "quotation_references")


def read_version(connection):
    row = connection.execute("SELECT version FROM schema_info WHERE id = 1").fetchone()
    if row is None:
        raise ValueError("Falta o registro de versão em schema_info.")
    return row[0]


def check_34_shape(connection):
    columns = {row[1] for row in connection.execute("PRAGMA table_info(official_update_chunks)")}
    required = {"last_contract_id", "processed_contracts"}
    if not required.issubset(columns):
        raise ValueError(
            "Esquema 34 incompleto ou incompatível: faltam colunas de official_update_chunks: "
            + ", ".join(sorted(required - columns))
        )
    for table in QUOTATION_TABLES:
        row = connection.execute(
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name=?", (table,)
        ).fetchone()
        if row is None:
            raise ValueError(f"Tabela essencial de cotações ausente: {table}.")


def quotation_counts(connection):
    # Tabelas locais pequenas; não consulta tabelas grandes de preços do PNCP.
    return {
        table: connection.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]
        for table in QUOTATION_TABLES
    }


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Cria uma cópia compatível com PNCP King 1.2.27 (esquema 33) a partir do banco 34."
    )
    parser.add_argument("origem", type=Path, help="Arquivo pncpking.db de esquema 34")
    parser.add_argument("destino", type=Path, help="Novo arquivo .db (não pode existir)")
    args = parser.parse_args(argv)

    source = args.origem.resolve()
    target = args.destino.resolve()
    if not source.is_file():
        parser.error(f"Banco de origem não encontrado: {source}")
    if source == target:
        parser.error("Origem e destino precisam ser arquivos diferentes.")
    if target.exists() or Path(str(target) + "-wal").exists() or Path(str(target) + "-shm").exists():
        parser.error(f"O destino já existe ou tem WAL associado: {target}")
    if not target.parent.is_dir():
        parser.error(f"A pasta de destino não existe: {target.parent}")

    temporary = target.with_name(target.name + ".partial-" + uuid4().hex)
    try:
        with source.open("rb") as file:
            if file.read(16) != b"SQLite format 3\x00":
                raise ValueError("A origem não parece ser um arquivo SQLite; pacotes .pncpking não são aceitos.")
        print("Feche o PNCP King antes de continuar. A origem será aberta somente para leitura.", flush=True)
        with closing(sqlite3.connect(source.as_uri() + "?mode=ro", uri=True, timeout=10)) as original:
            version = read_version(original)
            if version != ORIGIN_VERSION:
                raise ValueError(f"Versão encontrada: {version}; esta ferramenta aceita somente v34.")
            check_34_shape(original)
            size = original.execute("PRAGMA page_count").fetchone()[0] * original.execute(
                "PRAGMA page_size"
            ).fetchone()[0]
            minimum_free = size + 512 * 1024 * 1024
            free = shutil.disk_usage(target.parent).free
            if free < minimum_free:
                raise OSError(
                    f"Espaço insuficiente: necessários aproximadamente {minimum_free / 2**30:.1f} GiB; "
                    f"disponíveis {free / 2**30:.1f} GiB."
                )
            print(f"Banco v{version} detectado. Criando cópia SQLite de ~{size / 2**30:.2f} GiB...", flush=True)
            with closing(sqlite3.connect(temporary, timeout=30, isolation_level=None)) as converted:
                last_percent = -5

                def show_progress(status, remaining, total):
                    nonlocal last_percent
                    if status not in (sqlite3.SQLITE_OK, sqlite3.SQLITE_DONE):
                        raise sqlite3.DatabaseError(f"Falha ao copiar páginas SQLite: código {status}.")
                    percent = 100 if total == 0 else (total - remaining) * 100 // total
                    if percent >= last_percent + 5 or remaining == 0:
                        print(f"Cópia: {percent}%", flush=True)
                        last_percent = percent

                # A API de backup copia também transações confirmadas que ainda estejam no -wal.
                original.backup(converted, pages=2048, progress=show_progress, sleep=0.25)
                if read_version(converted) != ORIGIN_VERSION:
                    raise ValueError("A versão na cópia diverge da origem. Nenhum arquivo foi publicado.")
                check_34_shape(converted)
                before = quotation_counts(converted)
                converted.execute("BEGIN IMMEDIATE")
                try:
                    changed = converted.execute(
                        "UPDATE schema_info SET version=? WHERE id=1 AND version=?",
                        (TARGET_VERSION, ORIGIN_VERSION),
                    ).rowcount
                    if changed != 1:
                        raise ValueError("Registro de versão não pôde ser alterado.")
                    converted.execute("COMMIT")
                except Exception:
                    converted.execute("ROLLBACK")
                    raise

                # A estrutura adicional v34 fica intacta: a aplicação 1.2.27 a ignora.
                # Não remover colunas evita reescrever tabelas e preserva um futuro upgrade.
                if read_version(converted) != TARGET_VERSION or quotation_counts(converted) != before:
                    raise ValueError("Falhou a confirmação de versão/cotações; destino descartado.")
                journal_mode = converted.execute("PRAGMA journal_mode=DELETE").fetchone()[0]
                if journal_mode.lower() != "delete":
                    raise ValueError("Não foi possível consolidar o banco em um só arquivo.")

        if target.exists() or Path(str(target) + "-wal").exists() or Path(str(target) + "-shm").exists():
            raise FileExistsError("Destino ou arquivo auxiliar foi criado por outro processo; recusada substituição.")
        if temporary.with_name(temporary.name + "-wal").exists():
            raise ValueError("O arquivo temporário ainda possui um WAL; recusada publicação.")
        os.rename(temporary, target)
        print(f"CONCLUÍDO: {target}\nEsquema interno: 33. Banco original v34 preservado: {source}")
        print("As colunas extras da v34 permanecem, mas são ignoradas pelo PNCP King 1.2.27.")
        return 0
    except (OSError, sqlite3.Error, ValueError, KeyboardInterrupt) as error:
        print(f"ERRO: {error}\nBanco original preservado. Nenhum destino foi ativado.", file=sys.stderr)
        return 1
    finally:
        for suffix in ("", "-wal", "-shm", "-journal"):
            path = Path(str(temporary) + suffix)
            try:
                path.unlink(missing_ok=True)
            except OSError:
                pass


if __name__ == "__main__":
    sys.exit(main())
