import argparse
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tools'))
sys.path.insert(0,str(ROOT/'exporter'))
from ac2r.jsonio import canonical_digest,write_json
from prepare_supplemental_bodies import prepare


class SupplementalPreparationTests(unittest.TestCase):
    def test_shared_model_pipeline_delegates_a_missing_body_to_the_body_adapter(self):
        import ifcopenshell
        with tempfile.TemporaryDirectory() as temp:
            folder=Path(temp);model=ifcopenshell.file(schema='IFC4');ifc=folder/'model.ifc';model.write(str(ifc))
            project='11111111-1111-4111-8111-111111111111'
            write_json(folder/'manifest.json',{'source':{'projectId':project},'coordinates':{},'elements':[
                {'sourceGuid':'22222222-2222-4222-8222-222222222222','kind':'Roof','raw':{},'payload':{}}]})
            frame={'projectId':project,'matrix':[[1,0,0,0],[0,1,0,0],[0,0,1,0],[0,0,0,1]]};frame['hash']=canonical_digest(frame)
            write_json(folder/'frame.json',frame)
            prepare(argparse.Namespace(out=folder/'out',manifest=folder/'manifest.json',ifc=ifc,frame=folder/'frame.json',resume=False),ifc_model=model)
            plan=json.loads((folder/'out/supplemental.json').read_text(encoding='utf-8'))
            self.assertEqual(plan['outcomes'],{'error':1})
            body=json.loads(Path(plan['batches'][0]['path']).read_text(encoding='utf-8'))
            self.assertEqual(body['elements'],[])
            audit=json.loads((folder/'out/roof-0000/audit.json').read_text(encoding='utf-8'))
            self.assertEqual(audit['roofs'][0]['error'],'IFC Tag match missing/ambiguous')
    def test_surface_colour_and_transparency_of_the_ifc_reach_the_body_record(self):
        import ifcopenshell
        import ifcopenshell.api
        with tempfile.TemporaryDirectory() as temp:
            folder=Path(temp);model=ifcopenshell.api.run('project.create_file')
            ifcopenshell.api.run('root.create_entity',model,ifc_class='IfcProject');ifcopenshell.api.run('unit.assign_unit',model)
            context=ifcopenshell.api.run('context.add_context',model,context_type='Model')
            body=ifcopenshell.api.run('context.add_context',model,context_type='Model',context_identifier='Body',target_view='MODEL_VIEW',parent=context)
            key='22222222-2222-4222-8222-222222222222'
            roof=ifcopenshell.api.run('root.create_entity',model,ifc_class='IfcRoof');roof.Tag=key
            representation=ifcopenshell.api.run('geometry.add_wall_representation',model,context=body,length=2,height=1,thickness=.1)
            ifcopenshell.api.run('geometry.assign_representation',model,product=roof,representation=representation)
            style=ifcopenshell.api.run('style.add_style',model,name='glass')
            ifcopenshell.api.run('style.add_surface_style',model,style=style,ifc_class='IfcSurfaceStyleRendering',
                attributes={'SurfaceColour':{'Name':None,'Red':.2,'Green':.5,'Blue':.8},'Transparency':.7})
            ifcopenshell.api.run('style.assign_representation_styles',model,shape_representation=representation,styles=[style])
            ifc=folder/'model.ifc';model.write(str(ifc))
            project='11111111-1111-4111-8111-111111111111'
            write_json(folder/'manifest.json',{'source':{'projectId':project},'coordinates':{},'elements':[
                {'sourceGuid':key,'kind':'Roof','levelKey':'L','mark':'R-1','raw':{},'payload':{}}]})
            frame={'projectId':project,'matrix':[[1,0,0,0],[0,1,0,0],[0,0,1,0],[0,0,0,1]]};frame['hash']=canonical_digest(frame)
            write_json(folder/'frame.json',frame)
            prepare(argparse.Namespace(out=folder/'out',manifest=folder/'manifest.json',ifc=ifc,frame=folder/'frame.json',resume=False),ifc_model=model)
            plan=json.loads((folder/'out/supplemental.json').read_text(encoding='utf-8'))
            row=json.loads(Path(plan['batches'][0]['path']).read_text(encoding='utf-8'))['elements'][0]
            self.assertEqual(row['surfaceStyles'],[{'rgb':[51,128,204],'transparency':70}])
            self.assertEqual(row['faceStyles'],[0]*len(row['faces']))
