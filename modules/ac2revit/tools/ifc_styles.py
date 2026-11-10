"""Surface colours and transparency of IFC geometry, carried with the mesh into Revit.

IfcOpenShell triangulates an element and reports, for every triangle, which IfcSurfaceStyle it had (`material_ids`,
-1 = none) and the styles themselves (`materials`: diffuse colour and transparency, 0 = opaque, 1 = clear). Without
this the DirectShape copies of windows, doors, railings and other IFC-only elements receive Revit's default grey,
so glass is opaque and facade colours are lost. The styles are written beside the mesh as

    surfaceStyles: [{"rgb": [r, g, b], "transparency": 0..100}, ...]   (unique, 0-255 and percent as in Revit)
    faceStyles:    [index or -1 per triangle]                           (same order as `faces`)

and are omitted when no triangle has a style.
"""


def _channel(value):
    return max(0, min(255, int(round(float(value) * 255))))


def style_of(material):
    """(r, g, b, transparency percent) of an IfcOpenShell style, or None when it has no usable colour."""
    # IfcOpenShell fills in its own default colour per IFC class (instance id 0) when the file has no style; that
    # is not source data and must not be written to the model.
    try:
        if material.instance_id() <= 0:
            return None
    except Exception:
        pass
    colour = None
    try:
        colour = material.diffuse
        colour = None if colour is None else (colour.r(), colour.g(), colour.b())
    except Exception:
        colour = None
    if colour is None:
        try:
            raw = material.get_color()
            colour = (raw.r(), raw.g(), raw.b())
        except Exception:
            return None
    try:
        transparency = float(material.transparency) if material.has_transparency() else 0.0
    except Exception:
        transparency = 0.0
    if not all(0 <= c <= 1.0000001 for c in colour) or not 0 <= transparency <= 1.0000001:
        return None
    return (_channel(colour[0]), _channel(colour[1]), _channel(colour[2]), max(0, min(100, int(round(transparency * 100)))))


class StyleAccumulator:
    """Merges the per-triangle styles of the parts of one element, in the order the parts are concatenated."""

    def __init__(self):
        self.styles, self.faces, self._index = [], [], {}

    def add(self, geometry, triangle_count):
        """geometry: IfcOpenShell `shape.geometry`; triangle_count: its number of triangles."""
        ids = list(getattr(geometry, 'material_ids', []) or [])
        materials = list(getattr(geometry, 'materials', []) or [])
        mapped = {}
        for number, material in enumerate(materials):
            style = style_of(material)
            if style is None:
                mapped[number] = -1
                continue
            if style not in self._index:
                self._index[style] = len(self.styles)
                self.styles.append({'rgb': list(style[:3]), 'transparency': style[3]})
            mapped[number] = self._index[style]
        if len(ids) != triangle_count:
            ids = [-1] * triangle_count
        self.faces.extend(mapped.get(int(i), -1) for i in ids)

    def apply(self, row):
        """Adds surfaceStyles/faceStyles to a mesh record when any triangle has a style."""
        if self.styles and any(i >= 0 for i in self.faces):
            row['surfaceStyles'] = self.styles
            row['faceStyles'] = self.faces
        return row

    def add_computed(self, styles, faces):
        """Merges styles already reduced by another accumulator (`styles`, `faces`)."""
        remap = {}
        for number, style in enumerate(styles):
            key = (*style['rgb'], style['transparency'])
            if key not in self._index:
                self._index[key] = len(self.styles)
                self.styles.append({'rgb': list(style['rgb']), 'transparency': style['transparency']})
            remap[number] = self._index[key]
        self.faces.extend(remap.get(i, -1) if i >= 0 else -1 for i in faces)


def styles_of_shape(geometry, triangle_count):
    """(styles, per-triangle indices) of one IfcOpenShell geometry."""
    acc = StyleAccumulator()
    acc.add(geometry, triangle_count)
    return acc.styles, acc.faces
