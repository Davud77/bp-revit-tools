import copy
import sys
import unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from verify_planar_solids import verify


class PlanarProofTests(unittest.TestCase):
    def cube(self):
        return {'vertices':[[0,0,0],[1,0,0],[1,1,0],[0,1,0],[0,0,1],[1,0,1],[1,1,1],[0,1,1]],'faces':[[0,1,2],[0,2,3],[4,6,5],[4,7,6],[0,4,5],[0,5,1],[1,5,6],[1,6,2],[2,6,7],[2,7,3],[3,7,4],[3,4,0]],'volumeM3':1}
    def test_retriangulation_is_complete_coverage(self):
        a=self.cube();b=copy.deepcopy(a);b['faces'][:2]=[[0,1,3],[1,2,3]]
        self.assertTrue(verify(a,b)['verified'])
    def test_missing_region_rejects_even_with_equal_declared_volume(self):
        a=self.cube();b=copy.deepcopy(a);b['faces'].pop()
        with self.assertRaises(ValueError):verify(a,b)
    def test_moved_plane_rejects(self):
        a=self.cube();b=copy.deepcopy(a)
        for point in b['vertices']:
            if point[2]==1:point[2]+=0.001
        with self.assertRaises(ValueError):verify(a,b)
    def test_volume_difference_rejects(self):
        a=self.cube();b=copy.deepcopy(a);b['volumeM3']=.9
        with self.assertRaises(ValueError):verify(a,b)

if __name__=='__main__':unittest.main()
