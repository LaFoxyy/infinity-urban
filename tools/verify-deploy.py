from pathlib import Path
import sys
root=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(root/'.local/validation-tools'))
import yaml

stack=yaml.safe_load((root/'deploy/portainer-stack.yml').read_text(encoding='utf-8'))
assert set(stack['services'])=={'web'},'A stack deve conter somente nossa aplicação.'
app=stack['services']['web'];deploy=app['deploy'];env=app['environment'];labels=dict(line.split('=',1) for line in deploy['labels'])
assert 'build' not in app and 'ports' not in app and 'privileged' not in app
assert 'network_mode' not in app and 'pid' not in app and app['user']=='1654:1654'
assert app['volumes']==['infinityboard_data:/app/.local']
assert set(stack['volumes'])=={'infinityboard_data'} and stack['volumes']['infinityboard_data']['external'] is True
assert app['networks']==['infinityNet'] and stack['networks']['infinityNet']['external'] is True
assert deploy['replicas']==1 and deploy['update_config']['order']=='stop-first' and deploy['rollback_config']['order']=='stop-first'
assert deploy['resources']['limits']=={'cpus':'0.50','memory':'512M'}
assert 'node.hostname == infinityServer' in deploy['placement']['constraints']
assert env['AllowedHosts']=='infinityboard.urbaninfinity.com.br'
assert env['Hosting__ProxyNetwork']=='10.0.1.0/24'
assert env['Channex__Enabled']=='${INFINITYBOARD_CHANNEX_ENABLED:-false}'
assert env['Hosting__SecretsFile']=='/run/secrets/infinityboard-channex.json'
assert labels['traefik.http.routers.infinityboard.rule']=='Host(`infinityboard.urbaninfinity.com.br`)'
assert labels['traefik.docker.network']=='infinityNet'
assert labels['traefik.http.routers.infinityboard.entrypoints']=='websecure'
assert labels['traefik.http.routers.infinityboard.tls.certresolver']=='letsencryptresolver'
assert labels['traefik.http.services.infinityboard-web.loadbalancer.server.port']=='8080'
for label in labels:
    assert label in {'traefik.enable','traefik.docker.network'} or label.startswith('traefik.http.routers.infinityboard.') or label.startswith('traefik.http.services.infinityboard-web.'),'Label fora do nosso serviço.'
assert set(stack['secrets'])=={'infinityboard_channex_v1'}
workflow=yaml.load((root/'.github/workflows/infinityboard.yml').read_text(encoding='utf-8'),Loader=yaml.BaseLoader)
assert set(workflow['jobs'])=={'checks','image','deploy'}
assert workflow['jobs']['image']['needs']=='checks' and workflow['jobs']['deploy']['needs']=='image'
assert workflow['jobs']['checks']['runs-on']=='ubuntu-latest' and workflow['jobs']['image']['runs-on']=='ubuntu-latest'
print('PASS YAML valido: dominio exato, somente nosso servico/volume, sem portas publicas ou socket Docker, limites e inicializacao Channex desativada.')
