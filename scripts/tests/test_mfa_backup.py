import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('mfa_deployment', Path(__file__).parents[1] / 'deploy-finance-integration.py')
deployment = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deployment)

class MfaBackupTests(unittest.TestCase):
    def test_master_key_is_private_and_not_exposed_in_command_arguments(self):
        key = 'synthetic-private-key'
        with tempfile.TemporaryDirectory() as directory:
            state = Path(directory)
            def run(args):
                self.assertNotIn(key, '\n'.join(args))
                self.assertIn('--read-only', args)
                self.assertIn('container:isolated-pg', args)
                self.assertEqual('--check-mfa-key', args[-1])
                env = state / 'mfa-key-candidate.env'
                self.assertEqual(0o600, env.stat().st_mode & 0o777)
                self.assertEqual('Mfa__EncryptionKey='+key+'\n', env.read_text())
            with patch.object(deployment, 'command', side_effect=run) as command:
                deployment.probe_mfa_key('immutable-image', 'isolated-pg', key, state, 'candidate')
                self.assertEqual(1, command.call_count)

    def test_environment_newlines_are_rejected_before_writing_or_running(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(deployment, 'command') as command:
            for key in ['key\nSmtp__Password=unexpected', 'key\rOther=unexpected']:
                with self.assertRaises(RuntimeError):
                    deployment.probe_mfa_key('image','isolated-pg',key,Path(directory),'candidate')
            command.assert_not_called()
            self.assertEqual([], list(Path(directory).iterdir()))
