import shutil
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
sys.path.insert(0, str(ROOT / 'exporter'))

try:
    import ifcopenshell
    import ifcopenshell.guid
except ImportError:  # pragma: no cover
    ifcopenshell = None

import ifc_parts  # noqa: E402


def model():
    f = ifcopenshell.file(schema='IFC4')
    g = ifcopenshell.guid.new
    origin = f.createIfcCartesianPoint((0.0, 0.0, 0.0))
    placement = f.createIfcLocalPlacement(None, f.createIfcAxis2Placement3D(origin, None, None))
    context = f.createIfcGeometricRepresentationContext(None, 'Model', 3, 1e-5, f.createIfcAxis2Placement3D(origin, None, None), None)
    units = f.createIfcUnitAssignment([f.createIfcSIUnit(None, 'LENGTHUNIT', None, 'METRE')])
    f.createIfcProject(g(), None, 'Проект #1', None, None, None, None, [context], units)

    def box(name):
        profile = f.createIfcRectangleProfileDef('AREA', name, None, 1.0, 1.0)
        solid = f.createIfcExtrudedAreaSolid(profile, None, f.createIfcDirection((0.0, 0.0, 1.0)), 1.0)
        shape = f.createIfcShapeRepresentation(context, 'Body', 'SweptSolid', [solid])
        return f.createIfcProductDefinitionShape(None, None, [shape])

    wall = f.createIfcWall(g(), None, 'Стена #5 (с решёткой)', None, None, placement, box('wall'), 'WALL-TAG')
    opening = f.createIfcOpeningElement(g(), None, 'Проём', None, None, placement, box('opening'), None)
    door = f.createIfcDoor(g(), None, 'Дверь', None, None, placement, box('door'), None)
    other = f.createIfcFurnishingElement(g(), None, 'Посторонний', None, None, placement, box('other'), None)
    f.createIfcRelVoidsElement(g(), None, None, None, wall, opening)
    f.createIfcRelFillsElement(g(), None, None, None, opening, door)
    return f, wall, opening, door, other


@unittest.skipUnless(ifcopenshell, 'ifcopenshell required')
class IfcPartsTests(unittest.TestCase):
    def setUp(self):
        self.folder = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.folder, True)

    def test_part_is_self_contained_and_excludes_unrelated_products(self):
        f, wall, opening, door, other = model()
        source = self.folder / 'model.ifc'
        f.write(str(source))
        # A multi-line entity, as some exporters write long lists.
        text = source.read_bytes().replace(b",'WALL-TAG')", b",\n'WALL-TAG')")
        source.write_bytes(text)
        index = ifc_parts.StepIndex(source)
        self.assertIn(wall.GlobalId, index.guids)
        self.assertEqual(index.tags, {})  # Tags are recorded only when they hold a source GUID
        mask = index.closure([index.guids[door.GlobalId]])
        path = index.write_parts({'door': mask}, self.folder / 'parts')['door']
        part = ifcopenshell.open(str(path))
        ids = {p.GlobalId for p in part.by_type('IfcProduct')}
        # The filling pulls in its opening, and the opening its host, as the openings stage needs.
        self.assertEqual(ids, {wall.GlobalId, opening.GlobalId, door.GlobalId})
        self.assertEqual(part.by_type('IfcProject')[0].Name, 'Проект #1')
        self.assertEqual(len(part.by_type('IfcRelFillsElement')), 1)
        self.assertEqual(part.by_type('IfcWall')[0].Name, 'Стена #5 (с решёткой)')

    def test_part_keeps_the_surface_style_of_its_geometry(self):
        f, wall, opening, door, other = model()
        solid = door.Representation.Representations[0].Items[0]
        rendering = f.createIfcSurfaceStyleRendering(f.createIfcColourRgb(None, 0.2, 0.5, 0.8), 0.7, None, None, None, None, None, None, 'FLAT')
        style = f.createIfcSurfaceStyle('Стекло', 'BOTH', [rendering])
        f.createIfcStyledItem(solid, [f.createIfcPresentationStyleAssignment([style])], None)
        other_solid = other.Representation.Representations[0].Items[0]
        f.createIfcStyledItem(other_solid, [f.createIfcPresentationStyleAssignment([style])], None)
        source = self.folder / 'model.ifc'
        f.write(str(source))
        index = ifc_parts.StepIndex(source)
        mask = index.closure([index.guids[door.GlobalId]])
        part = ifcopenshell.open(str(index.write_parts({'door': mask}, self.folder / 'parts')['door']))
        # Only the style of the selected geometry travels with the part; the unrelated product's style does not.
        self.assertEqual(len(part.by_type('IfcStyledItem')), 1)
        self.assertEqual(part.by_type('IfcSurfaceStyleRendering')[0].Transparency, 0.7)

    def test_string_hash_is_not_a_reference(self):
        args = ifc_parts.split_arguments(b"'a#12,b',#7,(#8,#9),'it''s #3'")
        self.assertEqual(args, [b"'a#12,b'", b'#7', b'(#8,#9)', b"'it''s #3'"])


if __name__ == '__main__':
    unittest.main()
