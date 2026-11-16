import sys
import unittest
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
sys.path.insert(0, str(ROOT / 'exporter'))

import prepare_trimmed_walls as trimmed  # noqa: E402


def prism(profile, thickness, along=(0, 0), direction=(1, 0)):
    """A wall body: the profile (s, z) extruded across the wall; returns vertices and triangles."""
    n = len(profile)
    ux, uy = direction
    nx, ny = -uy, ux
    vertices = []
    for t in (0.0, thickness):
        for s, z in profile:
            vertices.append((along[0] + ux * s + nx * t, along[1] + uy * s + ny * t, z))
    triangles = []
    # fan triangulation of a convex-or-star polygon is enough for the test shapes
    for t, flip in ((n, False), (0, True)):
        for i in range(1, n - 1):
            tri = [t, t + i, t + i + 1]
            triangles.append(tri[::-1] if flip else tri)
    for i in range(n):
        j = (i + 1) % n
        triangles.append([i, j, n + j])
        triangles.append([i, n + j, n + i])
    return np.array(vertices, dtype=float), np.array(triangles, dtype=int)


class TrimmedWallTests(unittest.TestCase):
    axis = ({'x': 0.0, 'y': 0.0}, {'x': 8.0, 'y': 0.0})

    def test_wall_under_a_stair_is_reported_with_its_profile(self):
        # 8 m wall, 3 m high at the left end, running down a stair to 1 m at the right.
        profile = [(0, 0), (8, 0), (8, 1), (0, 3)]
        vertices, triangles = prism(profile, 0.2)
        found = trimmed.profile_of(vertices, triangles, *self.axis)
        self.assertIsNotNone(found)
        points, bbox, thickness = found
        self.assertAlmostEqual(thickness, 0.2)
        self.assertEqual(len(points), 4)
        self.assertEqual(sorted(round(p['z'], 3) for p in points), [0.0, 0.0, 1.0, 3.0])
        self.assertAlmostEqual(points[0]['y'], 0.1)  # the middle of the body across the wall

    def test_plain_rectangle_is_not_trimmed(self):
        vertices, triangles = prism([(0, 0), (8, 0), (8, 3), (0, 3)], 0.2)
        self.assertIsNone(trimmed.profile_of(vertices, triangles, *self.axis))

    def test_body_that_is_not_a_prism_across_the_wall_is_skipped(self):
        vertices, triangles = prism([(0, 0), (8, 0), (8, 3), (0, 3)], 0.2)
        vertices[2, 1] += 0.05  # a vertex off both faces
        self.assertIsNone(trimmed.profile_of(vertices, triangles, *self.axis))

    def test_collinear_points_are_merged(self):
        merged = trimmed.simplify([(0, 0), (1, 1), (2, 2), (3, 3), (3, 0)])
        self.assertEqual(merged, [(0, 0), (3, 3), (3, 0)])


if __name__ == '__main__':
    unittest.main()
