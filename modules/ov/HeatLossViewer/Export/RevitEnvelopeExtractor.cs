using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace HeatLossViewer.Export;

/// <summary>Envelope areas from room solid subfaces. Exposure remains a reviewable beta classification.</summary>
public sealed class RevitEnvelopeExtractor : IDisposable
{
    private readonly Document doc;
    private readonly string source;
    private readonly Transform transform;
    private readonly SpatialElementGeometryCalculator calculator;
    private readonly Dictionary<string,List<Solid>> solids = [];
    private static double M(double v) => UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Meters);
    public RevitEnvelopeExtractor(Document doc, SpatialElementBoundaryOptions options, string source, Transform transform)
    {
        this.doc = doc; this.source = source; this.transform = transform;
        calculator = new SpatialElementGeometryCalculator(doc, options);
    }

    public void ReadSurfaces(Room room, RoomData dto)
    {
        using var result = calculator.CalculateSpatialElementGeometry(room);
        using var solid = result.GetGeometry();
        foreach (Face face in solid.Faces)
        {
            var info = result.GetBoundaryFaceInfo(face);
            if (info is null || info.Count == 0)
            {
                AddSurface(dto, face, null, null, "free");
                continue;
            }
            foreach (var sub in info)
            {
                using var patch = sub.GetSubface();
                var reference = sub.SpatialBoundaryElement;
                Element? host = null;
                string? hostKey = null;
                if (reference.HostElementId != ElementId.InvalidElementId)
                {
                    host = doc.GetElement(reference.HostElementId);
                    if (host is not null) hostKey = source + "/" + host.UniqueId;
                }
                else if (doc.GetElement(reference.LinkInstanceId) is RevitLinkInstance link)
                {
                    host = link.GetLinkDocument()?.GetElement(reference.LinkedElementId);
                    if (host is not null) hostKey = source + "/" + link.UniqueId + "/" + host.UniqueId;
                }
                AddSurface(dto, patch, host, hostKey, sub.SubfaceType.ToString());
            }
        }
        dto.EnvelopeStatus = dto.Surfaces.Count > 0 ? "beta" : "demo";
    }

    private void AddSurface(RoomData room, Face face, Element? host, string? hostKey, string subfaceType)
    {
        double area = UnitUtils.ConvertFromInternalUnits(face.Area, UnitTypeId.SquareMeters);
        if (area < 1e-8) return;
        var box = face.GetBoundingBox();
        var normal = transform.OfVector(face.ComputeNormal((box.Min + box.Max) * .5));
        var point = transform.OfPoint(face.Evaluate((box.Min + box.Max) * .5));
        using var mesh = face.Triangulate();
        var vertices = mesh.Vertices.Select(transform.OfPoint).ToList();
        string kind = Math.Abs(normal.Z) > .5 ? (normal.Z > 0 ? "roof" : "floor") : "wall";
        string exposure = "unknown", classification = "demo";
        if (host is Wall wall)
        {
            kind = "wall";
            exposure = wall.WallType.Function == WallFunction.Exterior ? "outside" : "inside";
            classification = "wall-type-function-beta";
        }
        else if (host is RoofBase) { kind = "roof"; exposure = "outside"; classification = "roof-element-beta"; }
        else if (host is Floor || host is Ceiling) classification = "horizontal-needs-review";
        var surface = new SurfaceData
        {
            Key = room.Key + "/face/" + room.Surfaces.Count,
            HostKey = hostKey ?? "", ElementId = host?.Id.Value.ToString() ?? "",
            TypeKey = RevitOvLibrary.TypeKey(host, hostKey ?? ""),
            TypeName = host is null ? subfaceType : doc.Equals(host.Document) ? doc.GetElement(host.GetTypeId())?.Name ?? host.Name : host.Name,
            Kind = kind, Exposure = exposure, ClassificationSource = classification,
            GrossArea = area, Normal = [normal.X, normal.Y, normal.Z], Point = [M(point.X),M(point.Y),M(point.Z)],
            BoundsMin = [M(vertices.Min(p=>p.X)),M(vertices.Min(p=>p.Y)),M(vertices.Min(p=>p.Z))],
            BoundsMax = [M(vertices.Max(p=>p.X)),M(vertices.Max(p=>p.Y)),M(vertices.Max(p=>p.Z))]
        };
        if(kind=="wall")
        {
            surface.Triangles=WallCutGeometry.Triangles(face,transform);
            if(host is Wall && host.Document.Equals(doc))
            {
                try
                {
                    if(!solids.TryGetValue(host.UniqueId,out var cached)) solids[host.UniqueId]=cached=WallCutGeometry.Solids(host);
                    if(!WallCutGeometry.Apply(surface,face,cached,transform)) surface.AreaSource="room-face-cut-fallback-beta";
                }
                catch (Exception ex) { surface.AreaSource="room-face-cut-fallback-beta";surface.AreaNote=ex.Message; }
            }
        }
        room.Surfaces.Add(surface);
    }

    public void ReadOpenings(List<Room> rooms, Dictionary<long, RoomData> exported, BuildingData data)
    {
        if (exported.Count == 0) return;
        // The lowest floor and highest horizontal boundary are demo assumptions, clearly retained in JSON.
        double lowest = exported.Values.Min(r => r.BaseElevation);
        double highest = exported.Values.Max(r => r.BaseElevation + r.Height);
        foreach (var dto in exported.Values)
        foreach (var surface in dto.Surfaces.Where(s => s.Exposure == "unknown" && s.Kind != "wall"))
        {
            bool bottom = surface.Kind == "floor" && Math.Abs(dto.BaseElevation - lowest) < .1;
            bool top = surface.Kind == "roof" && Math.Abs(dto.BaseElevation + dto.Height - highest) < .1;
            surface.Exposure = bottom ? "ground" : top ? "outside" : "inside";
            surface.ClassificationSource = "demo-horizontal-assumption";
        }
        foreach (var dto in exported.Values)
        foreach (var s in dto.Surfaces.Where(s => s.Kind == "wall" && s.Exposure != "outside"))
        {
            // A reviewable demo heuristic for imported families and walls whose Function was not set.
            double z = (s.BoundsMin[2]+s.BoundsMax[2])*.5;
            bool neighbor = new[] { .45, .9 }.Any(offset => exported.Values.Any(r =>
                z >= r.BaseElevation-.01 && z <= r.BaseElevation+r.Height+.01 &&
                Contains(r,s.Point[0]+s.Normal[0]*offset,s.Point[1]+s.Normal[1]*offset)));
            s.Exposure = neighbor ? "inside" : "outside";
            s.ClassificationSource = "demo-room-adjacency";
        }
        var phases = rooms.Select(r => r.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() ?? r.CreatedPhaseId).DistinctBy(id => id.Value)
            .Select(id => doc.GetElement(id)).OfType<Phase>().ToList();
        var filter = new ElementMulticategoryFilter(new[] { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors });
        var tagged=(Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SourceOpeningMetadata.SchemaId)==null?Enumerable.Empty<Element>():new FilteredElementCollector(doc).WherePasses(new Autodesk.Revit.DB.ExtensibleStorage.ExtensibleStorageFilter(SourceOpeningMetadata.SchemaId)))
            .Select(e=>new{Element=e,Metadata=SourceOpeningMetadata.Read(e)}).Where(e=>e.Metadata!=null).ToList();
        var metadata=tagged.ToDictionary(e=>e.Element.Id.Value,e=>e.Metadata!);
        string SourceKey(SourceOpeningMetadata m,string key)=>m.Project.ToUpperInvariant()+":"+key.ToUpperInvariant();
        var bySource=tagged.GroupBy(e=>SourceKey(e.Metadata!,e.Metadata!.Key)).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single().Element);
        var fillings=tagged.Where(e=>e.Metadata!.Kind is "Door" or "Window" && e.Metadata.Key.Equals(e.Metadata.Guid,StringComparison.OrdinalIgnoreCase))
            .Select(e=>SourceKey(e.Metadata!,e.Metadata!.Guid)).ToHashSet();
        var categoryElements=new FilteredElementCollector(doc).WherePasses(filter).WhereElementIsNotElementType().ToList();
        var nativeOpenings=new FilteredElementCollector(doc).OfClass(typeof(Opening)).Cast<Opening>().Where(o=>o.Host is Wall).Cast<Element>();
        var sourceOpenings=tagged.Where(e=>e.Metadata!.Kind is "Door" or "Window" or "Opening").Select(e=>e.Element);
        var candidates=categoryElements.Concat(nativeOpenings).Concat(sourceOpenings).DistinctBy(e=>e.Id.Value)
            .Where(e=>!metadata.TryGetValue(e.Id.Value,out var m) || !m.Key.StartsWith("opening:",StringComparison.OrdinalIgnoreCase) || !fillings.Contains(SourceKey(m,m.Guid)))
            .OrderBy(e=>e is Opening?1:0).ToList();
        data.OpeningCoverage[source+"/categoryElements"]=categoryElements.Count;data.OpeningCoverage[source+"/candidates"]=candidates.Count;
        int exportedBefore=data.Openings.Count;
        foreach (var item in candidates)
        {
            try
            {
                var family = item as FamilyInstance;
                metadata.TryGetValue(item.Id.Value,out var sourceMetadata);
                Element? host=family?.Host ?? (item as Opening)?.Host;
                if(host==null && sourceMetadata?.HostGuid.Length>0)host=bySource.GetValueOrDefault(SourceKey(sourceMetadata,sourceMetadata.HostGuid));
                var wall=host as Wall;
                var associated = new HashSet<string>();
                foreach (var phase in phases.Where(_ => family is not null))
                {
                    try
                    {
                        foreach (var room in new[] { family!.get_FromRoom(phase), family.get_ToRoom(phase) })
                            if (room is not null && exported.TryGetValue(room.Id.Value, out var dto)) associated.Add(dto.Key);
                    }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException) { /* Imported non-room-aware family. */ }
                }
                string hostKey = host is null ? "" : source + "/" + host.UniqueId;
                var bbox = item.get_BoundingBox(null) ?? sourceMetadata?.VoidBounds();
                if (bbox is null){data.Warnings.Add($"Проём {item.Id.Value}: геометрия отсутствует; элемент не экспортирован.");continue;}
                double minZ=M(transform.OfPoint(bbox.Min).Z),maxZ=M(transform.OfPoint(bbox.Max).Z);
                bool empty=item is Opening || sourceMetadata?.Empty==true || sourceMetadata?.Key.StartsWith("opening:",StringComparison.OrdinalIgnoreCase)==true;
                bool window = sourceMetadata?.Kind=="Window" || item.Category?.Id.Value == (long)BuiltInCategory.OST_Windows;
                bool inferred=empty && sourceMetadata?.Kind is not ("Door" or "Window");
                if(inferred)window=!exported.Values.Any(r=>maxZ>r.BaseElevation && minZ<r.BaseElevation+r.Height && minZ<=r.BaseElevation+.15);
                double width = Length(item, window ? BuiltInParameter.WINDOW_WIDTH : BuiltInParameter.DOOR_WIDTH);
                double height = Length(item, window ? BuiltInParameter.WINDOW_HEIGHT : BuiltInParameter.DOOR_HEIGHT);
                var location = (item.Location as LocationPoint)?.Point ?? (bbox.Min + bbox.Max) * .5;
                var curve = (wall?.Location as LocationCurve)?.Curve;
                var projection = curve?.Project(location);
                var tangent = projection is not null ? curve!.ComputeDerivatives(projection.Parameter, false).BasisX.Normalize() : family?.HandOrientation ?? XYZ.BasisX;
                if (Math.Abs(tangent.Z) > .99) tangent = XYZ.BasisX;
                tangent = new XYZ(tangent.X, tangent.Y, 0).Normalize();
                bool fallback = width <= 0 || height <= 0;
                if (width <= 0)
                {
                    var corners = new List<XYZ>();
                    foreach (double x in new[] { bbox.Min.X, bbox.Max.X })
                    foreach (double y in new[] { bbox.Min.Y, bbox.Max.Y }) corners.Add(new XYZ(x,y,0));
                    width = corners.Max(p => p.DotProduct(tangent)) - corners.Min(p => p.DotProduct(tangent));
                }
                if (height <= 0) height = bbox.Max.Z - bbox.Min.Z;
                if (width <= 0 || height <= 0){data.Warnings.Add($"Проём {item.Id.Value}: ширина/высота не определена.");continue;}
                // Prefer explicit phase associations. Geometric fallback is restricted to the actual host wall.
                if (associated.Count == 0 && wall is not null)
                    foreach (var dto in exported.Values.Where(r => r.Surfaces.Any(s => s.HostKey == hostKey)))
                        if (maxZ > dto.BaseElevation && minZ < dto.BaseElevation + dto.Height)
                        {
                            var local = transform.OfPoint(location);
                            if (DistanceToContour(dto, M(local.X), M(local.Y)) <= M(wall.Width) + .15) associated.Add(dto.Key);
                        }
                var position = transform.OfPoint(new XYZ(location.X,location.Y,bbox.Min.Z + height*.5));
                var surfaceKeys = new Dictionary<string,string>();
                foreach (var dto in exported.Values)
                {
                    if (maxZ <= dto.BaseElevation || minZ >= dto.BaseElevation+dto.Height) continue;
                    if (associated.Count > 0 && !associated.Contains(dto.Key)) continue;
                    // Positions above are already transformed but still in feet. Use meters for all comparisons.
                    var p = new[] { M(position.X), M(position.Y), M(position.Z) };
                    var match = dto.Surfaces.Where(s=>s.Kind=="wall" && s.Point.Length==3 &&
                        p[0]>=s.BoundsMin[0]-.35 && p[0]<=s.BoundsMax[0]+.35 && p[1]>=s.BoundsMin[1]-.35 && p[1]<=s.BoundsMax[1]+.35 &&
                        p[2]>=s.BoundsMin[2]-.1 && p[2]<=s.BoundsMax[2]+.1)
                        .Select(s=>new { Surface=s, Distance=Math.Abs((p[0]-s.Point[0])*s.Normal[0]+(p[1]-s.Point[1])*s.Normal[1]+(p[2]-s.Point[2])*s.Normal[2]) })
                        .Where(s=>s.Distance <= .7).OrderBy(s=>s.Distance).FirstOrDefault();
                    if (match is not null) surfaceKeys[dto.Key] = match.Surface.Key;
                }
                if (associated.Count == 0)
                    foreach (var pair in surfaceKeys.OrderBy(p=>DistanceToContour(exported.Values.First(r=>r.Key==p.Key),M(position.X),M(position.Y))).Take(2)) associated.Add(pair.Key);
                if(inferred && associated.Count>0)window=!exported.Values.Where(r=>associated.Contains(r.Key)).Any(r=>minZ<=r.BaseElevation+.15);
                surfaceKeys = surfaceKeys.Where(p=>associated.Contains(p.Key)).ToDictionary(p=>p.Key,p=>p.Value);
                var direction = transform.OfVector(tangent);
                if(empty && data.Openings.Any(o=>o.HostKey==hostKey && hostKey.Length>0 && Math.Abs(o.Width-M(width))<.03 && Math.Abs(o.Height-M(height))<.03 &&
                    Math.Abs(o.Position[0]-M(position.X))+Math.Abs(o.Position[1]-M(position.Y))<.3 && Math.Abs(o.Position[2]-M(position.Z))<.05))continue;
                data.Openings.Add(new OpeningData
                {
                    Key = source + "/" + item.UniqueId, IdText = item.Id.Value.ToString(), HostKey = hostKey,
                    TypeKey = RevitOvLibrary.TypeKey(item, source + "/" + item.UniqueId),
                    Kind = window ? "window" : "door", Name = family is not null ? family.Symbol.FamilyName + " · " + family.Symbol.Name : item.Name,
                    RoomKeys = associated.ToList(), Position = [M(position.X), M(position.Y), M(position.Z)],
                    Tangent = [direction.X,direction.Y,direction.Z], Width = M(width), Height = M(height),
                    GeometrySource = fallback ? "bounding-box-beta" : "family-parameters-beta",
                    IsEmpty=empty,ClassificationSource=inferred?"sill-height-beta":sourceMetadata?.Kind is "Door" or "Window"?"archicad-source-kind":"revit-category",
                    IsExterior = (wall?.WallType.Function == WallFunction.Exterior || surfaceKeys.Any(p=>exported.Values.First(r=>r.Key==p.Key).Surfaces.Any(s=>s.Key==p.Value && s.Exposure=="outside"))) && associated.Count <= 1,
                    SurfaceKeys = surfaceKeys
                });
            }
            catch (Exception ex) { data.Warnings.Add($"Проём {item.Id.Value}: бета ({ex.Message})."); }
        }
        foreach(var dto in exported.Values)
        foreach(var surface in dto.Surfaces)
        foreach(var cut in surface.Cuts)
        {
            var candidate=data.Openings.Where(o=>o.RoomKeys.Contains(dto.Key) && (o.HostKey==surface.HostKey || o.SurfaceKeys.GetValueOrDefault(dto.Key)==surface.Key))
                .Where(o=>o.Position[2]+o.Height*.5 >= cut.BoundsMin[2]-.1 && o.Position[2]-o.Height*.5 <= cut.BoundsMax[2]+.1)
                .Select(o=>new {Opening=o,Distance=Math.Sqrt(Math.Pow(o.Position[0]-cut.Center[0],2)+Math.Pow(o.Position[1]-cut.Center[1],2)+Math.Pow(o.Position[2]-cut.Center[2],2))})
                .Where(o=>o.Distance < Math.Max(o.Opening.Width,o.Opening.Height)*.75+.15).OrderBy(o=>o.Distance).FirstOrDefault();
            if(candidate is not null){cut.OpeningKey=candidate.Opening.Key;cut.Kind=candidate.Opening.Kind;}
            else
            {
                // A wall void can have no family at all. Retain the actual cut area and classify its sill explicitly.
                if(cut.BoundsMax[2]>=dto.BaseElevation+dto.Height-.01 && cut.BoundsMin[2]>dto.BaseElevation+.15)
                {data.Warnings.Add($"Вырез {cut.Key} до верха помещения: требуется классификация границы.");continue;}
                string kind=cut.BoundsMin[2]<=dto.BaseElevation+.15?"door":"window";
                var normal=surface.Normal;double tx=-normal[1],ty=normal[0],length=Math.Sqrt(tx*tx+ty*ty);if(length<1e-9)continue;tx/=length;ty/=length;
                var corners=new[]{new[]{cut.BoundsMin[0],cut.BoundsMin[1]},new[]{cut.BoundsMin[0],cut.BoundsMax[1]},new[]{cut.BoundsMax[0],cut.BoundsMin[1]},new[]{cut.BoundsMax[0],cut.BoundsMax[1]}};
                double width=corners.Max(p=>p[0]*tx+p[1]*ty)-corners.Min(p=>p[0]*tx+p[1]*ty),height=cut.BoundsMax[2]-cut.BoundsMin[2];
                if(width<=0 || height<=0)continue;
                var existing=data.Openings.FirstOrDefault(o=>o.IsEmpty && o.HostKey==surface.HostKey && Math.Abs(o.Position[2]-cut.Center[2])<.02 &&
                    Math.Abs((o.Position[0]-cut.Center[0])*tx+(o.Position[1]-cut.Center[1])*ty)<.02 && Math.Abs(o.Width-width)<.02 && Math.Abs(o.Height-height)<.02);
                if(existing==null)
                {
                    existing=new OpeningData{Key=source+"/void/"+cut.Key,IdText=surface.ElementId+" · проём",HostKey=surface.HostKey,Kind=kind,Name="Пустой проём",
                        RoomKeys=[dto.Key],Position=cut.Center,Tangent=[tx,ty,0],Width=width,Height=height,MeasuredArea=cut.Area,GeometrySource="revit-wall-void",IsEmpty=true,
                        ClassificationSource="sill-height-beta",IsExterior=surface.Exposure=="outside",SurfaceKeys=new(){[dto.Key]=surface.Key}};
                    data.Openings.Add(existing);
                }
                else {if(!existing.RoomKeys.Contains(dto.Key))existing.RoomKeys.Add(dto.Key);existing.SurfaceKeys[dto.Key]=surface.Key;}
                cut.OpeningKey=existing.Key;cut.Kind=existing.Kind;
            }
        }
        data.OpeningCoverage[source+"/exported"]=data.Openings.Count-exportedBefore;
        data.OpeningCoverage[source+"/unassociated"]=data.Openings.Skip(exportedBefore).Count(o=>o.RoomKeys.Count==0);
        if(data.Openings.Skip(exportedBefore).Any(o=>o.RoomKeys.Count==0))data.Warnings.Add("Часть проёмов сохранена без связи с помещением; они не включены в его тепловой баланс.");
    }

    private static double Length(Element item, BuiltInParameter parameter)
    {
        var p = item.get_Parameter(parameter);
        if (p?.StorageType == StorageType.Double && p.AsDouble() > 0) return p.AsDouble();
        p = item.Document.GetElement(item.GetTypeId())?.get_Parameter(parameter);
        return p?.StorageType == StorageType.Double ? p.AsDouble() : 0;
    }

    private static double DistanceToContour(RoomData dto, double x, double y)
    {
        double min = double.PositiveInfinity;
        foreach (var p in dto.Polygons)
        foreach (var ring in new[] { p.Contour }.Concat(p.Holes))
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            double dx = b[0]-a[0], dy = b[1]-a[1], len = dx*dx+dy*dy;
            double t = len > 0 ? Math.Clamp(((x-a[0])*dx+(y-a[1])*dy)/len,0,1) : 0;
            min = Math.Min(min,Math.Sqrt(Math.Pow(x-a[0]-t*dx,2)+Math.Pow(y-a[1]-t*dy,2)));
        }
        return min;
    }
    private static bool Contains(RoomData room, double x, double y)
    {
        static bool Ring(List<double[]> ring, double x, double y)
        {
            bool inside=false;
            for(int i=0,j=ring.Count-1;i<ring.Count;j=i++)
                if((ring[i][1]>y)!=(ring[j][1]>y) && x<(ring[j][0]-ring[i][0])*(y-ring[i][1])/(ring[j][1]-ring[i][1])+ring[i][0]) inside=!inside;
            return inside;
        }
        return room.Polygons.Any(p=>Ring(p.Contour,x,y) && !p.Holes.Any(h=>Ring(h,x,y)));
    }
    public void Dispose()
    {
        calculator.Dispose();
        foreach(var solid in solids.Values.SelectMany(s=>s)) solid.Dispose();
    }
}
