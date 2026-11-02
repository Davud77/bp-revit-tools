import argparse
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
sys.path[:0]=[str(Path(__file__).resolve().parents[1]/'tools'),str(Path(__file__).resolve().parents[1]/'exporter')]
import revit_session as worker
from export_options import json_deliverable,ifc_deliverable,KINDS,restrict_pipeline,selected_kinds
from ac2r.client import ApiError


class ExportOptionTests(unittest.TestCase):
    def test_json_connection_survives_unavailable_ifc_translators(self):
        def call(command,*args,**kwargs):
            if command in ('GetIFCExportTranslators','GetCurrentWindowType'):
                raise ApiError('unsupported',command=command,code=4010)
            return dict(projectPath='same.pln') if command=='GetProjectInfo' else dict(version='1.7')
        with patch.object(worker,'TapirClient') as client,patch.object(worker,'project_identity',return_value='p'):
            client.return_value.call.side_effect=call
            info=worker.probe(19723)
        self.assertEqual(info['translators'],[]);self.assertEqual(info['projectId'],'p')

    def test_default_all_and_empty_scope_is_rejected(self):
        self.assertEqual(set(selected_kinds()),set(KINDS))
        with self.assertRaises(ValueError):selected_kinds([])
        with self.assertRaises(ValueError):selected_kinds(['Unknown'])

    def test_json_scope_retains_source_and_marks_excluded_host(self):
        manifest=dict(source={},elements=[dict(sourceGuid='w',kind='Wall'),dict(sourceGuid='d',kind='Door',dependencies=['w'])],issues=[])
        with tempfile.TemporaryDirectory() as folder:
            result=json_deliverable(manifest,['Door'],Path(folder)/'manifest.json')
            self.assertEqual(len(result['elements']),1)
            self.assertEqual(result['elements'][0]['status'],'unsupported')
            self.assertEqual(len(manifest['elements']),2)
            self.assertEqual(result['source']['exportKinds'],['Door'])

    def test_ifc_filter_removes_filling_preserving_wall_and_void(self):
        import ifcopenshell
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);model=ifcopenshell.file(schema='IFC4')
            wall=model.create_entity('IfcWall',GlobalId=ifcopenshell.guid.new(),Tag='W')
            opening=model.create_entity('IfcOpeningElement',GlobalId=ifcopenshell.guid.new())
            door=model.create_entity('IfcBuildingElementProxy',GlobalId=ifcopenshell.guid.new(),Tag='D')
            space=model.create_entity('IfcSpace',GlobalId=ifcopenshell.guid.new())
            model.create_entity('IfcRelVoidsElement',GlobalId=ifcopenshell.guid.new(),RelatingBuildingElement=wall,RelatedOpeningElement=opening)
            model.create_entity('IfcRelFillsElement',GlobalId=ifcopenshell.guid.new(),RelatingOpeningElement=opening,RelatedBuildingElement=door)
            manifest=dict(elements=[dict(sourceGuid='W',kind='Wall'),dict(sourceGuid='D',kind='Door'),
                dict(sourceGuid='S',kind='Zone',raw=dict(ifcIdentifiers=dict(ifcId=space.GlobalId)))])
            model.write(str(root/'source.ifc'));ifc_deliverable(manifest,['Wall'],root/'source.ifc',root/'selected.ifc')
            result=ifcopenshell.open(str(root/'selected.ifc'))
            self.assertEqual(len(result.by_type('IfcWall')),1)
            self.assertEqual(len(result.by_type('IfcOpeningElement')),1)
            self.assertEqual(len(result.by_type('IfcRelVoidsElement')),1)
            self.assertFalse(result.by_type('IfcBuildingElementProxy'))
            self.assertFalse(result.by_type('IfcSpace'))
            self.assertEqual(len(ifcopenshell.open(str(root/'source.ifc')).by_type('IfcBuildingElementProxy')),1)

    def test_ifc_window_recovery_preserves_scope_and_translator(self):
        class Api:
            calls=[]
            saves=0
            def call(self,command,parameters=None):
                self.calls.append((command,parameters))
                if command=='GetProjectInfo':return dict(projectPath='same')
                if command=='IFCFileOperation':
                    self.saves+=1
                    if self.saves==1:raise ApiError('bad window',command=command,code=-2130313109)
                    Path(parameters['ifcFilePath']).write_bytes(b'x'*2048)
                return dict(success=True)
        with tempfile.TemporaryDirectory() as folder:
            api=Api();worker.export_ifc(api,dict(projectPath='same'),'Exact',Path(folder)/'model.ifc')
            saves=[p for c,p in api.calls if c=='IFCFileOperation']
            self.assertEqual(len(saves),2);self.assertEqual(saves[0],saves[1])
            self.assertIn(('ChangeWindow',dict(windowType='3DModel')),api.calls)

    def test_json_mode_never_requests_ifc_or_geometry_preparation(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);target=root/'artifacts/sessions/json'
            args=argparse.Namespace(output=target,port=19723,project_id='p',project_path='s.pln',mode='json',kinds=['Wall'],translator='',properties=False)
            info=dict(projectId='p',project=dict(projectPath='s.pln'),translators=[])
            def export(args):
                path=Path(args.output);path.mkdir();(path/'manifest.json').write_text(json.dumps(dict(source={},elements=[],issues=[])))
            with patch.object(worker,'ROOT',root),patch.object(worker,'probe',return_value=info),patch.object(worker,'export',side_effect=export),patch.object(worker,'export_ifc') as ifc,patch.object(worker,'prepare_pipeline') as prepare:
                state=worker.run(args)
            ifc.assert_not_called();prepare.assert_not_called()
            self.assertEqual(state['status'],'ready');self.assertTrue(Path(state['pipeline']).is_file())
            self.assertFalse((target/'source/model.ifc').exists())

    def test_pipeline_scope_is_explicit_and_persistent(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'pipeline.json';path.write_text('{"pipelineFormat":"ac2r.pipeline/1"}')
            restrict_pipeline(path,['Wall'])
            self.assertEqual(json.loads(path.read_text())['exportKinds'],['Wall'])

    def test_ifc_mode_skips_detailed_json_and_does_not_replace_build_pointer(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);sessions=root/'artifacts/sessions';sessions.mkdir(parents=True)
            pointer=sessions/'last-completed.json';pointer.write_text('{"session":"previous"}')
            args=argparse.Namespace(output=sessions/'ifc',port=19723,project_id='p',project_path='s.pln',mode='ifc',kinds=None,translator='Exact',properties=False)
            info=dict(projectId='p',project=dict(projectPath='s.pln'),translators=[dict(name='Exact')])
            def save(api,project,translator,path):path.write_bytes(b'x'*2048)
            with patch.object(worker,'ROOT',root),patch.object(worker,'probe',return_value=info),patch.object(worker,'export') as export,patch.object(worker,'TapirClient') as client,patch.object(worker,'export_ifc',side_effect=save),patch.object(worker,'prepare_pipeline') as prepare:
                client.return_value.call.return_value=dict(elements=[])
                state=worker.run(args)
            export.assert_not_called();prepare.assert_not_called()
            self.assertEqual(state['status'],'exported');self.assertTrue(Path(state['outputs']['ifc']).is_file())
            self.assertFalse((args.output/'source/manifest.json').exists())
            self.assertEqual(json.loads(pointer.read_text())['session'],'previous')
