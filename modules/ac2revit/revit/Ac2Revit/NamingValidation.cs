using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>Explicit fixture-only diagnostic: creates a new metric project and checks visible labels.</summary>
public static class NamingValidation
{
    public static object Run(UIApplication ui,string fixture,string output)
    {
        if(File.Exists(output))throw new IOException("Naming validation output exists");
        var manifest=Manifest.Load(Path.Combine(fixture,"manifest.json"));
        var doc=ui.Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var config=new ImportConfig{ConnectWalls=false};
            var first=new Importer(doc,manifest,config).Run(new HashSet<string>{"Wall"});
            if(first.Items.Any(i=>i.Status is "failed" or "conflict" or "revitError"))throw new InvalidDataException("Fixture import failed");
            var index=Storage.Index(doc,manifest.Source.ProjectId);
            foreach(var level in manifest.Levels)
                if(index.GetValueOrDefault(level.Key) is not Level actual || GridLabels.Visible(actual.Name)!=SourceLabel.Format(level.Name))
                    throw new InvalidDataException("Source level label differs");
            foreach(var type in manifest.Types)
                if(index.GetValueOrDefault(type.Key) is not ElementType actual || GridLabels.Visible(actual.Name)!=SourceLabel.Format(type.Name))
                    throw new InvalidDataException("Source type label differs");
            foreach(var raw in manifest.Materials)
            {
                string key="material:"+raw.GetProperty("attributeId").S("guid").ToUpperInvariant();
                if(index.GetValueOrDefault(key) is not Material actual || GridLabels.Visible(actual.Name)!=SourceLabel.Format(raw.S("name")))throw new InvalidDataException("Source material label differs");
            }
            var names=index.Select(p=>new{key=p.Key,name=p.Value.Name}).ToList();
            using(var t=new Transaction(doc,"Fixture generated family label"))
            {
                t.Start();
                if(!doc.LoadFamily(Path.Combine(fixture,"column.rfa"),out var family) || family==null)throw new InvalidDataException("Fixture family missing");
                SourceNames.RenameFamily(doc,family,"Колонна 400 × 400 мм");
                if(family.Name!=SourceLabel.Format("Колонна 400 × 400 мм"))throw new InvalidDataException("Family label differs");
                names.Add(new{key="fixture-family",name=family.Name});t.Commit();
            }
            foreach(var (kind,category) in new[]{("Door",BuiltInCategory.OST_Doors),("Window",BuiltInCategory.OST_Windows),("Column",BuiltInCategory.OST_StructuralColumns),("Roof",BuiltInCategory.OST_GenericModel)})
            {
                string path=Path.Combine(fixture,kind+".json");
                var report=ReferenceFallback.Run(doc,path,Path.GetFullPath,category:category,recordKind:kind);
                if(report.Items.Any(i=>i.Status=="blocked"))throw new InvalidDataException("Fixture fallback failed: "+kind);
                using var json=JsonDocument.Parse(File.ReadAllText(path));var row=json.RootElement.GetProperty("elements")[0];
                var shape=Storage.Index(doc,manifest.Source.ProjectId)[row.S("sourceGuid")];
                if(shape.Name!=SourceLabel.Format(row.S("mark"))+" — геометрия IFC")throw new InvalidDataException("Fallback label differs");
                names.Add(new{key=row.S("sourceGuid"),name=shape.Name});
            }
            var owned=Storage.Index(doc,manifest.Source.ProjectId);int before=owned.Count;
            string edited="Пользовательское имя этажа";
            using(var t=new Transaction(doc,"Fixture user rename")){t.Start();owned[manifest.Levels[0].Key].Name=edited;t.Commit();}
            var repeat=new Importer(doc,manifest,config).Run(new HashSet<string>{"Wall"});
            var after=Storage.Index(doc,manifest.Source.ProjectId);
            if(after.Count!=before || after[manifest.Levels[0].Key].Name!=edited || repeat.Items.Any(i=>i.Status is "created" or "failed" or "conflict"))
                throw new InvalidDataException("Repeat import duplicated objects or changed a user label");
            doc.SaveAs(output,new SaveAsOptions{MaximumBackups=1});
            return new{status="verified",revitVersion=ui.Application.VersionNumber,names,sourceIdentities=before,
                repeatWithoutDuplicates=true,userRenamePreserved=true,duplicateSourceLevelNamesVerified=true,output};
        }
        finally{doc.Close(false);}
    }
}
