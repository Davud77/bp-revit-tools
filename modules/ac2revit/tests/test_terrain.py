import sys
from pathlib import Path
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'exporter'))
from ac2r.terrain import prepare


def body():
    return {'bodies':[{'vertices':[dict(x=x,y=y,z=z) for x,y,z in
        [(0,0,1),(5,0,1),(5,5,1),(0,5,1),(1,1,1),(1,2,1),(2,2,1),(2,1,1),(0,0,-2)]],
        'polygons':[{'vertexIds':[0,1,2,3],'holes':[{'vertexIds':[4,5,6,7]}], 'surfaceId':{'guid':'MATERIAL'}}]}]}


class TerrainTests(unittest.TestCase):
    def test_vertical_step_uses_separate_native_height_fields(self):
        g={'bodies':[{'vertices':[dict(x=x,y=y,z=z) for x,y,z in
            [(0,0,1),(1,0,1),(1,1,1),(0,1,1),(1,0,0),(2,0,0),(2,1,0),(1,1,0)]],
            'polygons':[{'vertexIds':[0,1,2,3],'surfaceId':{'guid':'M'}},{'vertexIds':[4,5,6,7],'surfaceId':{'guid':'M'}}]}]}
        parts=prepare(g)['parts'];self.assertEqual(len(parts),2)
        self.assertEqual({p['vertices'][0]['z'] for p in parts},{0,1})
        self.assertAlmostEqual(sum(p['sourcePlanArea'] for p in parts),2)
    def test_holes_and_material_and_elevations_are_retained(self):
        part=prepare(body())['parts'][0]
        self.assertEqual(len(part['loops']),2)
        self.assertAlmostEqual(part['sourcePlanArea'],24)
        self.assertEqual(part['surfaceGuid'],'MATERIAL')
        self.assertEqual(part['thickness'],3)
        self.assertTrue(all(p['z']==1 for p in part['vertices']))

    def test_nonplanar_ngon_is_not_silently_flattened(self):
        g=body();g['bodies'][0]['vertices'][2]['z']=2
        with self.assertRaisesRegex(ValueError,'Non-planar'): prepare(g)

    def test_overlapping_top_surfaces_are_rejected(self):
        g=body();g['bodies'][0]['polygons']*=2
        with self.assertRaisesRegex(ValueError,'Overlapping'): prepare(g)

    def test_missing_surface_is_explicit(self):
        g=body();g['bodies'][0]['polygons'][0].pop('surfaceId')
        with self.assertRaisesRegex(ValueError,'material'): prepare(g)

    def test_slope_is_preserved(self):
        g=body()
        for p in g['bodies'][0]['vertices'][:-1]:p['z']=p['x']*.2+1
        part=prepare(g)['parts'][0]
        self.assertTrue(all(abs(p['z']-(p['x']*.2+1))<1e-8 for p in part['vertices']))


if __name__=='__main__':unittest.main()
