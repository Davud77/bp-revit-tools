from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'exporter'))
from ac2r.client import ApiError
from ac2r.jsonio import file_sha256, write_json
from ac2r.mesh_capture import capture


class MeshCaptureTests(unittest.TestCase):
    def test_positional_errors_do_not_assign_another_mesh_body(self):
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder)
            source = {'elements': [{'type': 'Mesh', 'sourceGuid': x} for x in ('FIRST', 'SECOND')], 'warnings': []}
            bodies = {'bodies': [{'vertices': [{'x': 1, 'y': 2, 'z': 3}], 'polygons': []}]}
            response = {'geometries3D': [{'error': {'code': 1}}, bodies]}
            class Api:
                last_archive = target/'raw/geometry.json'
                def call(self, command, parameters):
                    if command == 'ChangeWindow': return {'success': True}
                    write_json(self.last_archive, {'response': {'result': {'addOnCommandResponse': response}}})
                    return response
            api = Api()
            capture(api, source, target, 100)
            self.assertNotIn('meshGeometryReference', source['elements'][0])
            ref = source['elements'][1]['meshGeometryReference']
            self.assertEqual(ref['sourceGuid'], 'SECOND')
            self.assertTrue(ref['jsonPointer'].endswith('/1'))
            self.assertEqual(ref['sha256'], file_sha256(api.last_archive))
            self.assertEqual(source['meshGeometryCoverage']['retrieved'], 1)
            self.assertEqual(source['meshGeometryCoverage']['errors'], 1)
            self.assertNotIn('bodies', source['elements'][1])

    def test_older_tapir_is_reported_without_aborting_export(self):
        class Api:
            def call(self, command, parameters):
                if command == 'ChangeWindow': return {'success': True}
                raise ApiError('Command not registered')
        source = {'elements': [{'type': 'Mesh', 'sourceGuid': 'ONE'}], 'warnings': []}
        capture(Api(), source, Path('.'), 100)
        self.assertEqual(source['meshGeometryCoverage']['missing'], 1)
        self.assertIn('Command not registered', source['warnings'][0])

    def test_mismatched_response_is_not_attached_to_source(self):
        class Api:
            def call(self, command, parameters):
                return {'geometries3D': []}
        source = {'elements': [{'type': 'Mesh', 'sourceGuid': 'ONE'}], 'warnings': []}
        capture(Api(), source, Path('.'), 100)
        self.assertEqual(source['meshGeometryCoverage']['missing'], 1)
        self.assertNotIn('meshGeometryReference', source['elements'][0])

    def test_projects_without_mesh_do_not_change_window(self):
        class Api:
            def call(self, *args): raise AssertionError('Unexpected API call')
        source = {'elements': [], 'warnings': []}
        capture(Api(), source, Path('.'), 100)
        self.assertEqual(source['meshGeometryCoverage']['expected'], 0)


if __name__ == '__main__': unittest.main()
