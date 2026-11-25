from pathlib import Path
from html.parser import HTMLParser
import hashlib,json,sys,zipfile

root=Path(__file__).resolve().parents[1]
manifest=json.loads((root/'docs/integracao/fontes-preservadas.json').read_text(encoding='utf-8'))
for entry in manifest['files']:
    path=(root/entry['path']).resolve()
    if not path.is_relative_to(root) or hashlib.sha256(path.read_bytes()).hexdigest()!=entry['sha256']:
        raise RuntimeError('Fonte da integração alterada: '+entry['path'])

class Page(HTMLParser):
    def __init__(self):super().__init__();self.ids=[]
    def handle_starttag(self,tag,attrs):
        for key,value in attrs:
            if key=='id':self.ids.append(value)

web=root/'src/InfinityUrban.Web'
for path in (web/'Pages').glob('*.html'):
    page=Page();page.feed(path.read_text(encoding='utf-8'))
    if len(set(page.ids))!=len(page.ids):raise RuntimeError('IDs duplicados em '+path.name)

excluded={'.local','App_Data','bin','obj','.git','node_modules','dist','.vs','__pycache__'}
files=[p for p in root.rglob('*') if p.is_file() and not any(part in excluded for part in p.relative_to(root).parts)]
private=web/'.local/channex-secrets.json'
secrets=[]
def collect(value):
    if isinstance(value,dict):
        for key,item in value.items():
            if isinstance(item,str) and len(item)>16 and any(word in key.lower() for word in ['key','secret','token']):secrets.append(item.encode())
            else:collect(item)
if private.exists():collect(json.loads(private.read_text(encoding='utf-8-sig')))
for path in files:
    if path.suffix in {'.db','.db-wal','.db-shm','.log'}:raise RuntimeError('Arquivo privado na entrega: '+str(path))
    if any(secret in path.read_bytes() for secret in secrets):raise RuntimeError('Credencial na entrega: '+str(path))

report={'project':'Infinity Urban','approvedFilesUnchanged':len(manifest['files']),'sourceIntegrity':'passed','credentialScan':'passed','htmlIds':'passed','standaloneLegacyServer':False}
if '--verify-only' not in sys.argv:
    dist=root/'dist';dist.mkdir(exist_ok=True)
    output=dist/'Infinity-Urban.zip'
    with zipfile.ZipFile(output,'w',zipfile.ZIP_DEFLATED) as archive:
        for path in files:archive.write(path,path.relative_to(root).as_posix())
    with zipfile.ZipFile(output) as archive:
        if archive.testzip():raise RuntimeError('Pacote inválido')
    report['package']=str(output);report['files']=len(files)
print(json.dumps(report,ensure_ascii=False))
