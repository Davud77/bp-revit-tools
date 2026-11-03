import math
import sys
import unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from hatch_patterns import vector_pattern
from test_hatch_mapping import HatchMappingTests
from prepare_hatch_mapping import prepare


class VectorPatternTests(unittest.TestCase):
    def fill(self):
        return {'subType':'Vector','name':'custom','horizontalSpacing':.002,'verticalSpacing':.004,'angle':0,
                'scaleWithPlan':False,'lineItems':[{'direction':0,'frequency':2,'offsetLine':1,'offset':{'x':1,'y':2},'lineLengths':[1,2,0,1]}]}
    def test_anisotropic_transform_and_dash_gap_units(self):
        _,spec=vector_pattern(self.fill());g=spec['grids'][0]
        self.assertAlmostEqual(g['offset']*.3048,.008);self.assertAlmostEqual(g['shift']*.3048,.002)
        self.assertEqual([round(v*.3048,6) for v in g['segments']],[.002,-.004,0,-.002])
        self.assertEqual([round(v*.3048,6) for v in g['origin']],[.002,.008])
    def test_rotation_and_model_scale(self):
        f=self.fill();f.update(angle=math.pi/2,scaleWithPlan=True)
        _,spec=vector_pattern(f,50);g=spec['grids'][0]
        self.assertAlmostEqual(g['angle'],math.pi/2);self.assertAlmostEqual(g['offset']*.3048,.008/50)
    def test_diagonal_anisotropic_offset_uses_normal_projection(self):
        f=self.fill();f['lineItems'][0].update(direction=math.pi/4,offsetLine=0)
        _,spec=vector_pattern(f);g=spec['grids'][0]
        length=math.hypot(.002/math.sqrt(2),.004/math.sqrt(2))
        self.assertAlmostEqual(g['offset']*.3048,2*.002*.004/length)
    def test_nonfinite_missing_and_invalid_dashes_reject(self):
        for change in [{'frequency':0},{'direction':float('nan')},{'lineLengths':[1]},{'lineLengths':[-1,2]}]:
            f=self.fill();f['lineItems'][0].update(change)
            with self.assertRaises(ValueError):vector_pattern(f)
    def test_missing_template_pattern_uses_vector_and_never_black_fill(self):
        fixture=HatchMappingTests();fixture.setUp();fixture.source['fills'][0].update(self.fill())
        rows,_=prepare(fixture.manifest,fixture.source,fixture.target,{},portable={})
        self.assertEqual(rows[0]['match'],'sourceVector');self.assertFalse(rows[0]['targetGeometry']['solid'])
    def test_explicit_symbol_fallback_and_native_preference(self):
        fixture=HatchMappingTests();fixture.setUp();fixture.source['fills'][0]['subType']='Symbol'
        policy={'Concrete':{'native':['Concrete target'],'fallback':'concrete'}}
        rows,_=prepare(fixture.manifest,fixture.source,fixture.target,{},portable=policy)
        self.assertEqual(rows[0]['targetName'],'Concrete target');self.assertEqual(rows[0]['match'],'analogue')
        fixture.target['patterns'].pop()
        rows,_=prepare(fixture.manifest,fixture.source,fixture.target,{},portable=policy)
        self.assertEqual(rows[0]['match'],'portableAnalogue')

if __name__=='__main__':unittest.main()
