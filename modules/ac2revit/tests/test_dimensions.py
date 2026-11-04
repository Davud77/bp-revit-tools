import copy
from pathlib import Path
import sys
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'exporter'))
from ac2r.dimension_capture import capture, grid_anchors
from ac2r.normalize import normalize
from ac2r.client import ApiError
from test_pipeline import snapshot,PROJECT,WALL

DIM='11111111-2222-4333-8444-555555555556'
OTHER='11111111-2222-4333-8444-555555555557'

def data():
    return dict(direction=dict(x=1,y=0),dimensionLinePosition=dict(x=0,y=-1),witnessPoints=[
        dict(baseElementId=dict(guid=WALL),coordinate=dict(x=10,y=20),dimensionValue=0),
        dict(baseElementId=dict(guid=WALL),coordinate=dict(x=15,y=20),dimensionValue=5)])

class Dimensions(unittest.TestCase):
    def test_grid_anchors_use_parameter_contract_even_for_already_captured_objects(self):
        s=dict(elements=[dict(sourceGuid=WALL,type='Object'),dict(sourceGuid=DIM,type='Dimension',dimensionData=data())])
        class Api:
            def call(self,name,params):
                if name=='GetDetailsOfElements':return dict(detailsOfElements=[dict(type='Object',details=dict(origin=dict(x=2,y=3),angle=0))])
                return dict(gdlParametersOfElements=[dict(parameters=[dict(name=k,value=v) for k,v in dict(AC_Type_i=1,AC_Length=10,AC_MarkerText_1='А',AC_GridLineMat=1,GridElemen_sp0=0).items()])])
        grid_anchors(Api(),s,100)
        self.assertEqual(s['dimensionAnchors'],[dict(sourceGuid=WALL,start=dict(x=2,y=3,z=0),end=dict(x=12,y=3,z=0),label='А')])

    def test_mismatched_dimension_response_is_not_attached_to_requested_guid(self):
        class Api:
            def call(self,name,params):
                if name=='GetElementsByType':return dict(elements=[dict(elementId=dict(guid=DIM))])
                if name=='GetDetailsOfElements':return dict(detailsOfElements=[dict(type='Dimension')])
                return dict(dimensionsData=[dict(data(),elementId=dict(guid=OTHER))])
        s=dict(elements=[],warnings=[]);self.assertEqual(capture(Api(),s),0)
        self.assertNotIn('dimensionData',s['elements'][0]);self.assertIn('identity differs',s['warnings'][0])

    def test_associative_payload_and_normalized_coordinates(self):
        s=snapshot();s['elements'].append(dict(sourceGuid=DIM,type='Dimension',floorIndex=5,dimensionData=data()))
        d=next(e for e in normalize(s,PROJECT)['elements'] if e['kind']=='Dimension')
        self.assertEqual(d['status'],'ready');self.assertEqual(d['dependencies'],[WALL])
        self.assertEqual(d['payload']['direction'],dict(x=1.0,y=0.0,z=0.0))
        self.assertEqual(d['payload']['witnesses'][1]['sourceGuid'],WALL)

    def test_static_missing_or_failed_witnesses_are_explicitly_unsupported(self):
        for value in ({},{'error':{'code':1}},data()):
            if 'witnessPoints' in value:del value['witnessPoints'][0]['baseElementId']
            s=snapshot();s['elements'].append(dict(sourceGuid=DIM,type='Dimension',floorIndex=5,dimensionData=value))
            d=next(e for e in normalize(s,PROJECT)['elements'] if e['kind']=='Dimension')
            self.assertEqual(d['status'],'unsupported');self.assertTrue(d['reason'])

    def test_positional_errors_remain_on_their_own_guid_and_capture_is_idempotent(self):
        ids=[dict(elementId=dict(guid=DIM)),dict(elementId=dict(guid=OTHER))]
        class Api:
            def call(self,name,params):
                if name=='GetElementsByType':return dict(elements=ids)
                if name=='GetDetailsOfElements':return dict(detailsOfElements=[dict(type='Dimension',floorIndex=5)]*2)
                return dict(dimensionsData=[{'error':{'code':1}},data()])
        s=dict(elements=[],warnings=[]);self.assertEqual(capture(Api(),s),2);capture(Api(),s)
        self.assertEqual(len(s['elements']),2);self.assertIn('error',s['elements'][0]['dimensionData'])
        self.assertEqual(s['elements'][1]['sourceGuid'],OTHER);self.assertEqual(len(s['elements'][1]['dimensionData']['witnessPoints']),2)

    def test_incomplete_response_is_reported_without_shifting_witnesses(self):
        class Api:
            def call(self,name,params):
                if name=='GetElementsByType':return dict(elements=[dict(elementId=dict(guid=DIM))])
                if name=='GetDetailsOfElements':return dict(detailsOfElements=[dict(type='Dimension',floorIndex=5)])
                return dict(dimensionsData=[])
        s=dict(elements=[],warnings=[]);self.assertEqual(capture(Api(),s),0);self.assertEqual(s['elements'],[]);self.assertEqual(len(s['warnings']),1)

if __name__=='__main__':unittest.main()
