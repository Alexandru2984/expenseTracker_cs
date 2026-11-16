#!/usr/bin/env python3
"""Remove application secrets from the DB container, preserving its exact image and volume."""
import fcntl
import json
import os
from pathlib import Path
import re
import subprocess
import time
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[1]
DB = 'expensetracker_cs-db-1'
API = 'expensetracker_cs-api-1'
DB_KEYS = {'POSTGRES_DB', 'POSTGRES_USER', 'POSTGRES_PASSWORD'}
APP_PREFIXES = ('Jwt__', 'Smtp__', 'Auth__', 'ConnectionStrings__', 'Cors__', 'ForwardedHeaders__',
                'FinanceIntegration__', 'Notifications__', 'Mfa__', 'ASPNETCORE_', 'MAILCOW_')

def validate_database(model, container, image):
    service = model['services']['db']
    declared = service.get('environment', {})
    if set(declared) != DB_KEYS:
        raise RuntimeError('Database environment must declare only its three PostgreSQL settings')
    if not re.search(r'@sha256:[a-f0-9]{64}$', service['image']) or image['Id'] != container['Image']:
        raise RuntimeError('Database image must be pinned to the currently running image')
    before = dict(item.split('=', 1) for item in container['Config']['Env'])
    expected = dict(item.split('=', 1) for item in image['Config']['Env'])
    expected.update({k: str(v) for k, v in declared.items()})
    if any(before.get(k) != v for k, v in expected.items()):
        raise RuntimeError('PostgreSQL image defaults or database credentials would change')
    removed = sorted(set(before) - set(expected))
    if any(not key.startswith(APP_PREFIXES) for key in removed):
        raise RuntimeError('Unknown non-application environment settings require review')
    volumes = service.get('volumes', [])
    if len(volumes) != 1 or volumes[0]['type'] != 'volume' or volumes[0]['target'] != '/var/lib/postgresql/data':
        raise RuntimeError('Expected the existing named PostgreSQL data volume')
    name = model['volumes'][volumes[0]['source']]['name']
    mounts = container['Mounts']
    if len(mounts) != 1 or mounts[0]['Type'] != 'volume' or mounts[0]['Name'] != name or mounts[0]['Destination'] != volumes[0]['target']:
        raise RuntimeError('PostgreSQL data mount would change')
    if service.get('ports') or container['HostConfig'].get('PortBindings'):
        raise RuntimeError('Database must not publish a host port')
    if set(container['NetworkSettings']['Networks']) != {model['networks']['default']['name']}:
        raise RuntimeError('Database network requires review')
    return removed

def run(args, **kw):
    return subprocess.run(args, check=True, stdout=kw.pop('stdout', subprocess.PIPE),
                          stderr=subprocess.PIPE, **kw)
def inspect(name):
    return json.loads(run(['docker', 'inspect', name], text=True).stdout)[0]
def config():
    return json.loads(run(['docker', 'compose', 'config', '--format', 'json'], cwd=ROOT, text=True).stdout)
def healthy(name):
    for _ in range(60):
        if inspect(name)['State'].get('Health', {}).get('Status') == 'healthy':
            return
        time.sleep(1)
    raise RuntimeError('Container did not become healthy: ' + name)

def api_healthy():
    for _ in range(30):
        result = subprocess.run(['curl', '--fail', '--silent', '--show-error', '--max-time', '3',
                                 'http://127.0.0.1:8098/health'], capture_output=True)
        if result.returncode == 0:
            return
        time.sleep(1)
    raise RuntimeError('API database health probe failed')

def main():
    if os.geteuid() != 0:
        raise SystemExit('Run with sudo')
    os.umask(0o077)
    parent = Path('/var/backups/expense-tracker'); parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    lock = (parent / '.deploy.lock').open('a')
    fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    git = ['git', '-c', 'safe.directory=' + str(ROOT)]
    def clean():
        if run(git + ['status', '--porcelain'], cwd=ROOT, text=True).stdout.strip():
            raise RuntimeError('Commit the reviewed database isolation before activation')
        return run(git + ['rev-parse', 'HEAD'], cwd=ROOT, text=True).stdout.strip()
    revision = clean(); model = config(); before = inspect(DB); api = inspect(API)
    image = json.loads(run(['docker', 'image', 'inspect', model['services']['db']['image']], text=True).stdout)[0]
    removed = validate_database(model, before, image)
    if not removed:
        print('Database already has only its PostgreSQL environment; no recreation needed')
        return
    stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')
    state = parent / ('db-env-' + stamp); state.mkdir(mode=0o700)
    (state / 'db-before.json').write_text(json.dumps(before))
    (state / 'source-revision.txt').write_text(revision + '\n')
    archive = state / 'database.dump'
    with archive.open('wb') as output:
        run(['docker', 'exec', DB, 'sh', '-c', 'exec pg_dump --format=custom -U "$POSTGRES_USER" "$POSTGRES_DB"'], stdout=output)
    probe = 'expense-db-env-restore-' + stamp.lower()
    try:
        run(['docker', 'run', '-d', '--name', probe, '--network', 'none', '--memory', '256m', '--pids-limit', '128',
             '--tmpfs', '/var/lib/postgresql/data:rw,nosuid,noexec,size=128m', '-e', 'POSTGRES_HOST_AUTH_METHOD=trust', before['Image']])
        for _ in range(100):
            if subprocess.run(['docker', 'exec', probe, 'pg_isready', '-h', '127.0.0.1', '-U', 'postgres'], capture_output=True).returncode == 0:
                break
            time.sleep(.1)
        else:
            raise RuntimeError('Isolated database restore was not ready')
        with archive.open('rb') as source:
            run(['docker', 'exec', '-i', probe, 'pg_restore', '--exit-on-error', '--no-owner', '--no-privileges',
                 '-h', '127.0.0.1', '-U', 'postgres', '-d', 'postgres'], stdin=source)
        run(['docker', 'exec', probe, 'psql', '-h', '127.0.0.1', '-U', 'postgres', '-d', 'postgres', '-At', '-c',
             'SELECT COUNT(*) FROM "Users"; SELECT COUNT(*) FROM "__EFMigrationsHistory";'])
    finally:
        subprocess.run(['docker', 'rm', '-f', probe], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    # Abort outside rollback if another operator changed the reviewed state.
    if clean() != revision or config() != model or inspect(DB)['Id'] != before['Id'] or inspect(API)['Id'] != api['Id']:
        raise RuntimeError('Configuration, source or containers changed during the restore drill')
    compose = ['docker', 'compose', '--project-name', 'expensetracker_cs']
    try:
        run(compose + ['up', '-d', '--no-deps', '--no-build', '--pull', 'never', 'db'], cwd=ROOT)
        healthy(DB)
        after = inspect(DB)
        if validate_database(model, after, image):
            raise RuntimeError('Application settings are still present in the database environment')
        if after['Image'] != before['Image'] or after['Mounts'][0]['Name'] != before['Mounts'][0]['Name']:
            raise RuntimeError('Database image or data volume changed')
        try:
            api_healthy()
        except RuntimeError:
            run(['docker', 'restart', API]); healthy(API); api_healthy()
        if inspect(API)['Image'] != api['Image']:
            raise RuntimeError('API image changed during database isolation')
        (state / 'verified.json').write_text(json.dumps({'before': before['Id'], 'after': after['Id'],
            'sameImage': True, 'sameDataVolume': True, 'removedAppVariables': removed, 'apiHealthy': True}))
        print('Database environment isolated; exact image and data volume preserved; API healthy; backup:', state)
    except BaseException:
        # Restore the prior container configuration, never overwrite its live data.
        old_env = dict(item.split('=', 1) for item in before['Config']['Env'])
        rollback = state / 'rollback-db.json'
        rollback.write_text(json.dumps({'services': {'db': {'image': before['Image'], 'environment': old_env}}}))
        run(['docker', 'compose', '--project-name', 'expensetracker_cs', '-f', str(ROOT / 'docker-compose.yml'),
             '-f', str(ROOT / 'docker-compose.override.yml'), '-f', str(rollback), 'up', '-d', '--no-deps', '--no-build', '--pull', 'never', 'db'], cwd=ROOT)
        healthy(DB); run(['docker', 'restart', API]); healthy(API); api_healthy()
        raise

if __name__ == '__main__':
    main()
