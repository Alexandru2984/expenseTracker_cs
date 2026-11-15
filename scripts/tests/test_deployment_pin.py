import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('expense_deployment', ROOT / 'scripts/deploy-finance-integration.py')
deployment = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deployment)


class ComposePinTests(unittest.TestCase):
    image = 'sha256:' + '1' * 64

    def test_default_compose_uses_immutable_pin_without_changing_database_configuration(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'docker-compose.yml').write_bytes((ROOT / 'docker-compose.yml').read_bytes())
            (root / '.env').write_text('POSTGRES_USER=synthetic\nPOSTGRES_PASSWORD=synthetic-test-password\nPOSTGRES_DB=synthetic\n')
            env = {k: v for k, v in os.environ.items() if not k.startswith(('COMPOSE_', 'POSTGRES_')) and k != 'ASPNETCORE_ENVIRONMENT'}
            def config():
                result = subprocess.run(['docker', 'compose', '--project-name', 'expense-pin-test', '--project-directory', str(root), 'config', '--format', 'json'], env=env, check=True, capture_output=True, text=True)
                return json.loads(result.stdout)
            before = config()
            self.assertEqual('synthetic-test-password', before['services']['db']['environment']['POSTGRES_PASSWORD'])
            deployment.write_compose_pin(root, self.image)
            after = config()
            self.assertEqual(self.image, after['services']['api']['image'])
            self.assertEqual(before['services']['db'], after['services']['db'])
            self.assertEqual(deployment.compose_pin(self.image), json.loads(deployment.read_compose_pin(root)))

    def test_unmanaged_override_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); path = root / 'docker-compose.override.yml'
            original = b'services:\n  api:\n    ports: ["9999:8080"]\n'
            path.write_bytes(original)
            with self.assertRaises(RuntimeError): deployment.read_compose_pin(root)
            self.assertEqual(original, path.read_bytes())

    def test_alternative_override_and_symlink_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); alternate = root / 'compose.override.yaml'
            alternate.write_text('services: {}')
            with self.assertRaises(RuntimeError): deployment.read_compose_pin(root)
            alternate.unlink()
            (root / 'target').write_text('{}')
            (root / 'docker-compose.override.yml').symlink_to(root / 'target')
            with self.assertRaises(RuntimeError): deployment.read_compose_pin(root)

    def test_mutable_reference_is_rejected(self):
        for image in ['latest', 'expensetracker_cs-api:latest', self.image + '\nother']:
            with self.assertRaises(ValueError): deployment.compose_pin(image)


if __name__ == '__main__': unittest.main()
