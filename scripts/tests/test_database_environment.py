import copy
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('db_isolation', Path(__file__).parents[1] / 'isolate-database-environment.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class DatabaseEnvironmentTests(unittest.TestCase):
    def setUp(self):
        self.digest = 'sha256:' + 'a' * 64
        self.model = {'services': {'db': {'image': 'postgres:16-alpine@' + self.digest,
            'environment': {'POSTGRES_USER': 'synthetic-user', 'POSTGRES_DB': 'synthetic-db', 'POSTGRES_PASSWORD': 'synthetic-password'},
            'volumes': [{'type': 'volume', 'source': 'data', 'target': '/var/lib/postgresql/data'}]}},
            'volumes': {'data': {'name': 'synthetic-data'}}, 'networks': {'default': {'name': 'synthetic-network'}}}
        self.image = {'Id': self.digest, 'Config': {'Env': ['PGDATA=/var/lib/postgresql/data']}}
        self.container = {'Image': self.digest, 'Config': {'Env': ['PGDATA=/var/lib/postgresql/data',
            'POSTGRES_USER=synthetic-user', 'POSTGRES_DB=synthetic-db', 'POSTGRES_PASSWORD=synthetic-password',
            'Jwt__Secret=synthetic-jwt', 'Smtp__Password=synthetic-smtp']},
            'Mounts': [{'Type': 'volume', 'Name': 'synthetic-data', 'Destination': '/var/lib/postgresql/data'}],
            'HostConfig': {'PortBindings': {}}, 'NetworkSettings': {'Networks': {'synthetic-network': {}}}}

    def test_removes_only_application_settings_and_accepts_already_isolated_container(self):
        self.assertEqual(module.validate_database(self.model, self.container, self.image), ['Jwt__Secret','Smtp__Password'])
        self.container['Config']['Env'] = self.container['Config']['Env'][:-2]
        self.assertEqual(module.validate_database(self.model, self.container, self.image), [])

    def test_rejects_application_secrets_in_the_resolved_database_model(self):
        self.model['services']['db']['environment']['Smtp__Password'] = 'synthetic-value'
        with self.assertRaises(RuntimeError): module.validate_database(self.model, self.container, self.image)

    def test_rejects_database_credential_or_pgdata_drift(self):
        for key in ['POSTGRES_PASSWORD', 'PGDATA']:
            candidate = copy.deepcopy(self.container)
            candidate['Config']['Env'] = [line if not line.startswith(key+'=') else key+'=unexpected' for line in candidate['Config']['Env']]
            with self.assertRaises(RuntimeError): module.validate_database(self.model, candidate, self.image)

    def test_rejects_unknown_environment_settings(self):
        self.container['Config']['Env'].append('LD_PRELOAD=unexpected')
        with self.assertRaises(RuntimeError): module.validate_database(self.model, self.container, self.image)

    def test_rejects_image_volume_and_network_changes(self):
        for field, value in [('Image','sha256:'+'b'*64)]:
            candidate=copy.deepcopy(self.container);candidate[field]=value
            with self.assertRaises(RuntimeError): module.validate_database(self.model, candidate, self.image)
        self.container['Mounts'][0]['Name']='another-volume'
        with self.assertRaises(RuntimeError): module.validate_database(self.model, self.container, self.image)

    def test_rejects_mutable_images_and_published_ports(self):
        original=self.model['services']['db']['image'];self.model['services']['db']['image']='postgres:16-alpine'
        with self.assertRaises(RuntimeError): module.validate_database(self.model,self.container,self.image)
        self.model['services']['db']['image']=original;self.model['services']['db']['ports']=[5432]
        with self.assertRaises(RuntimeError): module.validate_database(self.model,self.container,self.image)

if __name__ == '__main__': unittest.main()
