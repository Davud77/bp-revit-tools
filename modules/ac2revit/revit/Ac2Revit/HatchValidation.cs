using Autodesk.Revit.DB;
using System.Text.Json;

namespace Ac2Revit;

internal static class HatchValidation
{
    public static object Run(Autodesk.Revit.ApplicationServices.Application app,string source,string template,string folder)
    {
        Directory.CreateDirectory(folder);var manifest=Manifest.Load(source);var doc=app.NewProjectDocument(template);
        string path=Path.Combine(folder,"hatches.rvt"),plan=Path.Combine(folder,"hatch-mapping.json");
        try
        {
            using(var t=new Transaction(doc,"Hatch fixture source materials"))
            {
                t.Start();
                foreach(var raw in manifest.Materials)
                {
                    string guid=raw.GetProperty("attributeId").S("guid").ToUpperInvariant();
                    var mat=(Material)doc.GetElement(Material.Create(doc,"AC fixture "+guid));
                    Storage.Tag(mat,manifest.Source.ProjectId,"material:"+guid,Json.Hash(raw),raw.GetRawText());
                }
                t.Commit();
            }
            string catalog=Path.Combine(folder,"catalog.json");HatchMappingStage.Inventory(doc,manifest,catalog);
            ExchangeServices.Run("prepare_hatch_mapping.py",["--manifest",source,"--target",catalog,"--rules",Path.Combine(ExchangeServices.Root,"config","hatch-analogues.ru.json"),"--out",folder]);
            var initial=HatchMappingStage.Run(doc,manifest,source,plan,Path.Combine(folder,"initial-audit.json"));
            if(initial.Items.Count!=manifest.Materials.Count)throw new InvalidDataException("Material coverage incomplete");
            var repeat=HatchMappingStage.Run(doc,manifest,source,plan,Path.Combine(folder,"repeat-audit.json"));
            if(repeat.Items.Any(i=>i.Status!="unchanged"))throw new InvalidDataException("Hatch resume is not idempotent");
            doc.SaveAs(path,new SaveAsOptions());doc.Close(false);doc=app.OpenDocumentFile(path);
            HatchMappingStage.Audit(doc,manifest,source,plan,Path.Combine(folder,"reopen-audit.json"));
            return new{status="complete",materials=manifest.Materials.Count,template,output=path,repeatUnchanged=repeat.Items.Count};
        }
        finally{doc.Close(false);}
    }
}
