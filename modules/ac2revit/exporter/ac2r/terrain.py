"""Prepare a height-field surface without filling concavities or source holes."""
import math
from collections import defaultdict


def height_patches(triangles):
    heights = defaultdict(set)
    for triangle in triangles:
        for x,y,z in triangle: heights[(round(x,8),round(y,8))].add(round(z,7))
    if all(len(values)==1 for values in heights.values()): return [triangles]
    # A vertical cliff cannot be represented by a single Revit height field.
    # Split actual source planes; never replace two Z values by their mean.
    patches = defaultdict(list)
    for a,b,c in triangles:
        u=[b[i]-a[i] for i in range(3)];v=[c[i]-a[i] for i in range(3)]
        n=[u[1]*v[2]-u[2]*v[1],u[2]*v[0]-u[0]*v[2],u[0]*v[1]-u[1]*v[0]]
        ax,by=-n[0]/n[2],-n[1]/n[2];offset=a[2]-ax*a[0]-by*a[1]
        patches[(round(ax,9),round(by,9),round(offset,8))].append((a,b,c))
    return list(patches.values())


def prepare(geometry):
    import shapely
    from shapely.geometry import Polygon
    parts = []
    for body in geometry['bodies']:
        vertices = [(float(p['x']), float(p['y']), float(p['z'])) for p in body['vertices']]
        if not vertices or not all(math.isfinite(v) for p in vertices for v in p):
            raise ValueError('Invalid Mesh vertices')
        bottom = min(p[2] for p in vertices)
        by_surface = defaultdict(list)
        for face in body['polygons']:
            ring = [vertices[i] for i in face['vertexIds']]
            # Newell normal respects the source body's outward orientation.
            normal = [sum((a[(k+1)%3]-b[(k+1)%3])*(a[(k+2)%3]+b[(k+2)%3])
                          for a,b in zip(ring, ring[1:]+ring[:1])) for k in range(3)]
            if normal[2] <= 1e-12: continue
            origin = ring[0]
            def z(x, y):
                return origin[2]-(normal[0]*(x-origin[0])+normal[1]*(y-origin[1]))/normal[2]
            holes = [[vertices[i] for i in h['vertexIds']] for h in face.get('holes', [])]
            if any(abs(p[2]-z(p[0],p[1])) > 1e-6 for loop in [ring]+holes for p in loop):
                raise ValueError('Non-planar source Mesh polygon; no guessed triangulation')
            polygon = Polygon([(p[0],p[1]) for p in ring], [[(p[0],p[1]) for p in h] for h in holes])
            if not polygon.is_valid or polygon.area <= 1e-12: raise ValueError('Invalid Mesh top polygon')
            surface = face.get('surfaceId',{}).get('guid','').upper()
            if not surface: raise ValueError('Source Mesh surface material is unavailable')
            for triangle in shapely.constrained_delaunay_triangles(polygon).geoms:
                points = [(x,y,z(x,y)) for x,y in list(triangle.exterior.coords)[:3]]
                by_surface[surface].append(points)
        if not by_surface: raise ValueError('Mesh has no upward height-field surface')
        all_polygons = [Polygon([(p[0],p[1]) for p in t]) for ts in by_surface.values() for t in ts]
        footprint = shapely.union_all(all_polygons)
        if abs(sum(p.area for p in all_polygons)-footprint.area) > max(1e-8,footprint.area*1e-9):
            raise ValueError('Overlapping Mesh top surfaces or overhangs')
        for surface, triangles in sorted(by_surface.items()):
            for triangles in height_patches(triangles):
                region = shapely.union_all([Polygon([(p[0],p[1]) for p in t]) for t in triangles])
                regions = [region] if region.geom_type == 'Polygon' else list(region.geoms)
                for region in regions:
                    selected = [t for t in triangles if region.covers(Polygon([(p[0],p[1]) for p in t]).representative_point())]
                    points, ids, faces = [], {}, []
                    for triangle in selected:
                        face = []
                        for p in triangle:
                            key = tuple(round(v,9) for v in p)
                            if key not in ids: ids[key]=len(points); points.append(dict(zip(('x','y','z'),p)))
                            face.append(ids[key])
                        faces.append(face)
                    # Preserve union boundary vertices exactly; do not replace the domain by a convex hull.
                    loops = [list(region.exterior.coords)[:-1]]+[list(r.coords)[:-1] for r in region.interiors]
                    parts.append({'vertices':points,'faces':faces,
                                  'loops':[[{'x':x,'y':y,'z':0} for x,y in loop] for loop in loops],
                                  'surfaceGuid':surface,'bottomElevation':bottom,
                                  'thickness':max(.001,max(p['z'] for p in points)-bottom),
                                  'sourcePlanArea':region.area})
    return {'format':'ac2r.terrain/1','parts':parts}
