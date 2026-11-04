"""Explicit per-element plan cover fills and library-object section overrides.

Material cut fills stay on the compound layers. Unsupported source orientation
or library-internal graphics are reported rather than guessed.
"""
import argparse
import copy
import hashlib
import json
import math
from pathlib import Path
import uuid
from prepare_hatch_mapping import prepare


def guid(value):
    if not isinstance(value, dict):return ''
    return value.get('guid', '') or guid(value.get('attributeId'))


def collect(manifest):
    fills={guid(f.get('attributeId')).upper():f for f in manifest.get('fills',[]) if 'attributeId' in f}
    requests=[];issues=[]
    for element in manifest.get('elements',[]):
        source=element['sourceGuid'];kind=element['kind'];d=element.get('raw',{}).get('details',{})
        def issue(slot,reason):issues.append({'sourceGuid':source,'kind':kind,'slot':slot,'status':'unsupported','reason':reason})
        def add(slot,fill_id,fg,bg):
            fill=fills.get(guid(fill_id).upper())
            if not fill:issue(slot,'В снимке отсутствует определение исходной штриховки.');return
            if not isinstance(fg,int) or not isinstance(bg,int):issue(slot,'Не заданы исходные перья штриховки.');return
            key=str(uuid.uuid5(uuid.UUID(manifest['source']['projectId']),source.upper()+':'+slot)).upper()
            requests.append({'sourceGuid':source,'kind':kind,'levelKey':element['levelKey'],'slot':slot,
                'material':{'attributeId':{'guid':key},'name':element.get('mark','') or kind,
                    'cutFillIndex':fill['index'],'cutFillPen':fg,'cutFillBackgroundPen':bg,'cutFillOrientation':'Global'}})
        if kind=='Slab' and isinstance(d.get('floorFill'),dict) and d['floorFill'].get('use'):
            f=d['floorFill'];orientation=f.get('orientation',{})
            identity=orientation.get('type')=='Global' and all(math.isclose(orientation.get(k,float('nan')),v,abs_tol=1e-9)
                for k,v in [('matrix00',1),('matrix10',0),('matrix01',0),('matrix11',1)])
            origin=orientation.get('origin',{})
            if f.get('use3DHatching'):issue('cover','Штриховка из поверхности требует отдельного сопоставления покрытия.')
            elif not identity or any(not math.isclose(origin.get(k,float('nan')),0,abs_tol=1e-9) for k in ('x','y')):
                issue('cover','Локальное начало, поворот или деформация исходной штриховки пока не воспроизводятся.')
            else:add('cover',f.get('fillId'),f.get('foregroundPen'),f.get('backgroundPen'))
        if kind in ('Object','Lamp') and 'sectionFillId' in d:
            if d.get('useObjectSectionAttributes') is False:
                add('cut',d['sectionFillId'],d.get('sectionFillPen'),d.get('sectionFillBackgroundPen'))
            else:issue('cut','Используются внутренние атрибуты GDL: одного sectionFillId недостаточно для достоверного переноса.')
        if any(isinstance(d.get(k),dict) and d[k].get('overridden') for k in ('cutFillPen','cutFillBackgroundPen')):
            issue('cutPens','Переопределённые перья среза элемента требуют послойного учёта; материал не заменён общей заливкой.')
        if isinstance(d.get('coverFill'),dict) and d['coverFill'].get('use'):
            issue('cover','Штриховка покрытия этого типа элемента ещё не поддержана.')
    return requests,issues


def build(manifest,target,config):
    requests,issues=collect(manifest)
    if not requests:return [],issues
    synthetic=dict(manifest,materials=[r['material'] for r in requests])
    source={'projectId':manifest['source']['projectId'],'materialDifferences':[],
        'fills':manifest.get('fills',[]),'penTables':manifest.get('penTables',[])}
    catalog=copy.deepcopy(target)
    catalog['materials']=[{'sourceGuid':r['material']['attributeId']['guid'],'elementId':i+1} for i,r in enumerate(requests)]
    catalog['compoundTypes']=[]
    rows,inventory=prepare(synthetic,source,catalog,config['rules'],True,config.get('portable'),config.get('referenceScale',100))
    graphics={r['materialGuid']:r for r in rows};assignments=[]
    by_key={r['material']['attributeId']['guid']:r for r in requests}
    for row in inventory:
        request=by_key[row['materialGuid']];entry={k:v for k,v in request.items() if k!='material'}
        if row['materialGuid'] in graphics:
            entry['graphics']=graphics[row['materialGuid']];assignments.append(entry)
        else:issues.append(dict(entry,status='unmapped',reason=row['reason']))
    return assignments,issues


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--manifest',type=Path,required=True);p.add_argument('--target',type=Path,required=True)
    p.add_argument('--rules',type=Path,required=True);p.add_argument('--out',type=Path,required=True);a=p.parse_args()
    def read(path):return json.loads(path.read_text(encoding='utf-8-sig'))
    manifest=read(a.manifest);assignments,issues=build(manifest,read(a.target),read(a.rules))
    plan={'format':'ac2r.element-hatches/1','projectId':manifest['source']['projectId'],
        'sourceManifestSha256':hashlib.sha256(a.manifest.read_bytes()).hexdigest(),'assignments':assignments,'issues':issues}
    a.out.parent.mkdir(parents=True,exist_ok=True);a.out.write_text(json.dumps(plan,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'assignments':len(assignments),'unsupported':len(issues)},ensure_ascii=True))
