"""Walls whose exact IFC body is trimmed at the top (by a stair, a roof, a slab).

Archicad cuts a wall with Solid Element Operations; the API still reports the nominal wall (a straight prism), while the
IFC "exact geometry" body is the cut one. For every straight wall whose IFC body is a prism through its thickness, the upper
outline of the body in the wall's vertical plane is compared with the nominal rectangle; walls that differ are written with the
profile (in source coordinates, at the middle of the body) so that Revit can build them as a native wall by profile.
Openings are not part of the profile: doors and windows are cut later, exactly as for any other wall.
"""
import argparse
import json
import math
from pathlib import Path
import sys

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'exporter'))
from ac2r.jsonio import file_sha256, write_json
from ifc_coordinates import apply_frame, infer_frame
from prepare_column_bodies import body_products
from source_ifc import SourceIfcIndex

TOLERANCE = 0.002  # 2 mm


def top_envelope(triangles):
    """Upper boundary of a planar triangulated region as a polyline [(s, z)], steps included.
    triangles: array (n, 3, 2) of (s, z)."""
    breaks = sorted({round(float(p[0]), 6) for tri in triangles for p in tri})
    points = []
    for left, right in zip(breaks, breaks[1:]):
        if right - left < 1e-6:
            continue
        mid = (left + right) / 2
        best = None
        for tri in triangles:
            if min(p[0] for p in tri) - 1e-9 > mid or max(p[0] for p in tri) + 1e-9 < mid:
                continue
            top_mid = _ztop(tri, mid)
            if top_mid is None:
                continue
            if best is None or top_mid > best[0]:
                best = (top_mid, tri)
        if best is None:
            continue
        tri = best[1]
        z_left = _ztop(tri, left + 1e-7)
        z_right = _ztop(tri, right - 1e-7)
        if z_left is None or z_right is None:
            continue
        points.append((left, z_left))
        points.append((right, z_right))
    return points


def _ztop(tri, s):
    values = []
    for i in range(3):
        a, b = tri[i], tri[(i + 1) % 3]
        if (a[0] - s) * (b[0] - s) <= 0 and abs(a[0] - b[0]) > 1e-12:
            u = (s - a[0]) / (b[0] - a[0])
            values.append(a[1] + u * (b[1] - a[1]))
    return max(values) if values else None


def simplify(points, tolerance=0.001):
    out = []
    for p in points:
        if out and abs(p[0] - out[-1][0]) < 0.002 and abs(p[1] - out[-1][1]) < 0.002:
            continue
        out.append(p)
    changed = True
    while changed and len(out) > 2:
        changed = False
        for i in range(1, len(out) - 1):
            a, b, c = out[i - 1], out[i], out[i + 1]
            cross = (b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0])
            span = math.hypot(c[0] - a[0], c[1] - a[1])
            if span > 0 and abs(cross) / span < tolerance:
                del out[i]
                changed = True
                break
    return out


def profile_of(vertices, triangles, begin, end):
    """(profile, bbox, thickness) of a straight wall body, or None when it is not a prism through the thickness
    or its top is not trimmed. begin/end: wall axis in source coordinates."""
    dx, dy = end['x'] - begin['x'], end['y'] - begin['y']
    length = math.hypot(dx, dy)
    if length < 1e-6:
        return None
    ux, uy = dx / length, dy / length
    nx, ny = -uy, ux
    s = (vertices[:, 0] - begin['x']) * ux + (vertices[:, 1] - begin['y']) * uy
    t = (vertices[:, 0] - begin['x']) * nx + (vertices[:, 1] - begin['y']) * ny
    t0, t1 = float(t.min()), float(t.max())
    thickness = t1 - t0
    if thickness < 0.005 or np.any((np.abs(t - t0) > 0.001) & (np.abs(t - t1) > 0.001)):
        return None
    z = vertices[:, 2]
    zmin, zmax = float(z.min()), float(z.max())
    face = [tri for tri in triangles if all(abs(t[i] - t1) < 0.001 for i in tri)]
    if not face:
        return None
    region = np.array([[(s[i], z[i]) for i in tri] for tri in face])
    envelope = simplify(top_envelope(region))
    if len(envelope) < 2 or all(p[1] >= zmax - TOLERANCE for p in envelope):
        return None  # the top is the nominal one
    s0, s1 = min(p[0] for p in envelope), max(p[0] for p in envelope)
    polygon = [(s0, zmin), (s1, zmin)] + list(reversed(envelope))
    polygon = simplify(polygon)
    if len(polygon) < 3:
        return None
    tm = (t0 + t1) / 2
    points = [dict(x=float(begin['x'] + ux * p[0] + nx * tm), y=float(begin['y'] + uy * p[0] + ny * tm), z=float(p[1])) for p in polygon]
    bbox = dict(min=[float(vertices[:, 0].min()), float(vertices[:, 1].min()), zmin], max=[float(vertices[:, 0].max()), float(vertices[:, 1].max()), zmax])
    return points, bbox, thickness


def prepare(manifest_path, ifc_path, out, ifc_model=None, frame=None):
    import ifcopenshell
    import ifcopenshell.geom
    out.mkdir(parents=True, exist_ok=True)
    m = json.loads(manifest_path.read_text(encoding='utf-8'))
    model = ifc_model if ifc_model is not None else ifcopenshell.open(str(ifc_path))
    identities = SourceIfcIndex(m, model)
    frame = frame or infer_frame(m, model, identities)
    settings = ifcopenshell.geom.settings()
    settings.set('use-world-coords', True)
    rows, checked = [], 0
    for e in m['elements']:
        if e['kind'] != 'Wall' or e.get('status') != 'ready':
            continue
        d = e['raw'].get('details', {})
        if d.get('geometryType', 'Straight') != 'Straight' or abs(d.get('arcAngle', 0)) > 1e-9 or d.get('profileType', 'Normal') != 'Normal':
            continue
        key = e['sourceGuid']
        try:
            matches = identities.matches(e)
            if len(matches) != 1:
                continue
            vertices, triangles, cursor, shapes = [], [], 0, []
            for part in body_products(matches[0], source_owner=identities.source_guid, owner_key=key.upper()):
                shape = ifcopenshell.geom.create_shape(settings, part)
                shapes.append(shape)  # the geometry belongs to the shape object
                raw = np.array(shape.geometry.verts).reshape(-1, 3)
                faces = np.array(shape.geometry.faces, dtype=int).reshape(-1, 3)
                if len(raw) == 0 or len(faces) == 0:
                    raise ValueError('empty body')
                vertices.append(apply_frame(raw, frame))
                triangles.append(faces + cursor)
                cursor += len(raw)
            if not vertices:
                continue
            checked += 1
            found = profile_of(np.concatenate(vertices), np.concatenate(triangles), d['begCoordinate'], d['endCoordinate'])
            if found is None:
                continue
            points, bbox, thickness = found
            rows.append(dict(sourceGuid=key, profile=points, bbox=bbox, thickness=thickness, mark=e.get('mark', '')))
        except Exception:
            continue
    ifc_hash = file_sha256(ifc_path)
    write_json(out / 'trimmed-walls.json', dict(projectId=m['source']['projectId'], ifcSha256=ifc_hash, elements=rows, checkedWalls=checked))
    return dict(checkedWalls=checked, trimmedWalls=len(rows))


if __name__ == '__main__':
    p = argparse.ArgumentParser()
    p.add_argument('--manifest', type=Path, required=True)
    p.add_argument('--ifc', type=Path, required=True)
    p.add_argument('--ifc-part', type=Path, help='IFC part to read from (ifc_parts.py); --ifc stays the recorded source')
    p.add_argument('--out', type=Path, required=True)
    a = p.parse_args()
    from ifc_reader_wait import wait_for_reader
    wait_for_reader(ROOT)
    part = None
    if a.ifc_part:
        import ifcopenshell
        part = ifcopenshell.open(str(a.ifc_part))
    print(json.dumps(prepare(a.manifest, a.ifc, a.out, ifc_model=part), indent=2), flush=True)
