import sys
import unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'exporter'))
from ac2r.zone_constraints import constraints


class ZoneConstraintTests(unittest.TestCase):
    def rows(self,label='Собственный + 1 (Верх)',top=3.65):
        return [dict(propertyValue=dict(status='normal',value=v)) for v in (3.65,label,-.1,top,0)]

    def test_link_is_verified_by_independent_elevations(self):
        result=constraints(self.rows(),0,{0:dict(level=0),1:dict(level=3.75)})
        self.assertEqual(result,dict(height=3.65,bottomOffset=0,relativeTopStory=1,topOffset=-.1))

    def test_mismatched_elevation_cannot_guess_a_link(self):
        with self.assertRaises(ValueError):constraints(self.rows(),0,{0:dict(level=0),1:dict(level=4)})

    def test_unknown_localized_relation_preserves_height_without_claiming_link(self):
        result=constraints(self.rows(label='Unlinked'),0,{0:dict(level=0),1:dict(level=3.75)})
        self.assertFalse(result['upperRelationVerified'])
        self.assertNotIn('relativeTopStory',result)

    def test_unavailable_property_does_not_become_zero(self):
        values=self.rows();values[0]['propertyValue']['status']='notAvailable'
        with self.assertRaises(ValueError):constraints(values,0,{0:dict(level=0),1:dict(level=3.75)})
