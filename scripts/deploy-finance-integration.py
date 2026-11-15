#!/usr/bin/env python3
"""Deploy only the API and immutable UI; preserve PostgreSQL, back up first, roll back on failure."""
import argparse
from datetime import datetime, timezone
import gzip
import json
import os
import re
from pathlib import Path
import shutil
import shlex
import subprocess
import tempfile
import time

ROOT=Path(__file__).resolve().parents[1]


def command(args,**kwargs):
    return subprocess.run(args,check=True,**({"stdout":subprocess.PIPE,"stderr":subprocess.PIPE}|kwargs))


def with_security_headers(config):
    """Embed reviewed app-specific policies; shared host snippets stay unchanged."""
    spa=(ROOT/'ops/security-headers-spa.conf').read_text().strip()
    api=(ROOT/'ops/security-headers-api.conf').read_text().strip()
    start='# expense-security-spa-start';end='# expense-security-spa-end'
    managed=start+'\n    '+spa.replace('\n','\n    ')+'\n    '+end
    if 'include snippets/security-headers.conf;' in config:
        config=config.replace('include snippets/security-headers.conf;',managed,1)
    elif start in config and end in config:
        config=re.sub(re.escape(start)+r'.*?'+re.escape(end),lambda _:managed,config,count=1,flags=re.S)
    else:raise RuntimeError('Expected security headers configuration was not found')
    names=re.findall(r'add_header\s+([\w-]+)',api)
    for route in ('/api/','/health'):
        pattern=r'(location\s+'+re.escape(route)+r'\s*\{)([^{}]*)(\})'
        def replace(match):
            body=re.sub(r'\s*# expense-security-api-start.*?# expense-security-api-end','',match[2],flags=re.S)
            policy='\n        # expense-security-api-start\n'
            policy+=''.join('        proxy_hide_header '+name+';\n' for name in names)
            policy+='        '+api.replace('\n','\n        ')+'\n        # expense-security-api-end\n    '
            return match[1]+body+policy+match[3]
        config,count=re.subn(pattern,replace,config,count=1)
        if count!=1:raise RuntimeError('Expected proxy location was not found: '+route)
    return config


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--image',required=True);args=parser.parse_args()
    if os.geteuid()!=0:raise SystemExit('Run with sudo')
    git=['git','-c','safe.directory='+str(ROOT)]
    if command(git+['status','--porcelain'],cwd=ROOT,text=True).stdout.strip():
        raise SystemExit('Commit the reviewed source and build before deploying; working tree is dirty')
    full_revision=command(git+['rev-parse','HEAD'],cwd=ROOT,text=True).stdout.strip()
    candidate=json.loads(command(['docker','image','inspect',args.image],text=True).stdout)[0]
    if (candidate['Config'].get('Labels') or {}).get('org.opencontainers.image.revision')!=full_revision:
        raise SystemExit('Image revision must match HEAD; build with --build-arg VCS_REF=<full commit>')
    candidate_id=candidate['Id']
    frontend=ROOT/'expense-tracker-ui/dist'
    if not (frontend/'index.html').is_file() or not (frontend/'build-revision.txt').is_file():
        raise SystemExit('Build the UI from the committed source before deploying')
    if (frontend/'build-revision.txt').read_text().strip()!=full_revision:
        raise SystemExit('Frontend build revision must match HEAD; rebuild the UI after committing')
    stamp=datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')
    state=Path('/var/backups/expense-tracker')/('deploy-'+stamp);state.mkdir(parents=True,mode=0o700);os.chmod(state.parent,0o700)
    envfile=ROOT/'.env';nginx=Path('/etc/nginx/sites-available/expenses.micutu.com')
    old_env=envfile.read_bytes();old_nginx=nginx.read_bytes()
    (state/'app.env').write_bytes(old_env);(state/'app.env').chmod(0o600)
    (state/'nginx.conf').write_bytes(old_nginx)
    active=json.loads(command(['docker','inspect','expensetracker_cs-api-1','expensetracker_cs-db-1'],text=True).stdout)
    api_env=dict(item.split('=',1) for item in active[0]['Config']['Env'] if '=' in item)
    db_env=dict(item.split('=',1) for item in active[1]['Config']['Env'] if '=' in item)
    lexer=shlex.shlex(api_env.get('ConnectionStrings__DefaultConnection',''),posix=True)
    lexer.whitespace=';';lexer.whitespace_split=True;lexer.commenters=''
    fields={key.strip().lower():value.strip() for part in lexer if '=' in part for key,value in [part.split('=',1)]}
    if fields.get('host')!='db' or fields.get('database')!=db_env.get('POSTGRES_DB'):
        raise RuntimeError('Live database identity does not match the backup source; review before deploying')
    old_image=active[0]['Image']
    rollback='expensetracker_cs-api:rollback-'+stamp
    command(['docker','image','tag',old_image,rollback])
    (state/'rollback-image.txt').write_text(rollback+'\n')
    # Consistent PostgreSQL MVCC snapshot; credentials remain inside the source container.
    archive=state/'database.dump'
    with archive.open('wb') as out:
        command(['docker','exec','expensetracker_cs-db-1','sh','-c','exec pg_dump --format=custom -U "$POSTGRES_USER" "$POSTGRES_DB"'],stdout=out)
    archive.chmod(0o600)
    with archive.open('rb') as saved:
        command(['docker','exec','-i','expensetracker_cs-db-1','pg_restore','--list'],stdin=saved)
    # Full restore drill in a separate, non-networked, ephemeral database container.
    pgimage=command(['docker','inspect','--format','{{.Image}}','expensetracker_cs-db-1'],text=True).stdout.strip()
    probe='expense-restore-probe-'+stamp.lower()
    try:
        command(['docker','run','-d','--name',probe,'--network','none','--memory','256m','--pids-limit','128','--tmpfs','/var/lib/postgresql/data:rw,nosuid,noexec,size=128m','-e','POSTGRES_HOST_AUTH_METHOD=trust',pgimage])
        for _ in range(100):
            # initdb temporarily starts a socket-only server, then stops it.
            # TCP readiness identifies the final server and avoids that race.
            if subprocess.run(['docker','exec',probe,'pg_isready','-h','127.0.0.1','-U','postgres'],capture_output=True).returncode==0:break
            time.sleep(.1)
        else:raise RuntimeError('Restore probe was not ready')
        with archive.open('rb') as saved:
            command(['docker','exec','-i',probe,'pg_restore','--exit-on-error','--no-owner','--no-privileges','-h','127.0.0.1','-U','postgres','-d','postgres'],stdin=saved)
        counts=command(['docker','exec',probe,'psql','-h','127.0.0.1','-U','postgres','-d','postgres','-At','-c','SELECT json_build_object(\'users\',(SELECT COUNT(*) FROM "Users"),\'subscriptions\',(SELECT COUNT(*) FROM "Subscriptions"))'],text=True).stdout.strip()
        json.loads(counts)
        print('Verified PostgreSQL backup restored in isolation; table counts:',counts)
    finally:subprocess.run(['docker','rm','-f',probe],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    if any(path.is_symlink() for path in frontend.rglob('*')):raise RuntimeError('Symlinks are not allowed in public artifacts')
    revision=command(['git','-c','safe.directory='+str(ROOT),'rev-parse','--short','HEAD'],cwd=ROOT,text=True).stdout.strip()
    release=Path('/opt/expense-tracker/releases')/(stamp+'-'+revision)
    release.mkdir(parents=True,mode=0o755)
    shutil.copytree(frontend,release/'public')
    for path in release.rglob('*'):os.chown(path,0,0);path.chmod(0o755 if path.is_dir() else 0o644)
    compose=['docker','compose','--project-name','expensetracker_cs','--project-directory',str(ROOT),'--env-file',str(envfile),'-f',str(ROOT/'docker-compose.yml')]
    def activate_image(image):
        override=state/'image-override.json';override.write_text(json.dumps({'services':{'api':{'image':image}}}))
        command(compose+['-f',str(override),'up','-d','--no-deps','--no-build','api'],cwd=ROOT)
        for _ in range(80):
            status=command(['docker','inspect','--format','{{.State.Health.Status}}','expensetracker_cs-api-1'],text=True).stdout.strip()
            if status=='healthy':break
            time.sleep(.5)
        else:raise RuntimeError('New API did not become healthy')
    try:
        lines=[line for line in old_env.decode().splitlines() if not line.startswith('FinanceIntegration__Enabled=')]
        envfile.write_text('\n'.join(lines)+'\nFinanceIntegration__Enabled=true\n');envfile.chmod(0o600)
        activate_image(candidate_id)
        config=old_nginx.decode()
        blocks=list(re.finditer(r'location\s+/\s*\{([^{}]*)\}',config))
        static=next((block for block in blocks if re.search(r'\broot\s+[^;]+;',block[1])),None)
        if static is None:raise RuntimeError('Static UI location was not found')
        body=re.sub(r'\broot\s+[^;]+;', 'root '+str(release/'public')+';',static[1],count=1)
        # HTML with a request-specific CSP nonce must not be revalidated against
        # a cached response carrying another request's nonce.
        body=re.sub(r'\bexpires\s+[^;]+;','',body)
        config=config[:static.start(1)]+body+config[static.end(1):]
        asset_headers='\n'.join(line for line in (ROOT/'ops/security-headers-spa.conf').read_text().splitlines()
            if not line.startswith('add_header Cache-Control'))
        assets='location /assets/ {\n        root '+str(release/'public')+';\n        try_files $uri =404;\n        '+asset_headers.replace('\n','\n        ')+'\n        add_header Cache-Control "public, max-age=31536000, immutable" always;\n    }'
        if re.search(r'location\s+/assets/\s*\{[^{}]*\}',config):
            config=re.sub(r'location\s+/assets/\s*\{[^{}]*\}',lambda _:assets,config,count=1)
        else:
            config=config.replace('location / {',assets+'\n\n    location / {',1)
        # Keep app-specific crawl rules on the same immutable release. The host
        # shared snippet points at another domain's sitemap and stays unchanged.
        robots='location = /robots.txt {\n        root '+str(release/'public')+';\n        default_type text/plain;\n        expires -1;\n        try_files $uri =404;\n    }'
        if re.search(r'location\s*=\s*/robots\.txt\s*\{[^{}]*\}',config):
            config=re.sub(r'location\s*=\s*/robots\.txt\s*\{[^{}]*\}',robots,config,count=1)
        elif 'include snippets/robots.conf;' in config:
            config=config.replace('include snippets/robots.conf;',robots,1)
        else:raise RuntimeError('Expected app robots location or shared snippet was not found')
        config=with_security_headers(config)
        nginx.write_text(config)
        command(['nginx','-t']);command(['systemctl','reload','nginx'])
        # Check the actual origin vhost, not a successful reload signal alone.
        for _ in range(30):
            response=command(['curl','--fail','--silent','--show-error','--max-time','5','--resolve','expenses.micutu.com:443:127.0.0.1','https://expenses.micutu.com/'])
            if response.stdout==(release/'public/index.html').read_bytes():break
            time.sleep(.2)
        else:raise RuntimeError('New immutable UI did not become active')
        for public_file in ('robots.txt','sitemap.xml'):
            response=command(['curl','--fail','--silent','--show-error','--max-time','5','--resolve','expenses.micutu.com:443:127.0.0.1','https://expenses.micutu.com/'+public_file])
            if response.stdout!=(release/'public'/public_file).read_bytes():
                raise RuntimeError('Public crawl metadata did not become active: '+public_file)
        status=command(['curl','--silent','--show-error','--max-time','5','-o','/dev/null','-w','%{http_code}','--resolve','expenses.micutu.com:443:127.0.0.1','https://expenses.micutu.com/api/integrations/finance/subscriptions'],text=True).stdout
        if status!='401':raise RuntimeError('Scoped export did not reject an anonymous request')
        headers=command(['curl','--silent','--show-error','--max-time','5','-D','-','-o','/dev/null','--resolve','expenses.micutu.com:443:127.0.0.1','https://expenses.micutu.com/api/integrations/finance/subscriptions'],text=True).stdout
        for name,value in {'x-frame-options':'DENY','referrer-policy':'no-referrer','cache-control':'no-store','x-robots-tag':'noindex, nofollow'}.items():
            values=[line.split(':',1)[1].strip() for line in headers.splitlines() if line.lower().startswith(name+':')]
            if values!=[value]:raise RuntimeError('Expected one authoritative security header: '+name)
        print('C# API and immutable UI activated:',revision,'; backup and rollback metadata:',state)
    except BaseException:
        envfile.write_bytes(old_env);nginx.write_bytes(old_nginx)
        activate_image(rollback)
        command(['nginx','-t']);command(['systemctl','reload','nginx'])
        raise


if __name__=='__main__':main()
