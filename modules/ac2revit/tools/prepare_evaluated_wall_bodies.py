"""Prepare archived evaluated bodies for strictly verified cuts of native walls."""
import argparse
import hashlib
import json
from pathlib import Path
from compare_archicad_geometry import triangulate


def prepare(manifest, archives):
    m=json.loads(manifest.read_text(encoding='utf-8-sig'));walls={e['sourceGuid'].upper():e for e in m['elements'] if e['kind']=='Wall'}
    rows=[];seen=set();sources=[];unavailable=[]
    for archive in archives:
        sources.append({'path':str(archive.resolve()),'sha256':hashlib.sha256(archive.read_bytes()).hexdigest()})
        data=json.loads(archive.read_text(encoding='utf-8-sig'));ids=data['request']['parameters']['addOnCommandParameters']['elements'];geometries=data['response']['result']['addOnCommandResponse']['geometries3D']
        if len(ids)!=len(geometries):raise ValueError('Changed positional source response')
        for identity,geometry in zip(ids,geometries):
            key=identity['elementId']['guid'].upper()
            if key not in walls or key in seen:raise ValueError('Foreign or duplicate source wall')
            seen.add(key);bodies=[]
            for body in geometry.get('bodies',[]):
                vertices,faces=triangulate({'bodies':[body]});points=[];lookup={};indices=[]
                for triangle in vertices[faces]:
                    face=[]
                    for point in triangle:
                        vertex=tuple(round(float(n),9) for n in point)
                        if vertex not in lookup:lookup[vertex]=len(points);points.append(dict(zip('xyz',map(float,point))))
                        face.append(lookup[vertex])
                    indices.append(face)
                bodies.append({'vertices':points,'faces':indices})
            if bodies:rows.append({'sourceGuid':key,'mark':walls[key]['mark'],'bodies':bodies})
            else:unavailable.append({'sourceGuid':key,'error':geometry.get('error','No evaluated source body')})
    return {'format':'ac2r.evaluated-wall-bodies/1','projectId':m['source']['projectId'],'coordinates':m['coordinates'],'sourceArchives':sources,'elements':rows,'unavailable':unavailable}


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--manifest',type=Path,required=True);p.add_argument('--archive',type=Path,action='append',required=True);p.add_argument('--out',type=Path,required=True);a=p.parse_args()
    result=prepare(a.manifest,a.archive)
    with a.out.open('x',encoding='utf-8') as stream:json.dump(result,stream,ensure_ascii=False)
    print('Prepared',len(result['elements']),'walls; unavailable',len(result['unavailable']))
