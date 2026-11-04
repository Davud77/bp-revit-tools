using Autodesk.Revit.DB;
using System.Text.Json;

namespace Ac2Revit;

internal static class HatchValidation
{
    public static object Run(Autodesk.Revit.ApplicationServices.Application app,string source,string template,string folder,int sampleLimit=0)
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
            if(sampleLimit>0)Samples(doc,manifest,folder,sampleLimit);
            doc.SaveAs(path,new SaveAsOptions());doc.Close(false);doc=app.OpenDocumentFile(path);
            HatchMappingStage.Audit(doc,manifest,source,plan,Path.Combine(folder,"reopen-audit.json"));
            return new{status="complete",materials=manifest.Materials.Count,template,output=path,repeatUnchanged=repeat.Items.Count};
        }
        finally{doc.Close(false);}
    }
    static void Samples(Document doc,Manifest manifest,string folder,int limit)
    {
        ViewPlan view;
        using(var transaction=new Transaction(doc,"Native material section hatch samples"))
        {
            transaction.Start();var level=Level.Create(doc,0);level.Name="АС — Образцы штриховок";
            var family=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.FloorPlan);
            view=ViewPlan.Create(doc,family.Id,level.Id);view.Name="АС — Проверка срезов материалов";view.Scale=20;view.DetailLevel=ViewDetailLevel.Fine;view.DisplayStyle=DisplayStyle.HLR;
            var baseType=new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t=>t.Kind==WallKind.Basic);
            var textType=new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).First().Id;
            var index=Storage.Index(doc,manifest.Source.ProjectId);int n=0;
            foreach(var raw in manifest.Materials.Take(limit))
            {
                var material=(Material)index["material:"+raw.GetProperty("attributeId").S("guid").ToUpperInvariant()];
                var type=(WallType)baseType.Duplicate("Образец среза "+(++n));
                using var structure=CompoundStructure.CreateSimpleCompoundStructure([new CompoundStructureLayer(Geometry.Feet(.6),MaterialFunctionAssignment.Structure,material.Id)]);
                type.SetCompoundStructure(structure);
                double x=Geometry.Feet(((n-1)%8)*4),y=Geometry.Feet(-((n-1)/8)*3);
                var wall=Wall.Create(doc,Line.CreateBound(new XYZ(x,y,0),new XYZ(x+Geometry.Feet(2.8),y,0)),type.Id,level.Id,Geometry.Feet(2),0,false,false);
                WallUtils.DisallowWallJoinAtEnd(wall,0);WallUtils.DisallowWallJoinAtEnd(wall,1);
                TextNote.Create(doc,view.Id,new XYZ(x,y-Geometry.Feet(.6),0),Geometry.Feet(3.6)/view.Scale,n+". "+raw.S("name"),new TextNoteOptions(textType));
            }
            var crop=view.CropBox;crop.Min=new XYZ(Geometry.Feet(-1),Geometry.Feet(-Math.Ceiling(n/8d)*3),crop.Min.Z);
            crop.Max=new XYZ(Geometry.Feet(32),Geometry.Feet(1.5),crop.Max.Z);view.CropBox=crop;view.CropBoxActive=true;view.CropBoxVisible=false;
            view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.Set(1);
            transaction.Commit();
        }
        var options=new ImageExportOptions{ExportRange=ExportRange.SetOfViews,FilePath=Path.Combine(folder,"native-material-cuts"),HLRandWFViewsFileType=ImageFileType.PNG,
            ShadowViewsFileType=ImageFileType.PNG,PixelSize=5000,ZoomType=ZoomFitType.FitToPage,ImageResolution=ImageResolution.DPI_150};
        options.SetViewsAndSheets([view.Id]);doc.ExportImage(options);
    }
}
