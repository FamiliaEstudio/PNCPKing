import importlib.util
from pathlib import Path
import sqlite3
import tempfile
import unittest

SCRIPT = Path(__file__).with_name('downgrade_schema_34_to_33.py')
spec = importlib.util.spec_from_file_location('downgrade', SCRIPT)
downgrade = importlib.util.module_from_spec(spec)
spec.loader.exec_module(downgrade)


class DowngradeTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.input = Path(self.tmp.name) / 'pncpking.db'
        self.output = Path(self.tmp.name) / 'pncpking-v33.db'
        with sqlite3.connect(self.input) as db:
            db.executescript('''
                CREATE TABLE schema_info(id INTEGER PRIMARY KEY, version INTEGER NOT NULL);
                INSERT INTO schema_info VALUES(1,34);
                CREATE TABLE official_update_chunks(chunk_key TEXT PRIMARY KEY,
                    last_contract_id TEXT, processed_contracts INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE quotation_projects(id TEXT PRIMARY KEY, title TEXT);
                CREATE TABLE quotation_lines(id TEXT PRIMARY KEY, project_id TEXT, selection TEXT);
                CREATE TABLE quotation_references(id TEXT PRIMARY KEY, line_id TEXT, unit_price INTEGER, state INTEGER);
                CREATE TABLE contracts(id TEXT PRIMARY KEY, search_text TEXT);
                CREATE TRIGGER contracts_fts_update AFTER UPDATE OF search_text ON contracts
                    WHEN old.search_text IS NOT new.search_text BEGIN SELECT 1; END;
                INSERT INTO quotation_projects VALUES ('p1','Processo 180086');
                INSERT INTO quotation_lines VALUES ('l1','p1','preço selecionado');
                INSERT INTO quotation_references VALUES ('r1','l1',123456,1);
                INSERT INTO official_update_chunks VALUES('c1', 'old id', 77);
                INSERT INTO contracts VALUES('c1','teste');
            ''')

    def check_data(self, path, version):
        with sqlite3.connect(path) as db:
            self.assertEqual(db.execute('SELECT version FROM schema_info').fetchone()[0], version)
            self.assertEqual(db.execute('SELECT selection FROM quotation_lines').fetchone()[0], 'preço selecionado')
            self.assertEqual(db.execute('SELECT unit_price,state FROM quotation_references').fetchone(), (123456,1))
            self.assertEqual(db.execute('SELECT last_contract_id,processed_contracts FROM official_update_chunks').fetchone(), ('old id',77))
            self.assertIn('WHEN old.search_text IS NOT new.search_text', db.execute("SELECT sql FROM sqlite_master WHERE name='contracts_fts_update'").fetchone()[0])

    def test_copy_and_original_preserved(self):
        self.assertEqual(downgrade.main([str(self.input), str(self.output)]), 0)
        self.check_data(self.output,33)
        self.check_data(self.input,34)
        self.assertFalse(Path(str(self.output)+'-wal').exists())

    def test_already_exists_refused(self):
        self.output.write_bytes(b'KEEP')
        with self.assertRaises(SystemExit):
            downgrade.main([str(self.input), str(self.output)])
        self.assertEqual(self.output.read_bytes(), b'KEEP')

    def test_wrong_version_refused(self):
        with sqlite3.connect(self.input) as db:
            db.execute('UPDATE schema_info SET version=33')
        self.assertEqual(downgrade.main([str(self.input), str(self.output)]), 1)
        self.assertFalse(self.output.exists())

    def test_incomplete_34_refused(self):
        with sqlite3.connect(self.input) as db:
            db.execute('ALTER TABLE official_update_chunks DROP COLUMN processed_contracts')
        self.assertEqual(downgrade.main([str(self.input), str(self.output)]), 1)
        self.assertFalse(self.output.exists())

    def test_wal_committed_data_included(self):
        with sqlite3.connect(self.input) as db:
            db.execute('PRAGMA journal_mode=WAL')
            db.execute("INSERT INTO quotation_references VALUES('r2','l1',999999,2)")
            db.commit()
            self.assertEqual(downgrade.main([str(self.input), str(self.output)]), 0)
        with sqlite3.connect(self.output) as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM quotation_references').fetchone()[0], 2)


if __name__ == '__main__':
    unittest.main()
