"""Compare archived evaluated Archicad polygons with a Revit GeometryAudit readback.

Identity comes only from the archived request GUIDs. Surface distances are sampled;
they do not prove equality of the continuous surfaces.
"""
import argparse
import json
import math
from pathlib import Path
import numpy as np
import shapely
from shapely.geometry import Polygon
from compare_column_geometry import probes, surface_distance


def triangulate(geometry):
    triangles=[]
    for body in geometry.get('bodies',[]):
        vertices=np.asarray([[p['x'],p['y'],p['z']] for p in body['vertices']],dtype=float)
        if not np.isfinite(vertices).all():raise ValueError('Nonfinite source vertices')
        for face in body['polygons']:
            ring=vertices[face['vertexIds']]
            if len(ring)<3:raise ValueError('Short source polygon')
            normal=np.cross(ring,np.roll(ring,-1,axis=0)).sum(axis=0)
            length=np.linalg.norm(normal)
            if length<1e-12:raise ValueError('Degenerate source polygon')
            normal/=length;origin=ring[0]
            edges=ring-origin;axis=edges[np.argmax(np.linalg.norm(edges,axis=1))];axis/=np.linalg.norm(axis)
            other=np.cross(normal,axis)
            loops=[ring]+[vertices[h['vertexIds']] for h in face.get('holes',[])]
            if any(np.max(np.abs((loop-origin)@normal))>1e-5 for loop in loops):raise ValueError('Nonplanar source face')
            planar=[np.column_stack(((loop-origin)@axis,(loop-origin)@other)) for loop in loops]
            polygon=Polygon(planar[0],planar[1:])
            if not polygon.is_valid:raise ValueError('Invalid source polygon')
            for triangle in shapely.constrained_delaunay_triangles(polygon).geoms:
                xy=np.asarray(triangle.exterior.coords)[:3]
                # CDT uses input vertices only: retain their exact 3D coordinates.
                all_xy=np.concatenate(planar);all_xyz=np.concatenate(loops)
                points=[]
                for point in xy:
                    distances=np.linalg.norm(all_xy-point,axis=1);i=int(distances.argmin())
                    if distances[i]>1e-8:raise ValueError('Unexpected triangulation vertex')
                    points.append(all_xyz[i])
                points=np.asarray(points)
                if np.dot(np.cross(points[1]-points[0],points[2]-points[0]),normal)<0:points=points[[0,2,1]]
                triangles.append(points)
    if not triangles:return None,None
    vertices=np.asarray(triangles).reshape(-1,3)
    return vertices,np.arange(len(vertices)).reshape(-1,3)


def compare(archives,native_path,manifest_path,max_probes=1000):
    manifest=json.loads(manifest_path.read_text(encoding='utf-8-sig'));frame=manifest['coordinates']['transform']
    angle=frame['rotationRadians'];c,s=math.cos(angle),math.sin(angle)
    rotation=np.asarray([[c,-s,0],[s,c,0],[0,0,1]]);offset=np.asarray([frame['translation'][k] for k in 'xyz'])
    native={r['sourceGuid'].upper():r for r in (json.loads(line) for line in native_path.read_text(encoding='utf-8-sig').splitlines()) if r['sourceGuid']}
    result=[];seen=set()
    for archive in archives:
        data=json.loads(archive.read_text(encoding='utf-8-sig'))
        ids=data['request']['parameters']['addOnCommandParameters']['elements']
        geometry=data['response']['result']['addOnCommandResponse']['geometries3D']
        if len(ids)!=len(geometry):raise ValueError('Positional source response changed')
        for identity,body in zip(ids,geometry):
            key=identity['elementId']['guid'].upper()
            if key in seen:raise ValueError('Duplicate source GUID')
            seen.add(key);row={'sourceGuid':key}
            try:
                if 'error' in body:raise ValueError(str(body['error']))
                a,af=triangulate(body)
                if a is None:row['status']='noEvaluatedSourceBody'
                elif key not in native:row['status']='missingInRevit'
                else:
                    n=native[key]
                    if n['geometryStatus']!='extracted' or not n['meshSaved']:raise ValueError('Native mesh unavailable')
                    b=np.asarray(n['vertices']);bf=np.asarray(n['faces']);a=a@rotation.T+offset
                    bounds=float(max(abs(a.min(0)-b.min(0)).max(),abs(a.max(0)-b.max(0)).max()))
                    def samples(vertices,faces):
                        points=probes(vertices,faces)
                        return points if len(points)<=max_probes else points[np.linspace(0,len(points)-1,max_probes,dtype=int)]
                    distance=max(surface_distance(samples(a,af),b,bf),surface_distance(samples(b,bf),a,af))
                    row.update(status='sampledWithin1mm' if bounds<=.001 and distance<=.001 else 'different',boundsDifferenceMeters=bounds,sampledSurfaceDifferenceMeters=distance,sourceTriangles=len(af),nativeTriangles=len(bf),maximumProbesPerDirection=max_probes)
            except (ValueError,KeyError,IndexError) as error:row.update(status='unverified',error=str(error))
            result.append(row)
    return {'note':'Bidirectional sampled surface comparison; no continuous geometry equality claim. No source/model writes.', 'elements':result}


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--archive',type=Path,action='append',required=True);parser.add_argument('--native',type=Path,required=True);parser.add_argument('--manifest',type=Path,required=True);parser.add_argument('--out',type=Path,required=True);args=parser.parse_args()
    report=compare(args.archive,args.native,args.manifest)
    with args.out.open('x',encoding='utf-8') as stream:json.dump(report,stream,ensure_ascii=False,indent=2)
    from collections import Counter
    print(dict(Counter(r['status'] for r in report['elements'])))
