"""Export deliverables and scope, separate from the immutable diagnostic source capture."""
import copy
import json
import shutil
from pathlib import Path
from ac2r.jsonio import write_json
from ac2r.normalize import digest

KINDS = ('Wall','Slab','Door','Window','Zone','Column','Beam','Object','Lamp','Roof',
         'Shell','Mesh','Morph','Stair','Railing','CurtainWall','Skylight','Opening','Grid','Dimension')


def selected_kinds(value=None):
    selected = list(KINDS) if value is None else list(dict.fromkeys(value))
    if not selected or set(selected)-set(KINDS):
        raise ValueError('Выберите хотя бы одну известную категорию экспорта.')
    return selected


def json_deliverable(manifest, selected, target):
    result = copy.deepcopy(manifest)
    result['elements'] = [e for e in result['elements'] if e['kind'] in selected]
    selected_ids = {e['sourceGuid'] for e in result['elements']}
    for e in result['elements']:
        missing = set(e.get('dependencies', []))-selected_ids
        if missing:
            e.update(status='unsupported',reason='Хост исключён из экспорта; выберите его категорию: '+', '.join(sorted(missing)))
            e['contentHash']=digest({k:v for k,v in e.items() if k!='contentHash'})
    result['issues'] = [i for i in result.get('issues',[]) if i.get('sourceGuid') in selected_ids]
    result['source']['exportKinds']=list(selected)
    result.pop('manifestHash',None);result['manifestHash']=digest(result)
    write_json(target,result)
    write_json(target.parent/'mapping.json',dict(allowPartial=True,zoneMode='room',roomAreaToleranceSquareMeters=1.0,connectWalls=True,families={}))
    return result


def ifc_deliverable(manifest, selected, source, target):
    """Remove excluded physical products by exact identity; retain spatial containers and wall voids."""
    target.parent.mkdir(parents=True,exist_ok=True)
    if set(selected)==set(KINDS):
        shutil.copyfile(source,target);return dict(filtered=False)
    import ifcopenshell
    import ifcopenshell.api.root
    from source_ifc import SourceIfcIndex
    model=ifcopenshell.open(str(source));index=SourceIfcIndex(manifest,model)
    keep=set();remove=set()
    def visit(product,owner,seen):
        if product.id() in seen:return
        seen.add(product.id())
        own=index.source_guid(product)
        if own and own!=owner:return
        keep.add(product.id())
        for rel in getattr(product,'IsDecomposedBy',()):
            for child in rel.RelatedObjects:visit(child,owner,seen)
    for e in manifest['elements']:
        for product in index.matches(e):
            if e['kind'] in selected:visit(product,e['sourceGuid'].upper(),set())
    for product in model.by_type('IfcProduct'):
        if any(product.is_a(kind) for kind in ('IfcSite','IfcBuilding','IfcBuildingStorey')):continue
        if product.is_a('IfcOpeningElement'):
            if any(rel.RelatingBuildingElement.id() in keep for rel in product.VoidsElements):continue
        if product.is_a('IfcGrid') and 'Grid' in selected:continue
        if product.id() not in keep:remove.add(product.id())
    # remove_product can remove dependent openings; re-resolve ids before each deletion.
    for step_id in sorted(remove,reverse=True):
        try:product=model.by_id(step_id)
        except RuntimeError:continue
        if product is not None:ifcopenshell.api.root.remove_product(model,product=product)
    model.write(str(target))
    # Reopen the final file and check excluded source identities cannot leak through.
    reopened=ifcopenshell.open(str(target));check=SourceIfcIndex(manifest,reopened)
    if any(check.matches(e) for e in manifest['elements'] if e['kind'] not in selected):
        raise ValueError('Проверка IFC: в итоговом файле остались исключённые элементы.')
    return dict(filtered=True,removedProducts=len(remove),keptProducts=len(reopened.by_type('IfcProduct')))


def publish_outputs(session, manifest, selected, mode):
    folder=Path(session)/'export';folder.mkdir(exist_ok=True)
    outputs={}
    if mode!='ifc':
        json_deliverable(manifest,selected,folder/'manifest.json');outputs['json']=str(folder/'manifest.json')
    if mode!='json':
        audit=ifc_deliverable(manifest,selected,Path(session)/'source/model.ifc',folder/'model.ifc')
        write_json(folder/'ifc-scope-report.json',audit);outputs['ifc']=str(folder/'model.ifc')
    return outputs


def restrict_pipeline(path, selected):
    path=Path(path);plan=json.loads(path.read_text(encoding='utf-8-sig'))
    plan['exportKinds']=selected_kinds(selected);write_json(path,plan)
