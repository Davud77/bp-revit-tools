"""Read linear dimension witnesses through Tapir; preserve errors per source GUID."""
from .client import ApiError, chunks, aligned
from .normalize import guid
import math


def grid_anchors(api, snapshot, size):
    known={e['sourceGuid']:e for e in snapshot['elements']}
    owners=sorted({guid(w.get('baseElementId')) for e in snapshot['elements'] if e.get('type')=='Dimension'
                   for w in e.get('dimensionData',{}).get('witnessPoints',[]) if guid(w.get('baseElementId')) and
                   (guid(w.get('baseElementId')) not in known or known[guid(w.get('baseElementId'))].get('type')=='Object')})
    anchors=[]
    for keys in chunks(owners,size):
        batch=[{'elementId':{'guid':key}} for key in keys]
        response=api.call('GetDetailsOfElements',{'elements':batch,'fields':['type','floorIndex','details']})
        objects=[(element,row) for element,row in aligned(response,'detailsOfElements',batch) if row.get('type')=='Object']
        if not objects:continue
        params=api.call('GetGDLParametersOfElements',{'elements':[e for e,_ in objects]})
        for (_,row),(element,values) in zip(objects,aligned(params,'gdlParametersOfElements',[e for e,_ in objects])):
            p={v.get('name'):v.get('value') for v in values.get('parameters',[])}
            # Standard Archicad grid parameter contract, independent of translated library names.
            if p.get('AC_Type_i')!=1 or not {'AC_Length','AC_MarkerText_1','AC_GridLineMat','GridElemen_sp0'}<=p.keys():continue
            d=row.get('details',{});origin=d.get('origin',{});angle=d.get('angle');length=p['AC_Length']
            if not isinstance(angle,(int,float)) or not isinstance(length,(int,float)) or not math.isfinite(angle) or not 0.001<length<100000:continue
            x=float(origin['x']);y=float(origin['y']);z=float(origin.get('z',0))
            anchors.append(dict(sourceGuid=guid(element['elementId']),start=dict(x=x,y=y,z=z),
                end=dict(x=x+math.cos(angle)*length,y=y+math.sin(angle)*length,z=z),label=str(p['AC_MarkerText_1'])))
    snapshot['dimensionAnchors']=anchors


def capture(api, snapshot, batch_size=100):
    try:
        ids = api.call('GetElementsByType', {'elementType': 'Dimension'}).get('elements', [])
        existing = {e['sourceGuid']: e for e in snapshot['elements']}
        for batch in chunks(ids, batch_size):
            headers = api.call('GetDetailsOfElements', {'elements': batch, 'fields': ['type','id','floorIndex','details']})
            data = api.call('GetDimensionData', {'elements': batch})
            heads = dict((guid(e['elementId']), value) for e, value in aligned(headers, 'detailsOfElements', batch))
            for element, value in aligned(data, 'dimensionsData', batch):
                key = guid(element['elementId'])
                row = existing.get(key)
                if row is None:
                    row = dict(heads[key], sourceGuid=key)
                    snapshot['elements'].append(row)
                    existing[key] = row
                if value.get('elementId') and guid(value['elementId'])!=key:
                    raise ApiError('Dimension response identity differs from request')
                row['dimensionData'] = value
        try:grid_anchors(api,snapshot,batch_size)
        except (ApiError,KeyError,TypeError,ValueError) as exc:snapshot['warnings'].append('Dimension grid anchors unavailable: '+str(exc))
        return len(ids)
    except (ApiError, KeyError, TypeError, ValueError) as exc:
        snapshot['warnings'].append('Linear dimension capture unavailable: '+str(exc))
        return 0
