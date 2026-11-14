#!/usr/bin/env python3
"""Harden only the Expense Tracker vhost and configure its exact Docker proxy.

Run before deploying the matching API release. Preserves the old env/vhost
privately; rolls back nginx/env on failure and never prints credentials.
"""
from datetime import datetime, timezone
import ipaddress
import json
import os
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def command(*args):
    result = subprocess.run(args, capture_output=True, timeout=30)
    if result.returncode:
        raise RuntimeError(f'{args[0]} failed; inspect its private service/configuration logs')
    return result.stdout


def replace(path, contents, info):
    temporary = path.with_name('.' + path.name + '.proxy-next')
    with temporary.open('wb') as stream:
        stream.write(contents)
        stream.flush()
        os.fsync(stream.fileno())
    os.chmod(temporary, info.st_mode & 0o777)
    os.chown(temporary, info.st_uid, info.st_gid)
    temporary.replace(path)


def main():
    if os.geteuid() != 0:
        raise SystemExit('Run as root')
    os.umask(0o077)
    config = Path('/etc/nginx/sites-available/expenses.micutu.com')
    envfile = ROOT / '.env'
    old, old_env = config.read_bytes(), envfile.read_bytes()
    config_info, env_info = config.stat(), envfile.stat()
    current = old.decode()
    if '$proxy_add_x_forwarded_for' not in current:
        raise RuntimeError('Expected the old forwarded-header configuration; inspect current state')
    if current.count('server_name expenses.micutu.com;') != 2:
        raise RuntimeError('Expected only the HTTP and HTTPS Expense Tracker servers')
    candidate = current.replace('server_name expenses.micutu.com;',
        'server_name expenses.micutu.com;\n    include snippets/cloudflare-realip.conf;', 1)
    candidate = candidate.replace('proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;',
        'proxy_set_header X-Forwarded-For $remote_addr;\n        proxy_set_header CF-Connecting-IP "";')
    health = re.search(r'location /health\s*\{([^{}]+)\}', candidate)
    if health is None:
        raise RuntimeError('Health location not found')
    body = health[1] + '\n        proxy_set_header X-Forwarded-For $remote_addr;\n        proxy_set_header X-Forwarded-Proto $scheme;\n        proxy_set_header CF-Connecting-IP "";\n    '
    candidate = candidate[:health.start(1)] + body + candidate[health.end(1):]
    container = json.loads(command('docker', 'inspect', 'expensetracker_cs-api-1'))[0]
    networks = list(container['NetworkSettings']['Networks'].values())
    if len(networks) != 1:
        raise RuntimeError('Expected one API bridge network; review proxy identity')
    gateway = ipaddress.ip_address(networks[0]['Gateway'])
    trusted = f'{gateway}/{gateway.max_prefixlen},127.0.0.1/32,::1/128'
    lines = [line for line in old_env.decode().splitlines() if not line.startswith('ForwardedHeaders__TrustedNetworks=')]
    env = ('\n'.join(lines) + '\nForwardedHeaders__TrustedNetworks=' + trusted + '\n').encode()
    stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')
    backup = Path('/var/backups/expense-tracker') / ('proxy-' + stamp)
    backup.mkdir(parents=True, mode=0o700)
    (backup / 'nginx.conf').write_bytes(old)
    (backup / 'app.env').write_bytes(old_env)
    try:
        replace(config, candidate.encode(), config_info)
        replace(envfile, env, env_info)
        command('nginx', '-t')
        command('systemctl', 'reload', 'nginx')
        command('curl', '--fail', '--silent', '--show-error', '--max-time', '8', '--output', '/dev/null',
                '--resolve', 'expenses.micutu.com:443:127.0.0.1', 'https://expenses.micutu.com/health')
        print('Nginx now verifies/overwrites client metadata. Exact proxy configured for the next API activation:', trusted)
    except BaseException:
        replace(config, old, config_info)
        replace(envfile, old_env, env_info)
        command('nginx', '-t')
        command('systemctl', 'reload', 'nginx')
        raise


if __name__ == '__main__':
    main()
