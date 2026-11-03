"""Independent complete planar-face coverage check when Revit's Boolean kernel fails.

All triangles are included, rather than point samples. Coordinates are quantised
to 0.1 micrometre for 2D unions; unmatched planes or uncovered regions reject.
"""
import argparse
import json
from pathlib import Path
import numpy as np
import shapely
from shapely.geometry import Polygon


def verify(reference, actual):
    planes=[];maximum=0.;total_error=0.
    def add(mesh,side):
        points=np.asarray(mesh['vertices'],dtype=float)
        if not np.isfinite(points).all():raise ValueError('Nonfinite mesh')
        for face in mesh['faces']:
            triangle=points[face];normal=np.cross(triangle[1]-triangle[0],triangle[2]-triangle[0]);length=np.linalg.norm(normal)
            if length<1e-14:continue
            normal/=length
            for x in normal:
                if abs(x)>1e-10:
                    if x<0:normal=-normal
                    break
            distance=float(normal@triangle[0]);matches=[p for p in planes if np.linalg.norm(p['normal']-normal)<1e-8 and abs(p['distance']-distance)<1e-6]
            if len(matches)>1:raise ValueError('Ambiguous reference planes')
            if not matches:
                if side=='actual':raise ValueError('Actual solid has a foreign plane')
                axis=np.eye(3)[np.argmin(np.abs(normal))];u=np.cross(normal,axis);u/=np.linalg.norm(u);v=np.cross(normal,u)
                plane={'normal':normal,'distance':distance,'u':u,'v':v,'reference':[],'actual':[]};planes.append(plane)
            else:plane=matches[0]
            deviation=float(np.max(abs(triangle@plane['normal']-plane['distance'])))
            if deviation>1e-6:raise ValueError('Face deviates from source plane')
            polygon=Polygon(np.column_stack((triangle@plane['u'],triangle@plane['v'])))
            if not polygon.is_valid:raise ValueError('Invalid projected triangle')
            plane[side].append(polygon)
    add(reference,'reference');add(actual,'actual')
    if not planes:raise ValueError('Empty solids')
    for plane in planes:
        expected=shapely.union_all(plane['reference'],grid_size=1e-7);native=shapely.union_all(plane['actual'],grid_size=1e-7)
        error=expected.symmetric_difference(native,grid_size=1e-7).area
        if error>max(1e-6,expected.area*1e-7):raise ValueError('Planar coverage differs: '+str(error)+' m2')
        maximum=max(maximum,error);total_error+=error
    a=np.asarray(reference['vertices']);b=np.asarray(actual['vertices']);bounds=float(max(abs(a.min(0)-b.min(0)).max(),abs(a.max(0)-b.max(0)).max()))
    volume=abs(reference['volumeM3']-actual['volumeM3'])
    if bounds>1e-6 or volume>1e-5:raise ValueError('Bounds or solid volume differ')
    return {'verified':True,'planes':len(planes),'maximumPlaneXorAreaM2':maximum,'totalXorAreaM2':total_error,'boundsDifferenceM':bounds,'volumeDifferenceM3':volume,'gridMeters':1e-7}


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--input',type=Path,required=True);parser.add_argument('--out',type=Path,required=True);args=parser.parse_args()
    data=json.loads(args.input.read_text(encoding='utf-8'));result=verify(data['reference'],data['actual'])
    args.out.write_text(json.dumps(result,indent=2),encoding='utf-8');print('Verified complete planar coverage')
