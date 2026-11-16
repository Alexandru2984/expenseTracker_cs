import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('receipt_deployment',Path(__file__).parents[1]/'deploy-finance-integration.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)

class DeletionReceiptTests(unittest.TestCase):
    record={'userId':'12345678-1234-1234-1234-123456789abc','deletedAt':'2026-11-16T10:00:00Z'}

    def test_ledger_union_survives_an_empty_or_older_database_and_is_private(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            first=module.preserve_deletion_receipts(root,[self.record])
            self.assertEqual(first,module.preserve_deletion_receipts(root,[]))
            self.assertEqual(0o600,(root/'account-deletion-ledger.json').stat().st_mode&0o777)
            self.assertEqual(first,json.loads((root/'account-deletion-ledger.json').read_text()))

    def test_invalid_receipts_and_sql_syntax_are_rejected(self):
        for records in [None,{},[self.record,self.record],[self.record|{'userId':"'; DROP TABLE Users; --"}],
                        [self.record|{'deletedAt':'2026-11-16T10:00:00'}],[self.record|{'email':'unexpected@example.invalid'}]]:
            with self.assertRaises(RuntimeError):module.validate_deletion_receipts(records)

    def test_conflicting_receipt_and_symlink_do_not_replace_the_ledger(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);module.preserve_deletion_receipts(root,[self.record]);path=root/'account-deletion-ledger.json';before=path.read_bytes()
            with self.assertRaises(RuntimeError):module.preserve_deletion_receipts(root,[self.record|{'deletedAt':'2026-11-17T10:00:00Z'}])
            self.assertEqual(before,path.read_bytes());path.unlink();path.symlink_to(root/'another-file')
            with self.assertRaises(RuntimeError):module.preserve_deletion_receipts(root,[])

    def test_restore_guard_only_reads_and_rejects_resurrected_users(self):
        def run(args,**kwargs):
            self.assertIn('/var/run/postgresql',args)
            sql=args[-1];self.assertTrue(sql.startswith('SELECT COUNT(*)'));self.assertNotIn('DELETE',sql)
            return SimpleNamespace(stdout='1\n')
        with patch.object(module,'command',side_effect=run):
            with self.assertRaisesRegex(RuntimeError,'deleted accounts'):module.verify_deletion_receipts('private-restore','postgres','postgres',[self.record])
        with patch.object(module,'command',return_value=SimpleNamespace(stdout='0\n')):
            module.verify_deletion_receipts('private-restore','postgres','postgres',[self.record])

    def test_snapshot_handles_pre_feature_schema_and_validates_current_receipts(self):
        with patch.object(module,'command',return_value=SimpleNamespace(stdout='f\n')) as command:
            self.assertEqual([],module.collect_deletion_receipts('private-db','postgres','postgres'));self.assertEqual(1,command.call_count)
        with patch.object(module,'command',side_effect=[SimpleNamespace(stdout='t\n'),SimpleNamespace(stdout=json.dumps([self.record]))]):
            self.assertEqual(1,len(module.collect_deletion_receipts('private-db','postgres','postgres')))
