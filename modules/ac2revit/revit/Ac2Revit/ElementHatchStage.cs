using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>Explicit source element fills, without replacing compound material layers.</summary>
public static class ElementHatchStage
{
    public sealed class Plan
    {
        public string Format {get;set;}="";
        public string ProjectId {get;set;}="";
        public string SourceManifestSha256 {get;set;}="";
        public List<Assignment> Assignments {get;set;}=[];
        public List<JsonElement> Issues {get;set;}=[];
    }
    public sealed class Assignment
    {
        public string SourceGuid {get;set;}="";
        public string Kind {get;set;}="";
        public string LevelKey {get;set;}="";
        public string Slot {get;set;}="";
        public HatchMappingStage.Row Graphics {get;set;}=new();
    }
    static int[] Rgb(Color c)=>c.IsValid?[c.Red,c.Green,c.Blue]:[];
    static object State(OverrideGraphicSettings g,string slot)=>slot=="cover"
        ?new {fg=g.SurfaceForegroundPatternId.Value,bg=g.SurfaceBackgroundPatternId.Value,fgRgb=Rgb(g.SurfaceForegroundPatternColor),bgRgb=Rgb(g.SurfaceBackgroundPatternColor),fgVisible=g.IsSurfaceForegroundPatternVisible,bgVisible=g.IsSurfaceBackgroundPatternVisible}
        :new {fg=g.CutForegroundPatternId.Value,bg=g.CutBackgroundPatternId.Value,fgRgb=Rgb(g.CutForegroundPatternColor),bgRgb=Rgb(g.CutBackgroundPatternColor),fgVisible=g.IsCutForegroundPatternVisible,bgVisible=g.IsCutBackgroundPatternVisible};
    static bool HasOverride(OverrideGraphicSettings g,string slot)=>slot=="cover"
        ?g.SurfaceForegroundPatternId!=ElementId.InvalidElementId||g.SurfaceBackgroundPatternId!=ElementId.InvalidElementId||g.SurfaceForegroundPatternColor.IsValid||g.SurfaceBackgroundPatternColor.IsValid||!g.IsSurfaceForegroundPatternVisible||!g.IsSurfaceBackgroundPatternVisible
        :g.CutForegroundPatternId!=ElementId.InvalidElementId||g.CutBackgroundPatternId!=ElementId.InvalidElementId||g.CutForegroundPatternColor.IsValid||g.CutBackgroundPatternColor.IsValid||!g.IsCutForegroundPatternVisible||!g.IsCutBackgroundPatternVisible;
    static Color Colour(int[] rgb)=>new((byte)rgb[0],(byte)rgb[1],(byte)rgb[2]);
    static ElementId PatternId(Document doc,HatchMappingStage.Row row)
    {
        if(row.Match=="empty")return ElementId.InvalidElementId;
        var matches=new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
            .Where(p=>p.Name==row.TargetName).Where(p=>{using var pattern=p.GetFillPattern();return pattern.Target==FillPatternTarget.Drafting && pattern.IsSolidFill==(row.Match=="solid")
                && (row.TargetGeometry!=null||Json.Hash(HatchMappingStage.Pattern(pattern))==row.TargetFingerprint);}).ToArray();
        if(matches.Length!=1)throw new InvalidDataException("Missing or changed element fill: "+row.TargetName);
        return matches[0].Id;
    }
    static void Set(OverrideGraphicSettings g,Assignment assignment,ElementId foreground,ElementId background)
    {
        var row=assignment.Graphics;bool visible=row.Match!="empty";
        if(assignment.Slot=="cover")
        {
            g.SetSurfaceForegroundPatternId(foreground);g.SetSurfaceForegroundPatternVisible(visible);g.SetSurfaceForegroundPatternColor(Colour(row.ForegroundRgb));
            g.SetSurfaceBackgroundPatternId(background);g.SetSurfaceBackgroundPatternVisible(background!=ElementId.InvalidElementId);
            if(background!=ElementId.InvalidElementId)g.SetSurfaceBackgroundPatternColor(Colour(row.BackgroundRgb));
        }
        else
        {
            g.SetCutForegroundPatternId(foreground);g.SetCutForegroundPatternVisible(visible);g.SetCutForegroundPatternColor(Colour(row.ForegroundRgb));
            g.SetCutBackgroundPatternId(background);g.SetCutBackgroundPatternVisible(background!=ElementId.InvalidElementId);
            if(background!=ElementId.InvalidElementId)g.SetCutBackgroundPatternColor(Colour(row.BackgroundRgb));
        }
    }
    static bool SolidGeometry(GeometryElement? geometry)=>geometry!=null&&geometry.Any(g=>g is Solid s&&s.Volume>1e-9 || g is GeometryInstance instance&&SolidGeometry(instance.GetInstanceGeometry()));
    public static Report PrepareAndRun(Document doc,Manifest manifest,string source,string rules,string folder,ImportSelection? selection=null)
    {
        Directory.CreateDirectory(folder);string catalog=Path.Combine(folder,"revit-catalog.json"),plan=Path.Combine(folder,"element-hatches.json");
        using(var sourceJson=JsonDocument.Parse(File.ReadAllText(source)))
        {
            if(manifest.Fills.Count==0||!sourceJson.RootElement.TryGetProperty("penTables",out var tables)||tables.ValueKind!=JsonValueKind.Array
                ||tables.EnumerateArray().Count(t=>t.B("isActiveForModel"))!=1)
            {
                var missing=new Report{ProjectId=manifest.Source.ProjectId,Mode="elementHatches"};
                missing.Add("element-hatches-source","ElementHatch","partial","В исходном снимке нет полного каталога штриховок и активных перьев. Нужна новая выгрузка Archicad; графика элементов сохранена.");
                missing.Save(Path.Combine(folder,"source-limitation.json"));return missing;
            }
        }
        HatchMappingStage.Inventory(doc,manifest,catalog);
        ExchangeServices.Run("prepare_element_hatches.py",["--manifest",source,"--target",catalog,"--rules",rules,"--out",plan],
            line=>File.AppendAllText(Path.Combine(folder,"prepare.log"),line+Environment.NewLine));
        return Run(doc,manifest,source,plan,Path.Combine(folder,"audit.json"),selection);
    }
    public static Report Run(Document doc,Manifest manifest,string source,string input,string output,ImportSelection? selection=null)
    {
        var plan=JsonSerializer.Deserialize<Plan>(File.ReadAllText(input),Json.Options)??throw new InvalidDataException("Empty element hatch plan");
        using var stream=File.OpenRead(source);
        if(plan.Format!="ac2r.element-hatches/1"||plan.ProjectId!=manifest.Source.ProjectId
            ||!Convert.ToHexString(SHA256.HashData(stream)).Equals(plan.SourceManifestSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Different source element hatch snapshot");
        var records=manifest.Elements.ToDictionary(e=>e.SourceGuid,StringComparer.OrdinalIgnoreCase);var unique=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var a in plan.Assignments)
        {
            var row=a.Graphics;
            if(!records.TryGetValue(a.SourceGuid,out var e)||e.Kind!=a.Kind||e.LevelKey!=a.LevelKey||!unique.Add(a.SourceGuid+":"+a.Slot)
                ||a.Slot is not ("cover" or "cut")||row.Match is not ("empty" or "solid" or "analogue" or "sourceVector" or "portableAnalogue")
                ||row.Background is not ("transparent" or "solid")||row.ForegroundRgb.Length!=3||row.BackgroundRgb.Length!=3
                ||row.ForegroundRgb.Concat(row.BackgroundRgb).Any(n=>n<0||n>255))throw new InvalidDataException("Invalid source element hatch assignment");
            var details=e.Raw.GetProperty("details");JsonElement fillId;
            if(a.Slot=="cover"&&e.Kind=="Slab")
            {var fill=details.GetProperty("floorFill");if(!fill.B("use")||fill.B("use3DHatching"))throw new InvalidDataException("Source cover fill is disabled or surface-derived");fillId=fill.GetProperty("fillId");}
            else if(a.Slot=="cut"&&e.Kind is "Object" or "Lamp"&&!details.B("useObjectSectionAttributes",true))fillId=details.GetProperty("sectionFillId");
            else throw new InvalidDataException("Unsupported source element fill scope");
            if(!fillId.S("guid").Equals(row.SourceFillGuid,StringComparison.OrdinalIgnoreCase)
                ||!manifest.Fills.Any(f=>f.D("index")==row.SourceFillIndex&&f.GetProperty("attributeId").S("guid").Equals(row.SourceFillGuid,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Element fill differs from source");
        }
        var index=Storage.Index(doc,plan.ProjectId);var report=new Report{ProjectId=plan.ProjectId,Mode="elementHatches"};var audit=new List<object>();
        var views=new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v=>!v.IsTemplate&&v.AreGraphicsOverridesAllowed()).ToList();
        using var group=new TransactionGroup(doc,"Archicad per-element fill graphics");group.Start();
        try
        {
            using var transaction=new Transaction(doc,"Native element cover and cut fill overrides");transaction.Start();
            HatchMappingStage.EnsurePatterns(doc,new HatchMappingStage.Plan{Materials=plan.Assignments.Select(a=>a.Graphics).ToList()});
            foreach(var a in plan.Assignments)
            {
                if(selection!=null&&!selection.Includes(a.Kind))continue;
                if(!index.TryGetValue(a.SourceGuid,out var element)){report.Add(a.SourceGuid,"ElementHatch","missing","Исходный элемент не представлен в Revit.");continue;}
                var targets=a.Slot=="cover"?views.OfType<ViewPlan>().Where(v=>v.ViewType is ViewType.FloorPlan or ViewType.EngineeringPlan
                    &&index.GetValueOrDefault(a.LevelKey) is Level level&&v.GenLevel?.Id==level.Id).Cast<View>().ToList():views;
                if(targets.Count==0){report.Add(a.SourceGuid,"ElementHatch","missing","Нет подходящего вида для исходной штриховки.");continue;}
                var fg=PatternId(doc,a.Graphics);var bg=HatchMappingStage.BackgroundId(doc,a.Graphics);
                bool cuttable=a.Slot!="cut"||a.Graphics.Match=="empty"||SolidGeometry(element.get_Geometry(new Options{DetailLevel=ViewDetailLevel.Fine}));
                foreach(var view in targets)
                {
                    string key="element-hatch:"+a.SourceGuid+":"+view.UniqueId+":"+a.Slot;var state=index.GetValueOrDefault(key) as DataStorage;
                    using var current=view.GetElementOverrides(element.Id);string before=Json.Hash(State(current,a.Slot));
                    if(state!=null)
                    {
                        var schema=Storage.Schema();using var meta=JsonDocument.Parse(state.GetEntity(schema).Get<string>(schema.GetField("RawJson")));
                        if(meta.RootElement.S("stateHash")!=before||Storage.Read(state)?.Hash!=Json.Hash(a))
                        {report.Add(a.SourceGuid,"ElementHatch","conflict","Сохранены изменения графики или изменилось исходное назначение: "+view.Name,element.UniqueId);continue;}
                    }
                    else if(HasOverride(current,a.Slot)){report.Add(a.SourceGuid,"ElementHatch","conflict","На виде уже есть пользовательское переопределение: "+view.Name,element.UniqueId);continue;}
                    Set(current,a,fg,bg);view.SetElementOverrides(element.Id,current);
                    using var readback=view.GetElementOverrides(element.Id);string after=Json.Hash(State(readback,a.Slot));
                    if(after!=Json.Hash(State(current,a.Slot)))throw new InvalidDataException("Native element fill readback differs");
                    if(state==null){state=DataStorage.Create(doc);index.Add(key,state);}
                    Storage.Tag(state,plan.ProjectId,key,Json.Hash(a),JsonSerializer.Serialize(new{stateHash=after},Json.Options));
                    audit.Add(new{sourceGuid=a.SourceGuid,kind=a.Kind,slot=a.Slot,elementId=element.Id.Value,viewId=view.Id.Value,viewName=view.Name,cuttable,state=State(readback,a.Slot)});
                    report.Add(a.SourceGuid,"ElementHatch",!cuttable?"partial":before==after?"unchanged":"updated",
                        a.Slot+": "+view.Name+(cuttable?"":". У элемента нет режущегося Solid; видимость штриховки среза не подтверждена."),element.UniqueId);
                }
            }
            if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("Element fill transaction rolled back");group.Assimilate();
        }
        catch{group.RollBack();throw;}
        foreach(var issue in plan.Issues)if(selection==null||selection.Includes(issue.S("kind")))report.Add(issue.S("sourceGuid"),"ElementHatch",issue.S("status"),issue.S("reason"));
        File.WriteAllText(output,JsonSerializer.Serialize(new{format="ac2r.element-hatch-audit/1",assignments=audit,issues=plan.Issues,outcomes=report.Items.GroupBy(i=>i.Status).ToDictionary(g=>g.Key,g=>g.Count())},Json.Options));
        return report;
    }
}
