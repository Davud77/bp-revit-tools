"""Archive native Mesh bodies, including meshes omitted by an IFC translator."""
from .client import ApiError, aligned, chunks
from .jsonio import archive_reference
from .normalize import guid


def capture(api, snapshot, target, batch_size):
    meshes = [e for e in snapshot['elements'] if e.get('type') == 'Mesh']
    coverage = {'expected': len(meshes), 'retrieved': 0, 'errors': 0, 'missing': 0,
                'coordinateSystem': 'Archicad world coordinates, metres'}
    snapshot['meshGeometryCoverage'] = coverage
    if not meshes:
        return
    # A missing command on older Tapir must not abort unrelated model capture.
    try:
        api.call('ChangeWindow', {'windowType': '3DModel'})
        for batch in chunks(meshes, batch_size):
            ids = [{'elementId': {'guid': e['sourceGuid']}} for e in batch]
            response = api.call('Get3DGeometryOfElements', {'elements': ids})
            rows = list(aligned(response, 'geometries3D', ids))
            reference = archive_reference(api.last_archive, target,
                '/response/result/addOnCommandResponse/geometries3D')
            for n, (element, (_, geometry)) in enumerate(zip(batch, rows)):
                if 'error' in geometry or not geometry.get('bodies'):
                    element['meshGeometryError'] = geometry.get('error', {'message': 'Empty native Mesh geometry'})
                    coverage['errors'] += 1
                    continue
                element['meshGeometryReference'] = dict(reference,
                    jsonPointer=reference['jsonPointer']+'/'+str(n),
                    sourceGuid=guid(ids[n]), coordinateSystem='ArchicadWorldMetres')
                coverage['retrieved'] += 1
                try:
                    from .terrain import prepare
                    element['terrain'] = prepare(geometry)
                except (ValueError, KeyError, TypeError, IndexError) as error:
                    element['terrainError'] = str(error)
                    snapshot['warnings'].append('Mesh '+element['sourceGuid']+': '+str(error))
    except (ApiError, ValueError, TypeError, KeyError) as error:
        snapshot['warnings'].append('Native Mesh geometry unavailable: '+str(error))
    coverage['missing'] = coverage['expected']-coverage['retrieved']-coverage['errors']
