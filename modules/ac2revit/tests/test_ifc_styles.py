import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

try:
    import ifcopenshell
    import ifcopenshell.api
    import ifcopenshell.geom
except ImportError:  # pragma: no cover
    ifcopenshell = None

import ifc_styles  # noqa: E402


def glass_window():
    f = ifcopenshell.api.run('project.create_file')
    ifcopenshell.api.run('root.create_entity', f, ifc_class='IfcProject')
    ifcopenshell.api.run('unit.assign_unit', f)
    context = ifcopenshell.api.run('context.add_context', f, context_type='Model')
    body = ifcopenshell.api.run('context.add_context', f, context_type='Model', context_identifier='Body', target_view='MODEL_VIEW', parent=context)
    window = ifcopenshell.api.run('root.create_entity', f, ifc_class='IfcWindow')
    representation = ifcopenshell.api.run('geometry.add_wall_representation', f, context=body, length=2, height=1, thickness=.1)
    ifcopenshell.api.run('geometry.assign_representation', f, product=window, representation=representation)
    return f, window, representation


@unittest.skipUnless(ifcopenshell, 'ifcopenshell required')
class SurfaceStyleTests(unittest.TestCase):
    def shape(self, window):
        settings = ifcopenshell.geom.settings()
        settings.set('use-world-coords', True)
        self.keep = ifcopenshell.geom.create_shape(settings, window)  # the geometry is owned by the shape object
        return self.keep.geometry

    def test_transparent_glass_style_is_read_per_triangle(self):
        f, window, representation = glass_window()
        style = ifcopenshell.api.run('style.add_style', f, name='glass')
        ifcopenshell.api.run('style.add_surface_style', f, style=style, ifc_class='IfcSurfaceStyleRendering',
                             attributes={'SurfaceColour': {'Name': None, 'Red': .2, 'Green': .5, 'Blue': .8}, 'Transparency': .7})
        ifcopenshell.api.run('style.assign_representation_styles', f, shape_representation=representation, styles=[style])
        geometry = self.shape(window)
        triangles = len(geometry.faces) // 3
        row = ifc_styles.StyleAccumulator()
        row.add(geometry, triangles)
        result = row.apply({})
        self.assertEqual(result['surfaceStyles'], [{'rgb': [51, 128, 204], 'transparency': 70}])
        self.assertEqual(result['faceStyles'], [0] * triangles)

    def test_unstyled_geometry_adds_nothing(self):
        f, window, representation = glass_window()
        geometry = self.shape(window)
        row = ifc_styles.StyleAccumulator()
        row.add(geometry, len(geometry.faces) // 3)
        self.assertEqual(row.apply({}), {})

    def test_styles_of_parts_are_merged_without_duplicates(self):
        row = ifc_styles.StyleAccumulator()
        row.add_computed([{'rgb': [1, 2, 3], 'transparency': 0}], [0, -1])
        row.add_computed([{'rgb': [9, 9, 9], 'transparency': 50}, {'rgb': [1, 2, 3], 'transparency': 0}], [0, 1, -1])
        self.assertEqual(row.styles, [{'rgb': [1, 2, 3], 'transparency': 0}, {'rgb': [9, 9, 9], 'transparency': 50}])
        self.assertEqual(row.faces, [0, -1, 1, 0, -1])


if __name__ == '__main__':
    unittest.main()
