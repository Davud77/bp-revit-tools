"""Paper-space Revit grids from vector fills; lengths in internal feet, angles radians.

Archicad's normalized line coordinates are transformed by R(angle) diag(hX,hY).
Scale-dependent fills are frozen at the explicitly selected reference scale.
"""
import hashlib
import json
import math


def named_geometry(label, grids):
    geometry={'target':'Drafting','orientation':'ToView','solid':False,'grids':grids}
    digest=hashlib.sha256(json.dumps(geometry,sort_keys=True,separators=(',',':')).encode()).hexdigest()[:16]
    return 'АС '+label[:55]+' · '+digest,geometry


def vector_pattern(fill, reference_scale=100):
    if fill.get('subType')!='Vector':return None
    if not math.isfinite(reference_scale) or reference_scale<=0:raise ValueError('Invalid hatch reference scale')
    sx=float(fill['horizontalSpacing']);sy=float(fill['verticalSpacing']);angle=float(fill['angle'])
    if not all(math.isfinite(x) for x in (sx,sy,angle)) or min(sx,sy)<=0:raise ValueError('Invalid source vector spacing')
    factor=1/(.3048*(reference_scale if fill.get('scaleWithPlan') else 1))
    sx*=factor;sy*=factor;c=math.cos(angle);s=math.sin(angle)
    def transform(x,y):return (c*sx*x-s*sy*y,s*sx*x+c*sy*y)
    grids=[]
    for item in fill.get('lineItems',[]):
        direction=float(item['direction']);frequency=float(item['frequency']);shift=float(item['offsetLine'])
        if not all(math.isfinite(x) for x in (direction,frequency,shift)) or frequency<=0:raise ValueError('Invalid source vector line')
        co,si=math.cos(direction),math.sin(direction);tx,ty=transform(co,si);length=math.hypot(tx,ty);tx/=length;ty/=length
        dx,dy=transform(-si*frequency+co*shift,co*frequency+si*shift)
        offset=dx*(-ty)+dy*tx
        parts=item.get('lineLengths',[])
        if len(parts)%2 or any(not math.isfinite(v) or v<0 for v in parts):raise ValueError('Invalid source dash/gap pairs')
        segments=[v*length*(1 if i%2==0 else -1) for i,v in enumerate(parts)]
        if segments and not any(v<0 for v in segments):raise ValueError('Missing source dash gap')
        origin=transform(item['offset']['x'],item['offset']['y'])
        if not 1e-6<=offset<=100:raise ValueError('Source grid outside Revit drafting limits')
        grids.append({'angle':math.atan2(ty,tx)%(2*math.pi),'origin':list(origin),'offset':offset,'shift':dx*tx+dy*ty,'segments':segments})
    if not grids or len(grids)>1024:raise ValueError('Missing/excessive source vector lines')
    return named_geometry(fill['name'],grids)


def portable_pattern(kind):
    """Explicit conventional analogues, not a reconstruction of symbol arcs."""
    def grid(degrees,mm,segments=(),origin=(0,0),shift=0):
        return {'angle':math.radians(degrees),'origin':[x/.3048/1000 for x in origin],
                'offset':mm/.3048/1000,'shift':shift/.3048/1000,'segments':[v/.3048/1000 for v in segments]}
    definitions={
        'insulation':[grid(45,2,(2,-2)),grid(135,2,(2,-2),origin=(1.414213562,0))],
        'horizontal':[grid(0,1.5)],
        'wood':[grid(0,2),grid(0,2,(3,-1),origin=(0,1),shift=1)],
        'sand':[grid(0,1,(0,-1),shift=.5)],
        'steel':[grid(45,1.5)],
        'tile':[grid(0,3),grid(90,3)],
        'cross':[grid(45,1.5),grid(135,1.5)],
        'concrete':[grid(45,2,(1.5,-.5,0,-.5)),grid(45,2,(1.5,-.5,0,-.5),origin=(1.25,1))],
    }
    if kind not in definitions:raise ValueError('Unknown portable hatch family: '+kind)
    return named_geometry('аналог '+kind,definitions[kind])
