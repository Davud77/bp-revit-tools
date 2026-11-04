using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using BotPlus.Openings.Core;

namespace BotPlus.Openings;

internal static class HoleCuts
{
    static string? Template(Autodesk.Revit.ApplicationServices.Application app)
    {
        string root=app.FamilyTemplatePath;
        return Directory.Exists(root)?Directory.EnumerateFiles(root,"*.rft",SearchOption.AllDirectories).FirstOrDefault(p=>
            Path.GetFileNameWithoutExtension(p).Equals("Metric Generic Model",StringComparison.OrdinalIgnoreCase)||Path.GetFileNameWithoutExtension(p).Equals("Метрическая система, типовая модель",StringComparison.OrdinalIgnoreCase)):null;
    }
    static FamilySymbol Symbol(Document doc,HoleGeometry g)
    {
        string name=$"BotPlus Отверстие {g.Orientation} {g.Shape} {g.A:0.###}x{g.B:0.###}x{g.Depth:0.###}";
        string signature=HoleJson.Hash(new{g.Orientation,g.Shape,g.A,g.B,g.Depth});
        var existing=new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault(s=>s.FamilyName==name);
        if(existing!=null){if(!HoleStore.OwnFamily(existing.Family,signature))throw new InvalidOperationException("Одноимённое семейство не принадлежит менеджеру отверстий; оно сохранено без изменений.");return existing;}
        string template=Template(doc.Application)??throw new InvalidOperationException("Не найден стандартный шаблон типовой модели Revit. Укажите/установите шаблоны семейств Autodesk.");
        var familyDoc=doc.Application.NewFamilyDocument(template);
        try
        {
            using(var transaction=new Transaction(familyDoc,"BotPlus — семейство выреза"))
            {
                transaction.Start();familyDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS).Set(1);
                var profile=HoleMarkers.Profile(g,XYZ.Zero,XYZ.BasisX,g.Orientation==HoleOrientation.Wall?XYZ.BasisZ:XYZ.BasisY);var array=new CurveArray();foreach(var curve in profile)array.Append(curve);
                var loops=new CurveArrArray();loops.Append(array);
                var plane=SketchPlane.Create(familyDoc,Plane.CreateByNormalAndOrigin(g.Orientation==HoleOrientation.Wall?XYZ.BasisY:XYZ.BasisZ,XYZ.Zero));
                familyDoc.FamilyCreate.NewExtrusion(false,loops,plane,HoleMarkers.Feet(g.Depth));
                familyDoc.FamilyManager.NewType("Вырез");transaction.Commit();
            }
            string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BotPlus.RT","Openings","Families");Directory.CreateDirectory(folder);
            string file=Path.Combine(folder,name+".rfa");familyDoc.SaveAs(file,new SaveAsOptions{OverwriteExistingFile=true,MaximumBackups=1});
            var family=familyDoc.LoadFamily(doc,new LoadOptions());HoleStore.InTransaction(doc,"Отверстия — служебное семейство",()=>HoleStore.TagFamily(family,signature));return (FamilySymbol)doc.GetElement(family.GetFamilySymbolIds().First());
        }
        finally{familyDoc.Close(false);}
    }
    sealed class LoadOptions:IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse,out bool overwriteParameterValues){overwriteParameterValues=false;return false;}
        public bool OnSharedFamilyFound(Family sharedFamily,bool familyInUse,out FamilySource source,out bool overwriteParameterValues){source=FamilySource.Project;overwriteParameterValues=false;return false;}
    }
    internal static List<string> Run(Document doc,Registry registry,IEnumerable<Hole> selected,bool removeOnly=false)
    {
        HoleMonitor.Reconcile(doc,registry);
        var messages=new List<string>();
        foreach(var hole in selected)
        {
            if(!removeOnly&&!hole.FullyApproved){messages.Add(hole.Id+": нет трёх согласований.");continue;}
            if(!removeOnly&&hole.CutIds.Count>0&&hole.CutGeometryHash==HoleJson.Hash(hole.Geometry)&&hole.CutIds.All(id=>doc.GetElement(id)!=null)){messages.Add(hole.Id+": вырез без изменений.");continue;}
            var backup=HoleJson.Clone(hole);
            try
            {
                var symbol=removeOnly?null:Symbol(doc,hole.Geometry);
                HoleStore.InTransaction(doc,"BotPlus — "+(removeOnly?"снять вырез":"вырезать отверстие"),()=>
                {
                    foreach(string cut in hole.CutIds){var old=doc.GetElement(cut);if(old!=null)HoleMarkers.DeleteOwned(doc,old.Id);}hole.CutIds.Clear();hole.CutGeometryHash="";
                    doc.Regenerate();
                    if(!removeOnly)
                    {
                        var body=HoleMarkers.Solid(hole.Geometry);var localBox=body.GetBoundingBox();
                        var corners=new List<XYZ>();foreach(double x in new[]{localBox.Min.X,localBox.Max.X})foreach(double y in new[]{localBox.Min.Y,localBox.Max.Y})foreach(double z in new[]{localBox.Min.Z,localBox.Max.Z})corners.Add(localBox.Transform.OfPoint(new XYZ(x,y,z)));
                        var box=new BoundingBoxXYZ{Min=new XYZ(corners.Min(p=>p.X),corners.Min(p=>p.Y),corners.Min(p=>p.Z)),Max=new XYZ(corners.Max(p=>p.X),corners.Max(p=>p.Y),corners.Max(p=>p.Z))};
                        var candidates=new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(e=>hole.Geometry.Orientation==HoleOrientation.Wall?e is Wall:e is Floor or Ceiling)
                            .Where(e=>e.get_BoundingBox(null) is {} b&&b.Min.X<=box.Max.X&&b.Max.X>=box.Min.X&&b.Min.Y<=box.Max.Y&&b.Max.Y>=box.Min.Y&&b.Min.Z<=box.Max.Z&&b.Max.Z>=box.Min.Z)
                            .Where(e=>hole.Imported||hole.HostLinkId.Length>0||hole.HostId.Length==0||e.UniqueId==hole.HostId).ToList();
                        var hosts=candidates.Where(e=>HoleMarkers.Solids(e.get_Geometry(new Options())).Any(s=>BooleanOperationsUtils.ExecuteBooleanOperation(s,body,BooleanOperationsType.Intersect).Volume>1e-9)).ToList();
                        if(hosts.Count==0)throw new InvalidOperationException("Задание не пересекает доступную основу в текущей модели.");
                        var before=hosts.ToDictionary(e=>e.Id,e=>HoleMarkers.Solids(e.get_Geometry(new Options())).ToList());
                        var volumes=hosts.ToDictionary(e=>e.Id,e=>before[e.Id].Sum(s=>s.Volume));
                        var expected=hosts.ToDictionary(e=>e.Id,e=>before[e.Id].Select(s=>BooleanOperationsUtils.ExecuteBooleanOperation(s,body,BooleanOperationsType.Difference)).Where(s=>s.Volume>1e-10).ToList());
                        var probe=Line.CreateBound(HoleMarkers.Center(hole.Geometry)-HoleMarkers.Axis(hole.Geometry)*HoleMarkers.Feet(100000),HoleMarkers.Center(hole.Geometry)+HoleMarkers.Axis(hole.Geometry)*HoleMarkers.Feet(100000));
                        foreach(var host in hosts)foreach(var solid in before[host.Id])
                        {using var intersection=solid.IntersectWithCurve(probe,new SolidCurveIntersectionOptions());for(int i=0;i<intersection.SegmentCount;i++){var curve=intersection.GetCurveSegment(i);for(int j=0;j<2;j++)if(Math.Abs((curve.GetEndPoint(j)-HoleMarkers.Center(hole.Geometry)).DotProduct(HoleMarkers.Axis(hole.Geometry)))>HoleMarkers.Feet(hole.Geometry.Depth/2)+1e-6)throw new InvalidOperationException("Глубина задания недостаточна для сквозного выреза основы.");}}
                        symbol!.Activate();doc.Regenerate();var origin=HoleMarkers.Center(hole.Geometry)-HoleMarkers.Axis(hole.Geometry)*HoleMarkers.Feet(hole.Geometry.Depth/2);
                        var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().MinBy(l=>Math.Abs(l.Elevation-origin.Z))??throw new InvalidOperationException("В модели нет уровней.");
                        var instance=doc.Create.NewFamilyInstance(origin,symbol,level,StructuralType.NonStructural);
                        if(Math.Abs(hole.Geometry.Yaw)>1e-9)ElementTransformUtils.RotateElement(doc,instance.Id,Line.CreateBound(origin,origin+XYZ.BasisZ),hole.Geometry.Yaw);
                        foreach(var host in hosts)
                        {if(!InstanceVoidCutUtils.CanBeCutWithVoid(host))throw new InvalidOperationException("Основа не допускает вырез пустотным семейством.");InstanceVoidCutUtils.AddInstanceVoidCut(doc,host,instance);}
                        doc.Regenerate();
                        foreach(var host in hosts)
                        {var actual=HoleMarkers.Solids(host.get_Geometry(new Options())).ToList();if(volumes[host.Id]-actual.Sum(s=>s.Volume)<1e-9||Difference(actual,expected[host.Id])>1e-6||Difference(expected[host.Id],actual)>1e-6)throw new InvalidOperationException("Геометрия выреза не совпала с заданием. Операция отменена.");}
                        hole.CutIds.Add(instance.UniqueId);hole.CutGeometryHash=HoleJson.Hash(hole.Geometry);
                    }
                    HoleStore.Save(doc,registry);
                });messages.Add(hole.Id+(removeOnly?": вырез снят.":": вырез выполнен, объём основы проверен."));
            }
            catch(Exception error){hole.CutIds=backup.CutIds;hole.CutGeometryHash=backup.CutGeometryHash;messages.Add(hole.Id+": "+error.Message);}
        }
        return messages;
    }
    static double Difference(IEnumerable<Solid> source,IEnumerable<Solid> subtract)
    {double volume=0;foreach(var s in source){var remainder=SolidUtils.CreateTransformed(s,Transform.Identity);foreach(var other in subtract){if(remainder.Volume<1e-10)break;remainder=BooleanOperationsUtils.ExecuteBooleanOperation(remainder,other,BooleanOperationsType.Difference);}volume+=remainder.Volume;}return volume;}
}
