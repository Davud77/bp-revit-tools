"""Read-only dimension capture against an explicitly named open PLN."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'exporter'))
from ac2r.client import TapirClient
from ac2r.dimension_capture import capture
from ac2r.normalize import normalize
from ac2r.jsonio import write_json

def run(args):
    api=TapirClient(args.port,args.out/'raw',timeout=60)
    before=api.call('GetProjectInfo')
    if Path(before.get('projectPath','')).resolve()!=args.expected_project.resolve():
        raise ValueError('The expected PLN is not the active Archicad project')
    snapshot=json.loads(args.base_snapshot.read_text(encoding='utf-8-sig'))
    stories=api.call('GetStories')
    baseline=[(s['index'],s['level']) for s in snapshot['stories']['stories']]
    current=[(s['index'],s['level']) for s in stories['stories']]
    if baseline!=current:raise ValueError('Source stories differ from the supplied baseline')
    snapshot['elements']=[e for e in snapshot['elements'] if e.get('type')!='Dimension']
    snapshot.setdefault('warnings',[])
    count=capture(api,snapshot)
    if before!=api.call('GetProjectInfo'):raise ValueError('The active project changed during capture')
    snapshot['dimensionCapture']=dict(project=before,utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
        sourceFileSha256=hashlib.sha256(args.expected_project.read_bytes()).hexdigest(),baseline=str(args.base_snapshot.resolve()))
    write_json(args.out/'snapshot.json',snapshot)
    manifest=normalize(snapshot,args.project_id);write_json(args.out/'manifest.json',manifest)
    rows=[e for e in manifest['elements'] if e['kind']=='Dimension']
    report=dict(captured=count,ready=sum(e['status']=='ready' for e in rows),unsupported=sum(e['status']!='ready' for e in rows),
        source=before,issues=[dict(sourceGuid=e['sourceGuid'],reason=e.get('reason','')) for e in rows if e['status']!='ready'],warnings=snapshot['warnings'])
    write_json(args.out/'dimensions-report.json',report)
    print(json.dumps({k:report[k] for k in ('captured','ready','unsupported')},ensure_ascii=False))

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--base-snapshot',type=Path,required=True);p.add_argument('--expected-project',type=Path,required=True)
    p.add_argument('--project-id',required=True);p.add_argument('--out',type=Path,required=True);p.add_argument('--port',type=int,default=19723)
    run(p.parse_args())
