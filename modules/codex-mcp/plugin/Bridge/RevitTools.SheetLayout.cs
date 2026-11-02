using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;
namespace WEAM.Revit.AI;
internal static partial class RevitTools
{
    private static object PreviewFinishSheet(UIApplication app,JsonElement args)=>PreviewOperation(app,args,"finishSheet","Оформление листа",FinishSheet);
    private static object ApplyFinishSheet(UIApplication app,JsonElement args,Func<bool>? cancelled)=>ApplyOperation(app,args,"finishSheet","Применить оформление листа?",FinishSheet,cancelled);
    private static object FinishSheet(Document doc,JsonElement args)
    {
        var sheet=doc.GetElement(new ElementId(HostedReadId(args,"sheetId"))) as ViewSheet??throw new ArgumentException("sheetId must identify a sheet.");
        XYZ Position(double x,double y)=>new(sheet.Outline.Min.U+SheetUnits(x),sheet.Outline.Min.V+SheetUnits(y),0);
        var moved=new List<long>();var notes=new List<long>();
        if(args.TryGetProperty("viewports",out var placements))
        {
            if(placements.GetArrayLength()>10)throw new ArgumentException("Maximum 10 viewports.");
            foreach(var p in placements.EnumerateArray())
            {
                var vp=doc.GetElement(new ElementId(HostedReadId(p,"viewportId"))) as Viewport;
                if(vp is null||vp.SheetId!=sheet.Id)throw new ArgumentException("Viewport belongs to another sheet.");
                var view=(View)doc.GetElement(vp.ViewId);
                if(p.TryGetProperty("viewportTypeId",out _))
                {
                    var typeId=new ElementId(HostedReadId(p,"viewportTypeId"));
                    if(!vp.GetValidTypes().Contains(typeId))throw new ArgumentException("viewportTypeId is not a valid viewport type.");
                    if(vp.GetTypeId()!=typeId){vp.ChangeTypeId(typeId);doc.Regenerate();}
                }
                if(p.TryGetProperty("scale",out _))
                {
                    var scale=(int)BuildingNumber(p,"scale",1,1000);
                    if(view.Scale!=scale){view.Scale=scale;doc.Regenerate();}
                }
                if(p.TryGetProperty("xMm",out _) || p.TryGetProperty("yMm",out _))
                {
                var desiredCenter=Position(BuildingNumber(p,"xMm",20,SheetMm(sheet.Outline.Max.U-sheet.Outline.Min.U)-5),BuildingNumber(p,"yMm",5,SheetMm(sheet.Outline.Max.V-sheet.Outline.Min.V)-5));
                // Keep the native depth of 3D viewports; moving on a sheet changes only X/Y.
                var oldCenter=vp.GetBoxCenter();vp.SetBoxCenter(new XYZ(desiredCenter.X,desiredCenter.Y,oldCenter.Z));doc.Regenerate();
                var box=vp.GetBoxOutline();
                if(box.MinimumPoint.X<sheet.Outline.Min.U+SheetUnits(20)-1e-6||box.MaximumPoint.X>sheet.Outline.Max.U-SheetUnits(5)+1e-6||box.MinimumPoint.Y<sheet.Outline.Min.V+SheetUnits(5)-1e-6||box.MaximumPoint.Y>sheet.Outline.Max.V-SheetUnits(5)+1e-6)throw new InvalidOperationException($"Viewport {vp.Id.Value} exceeds sheet frame: ({SheetMm(box.MinimumPoint.X-sheet.Outline.Min.U):F2},{SheetMm(box.MinimumPoint.Y-sheet.Outline.Min.V):F2})..({SheetMm(box.MaximumPoint.X-sheet.Outline.Min.U):F2},{SheetMm(box.MaximumPoint.Y-sheet.Outline.Min.V):F2}) mm.");
                }
                if(p.TryGetProperty("labelPosition",out var label))
                {
                    if(vp.Rotation!=ViewportRotation.None)throw new ArgumentException("Explicit label position requires an unrotated viewport.");
                    var desired=Position(BuildingNumber(label,"xMm",20,SheetMm(sheet.Outline.Max.U-sheet.Outline.Min.U)-5),BuildingNumber(label,"yMm",5,SheetMm(sheet.Outline.Max.V-sheet.Outline.Min.V)-5));
                    // Position the real label outline, accounting for the caption family's own offsets.
                    var bounds=vp.GetLabelOutline();
                    var alignment=JsonTools.GetString(label,"alignment","left");
                    if(alignment is not ("left" or "center"))throw new ArgumentException("Invalid caption alignment.");
                    var reference=alignment=="center" ? new XYZ((bounds.MinimumPoint.X+bounds.MaximumPoint.X)/2,bounds.MinimumPoint.Y,bounds.MinimumPoint.Z):bounds.MinimumPoint;
                    vp.LabelOffset+=desired-reference;doc.Regenerate();
                    bounds=vp.GetLabelOutline();
                    if(bounds.MinimumPoint.X<sheet.Outline.Min.U+SheetUnits(20)-1e-6||bounds.MaximumPoint.X>sheet.Outline.Max.U-SheetUnits(5)+1e-6||bounds.MinimumPoint.Y<sheet.Outline.Min.V+SheetUnits(5)-1e-6||bounds.MaximumPoint.Y>sheet.Outline.Max.V-SheetUnits(5)+1e-6)throw new InvalidOperationException("Viewport caption exceeds sheet frame.");
                }
                moved.Add(vp.Id.Value);
            }
        }
        if(args.TryGetProperty("notes",out var texts))
        {
            if(texts.GetArrayLength()>20)throw new ArgumentException("Maximum 20 notes.");
            foreach(var n in texts.EnumerateArray())
            {
                if(n.TryGetProperty("textTypeId",out _) == n.TryGetProperty("heightMm",out _))
                    throw new ArgumentException("Specify exactly one of textTypeId or heightMm.");
                TextNoteType type;
                if(n.TryGetProperty("textTypeId",out _))
                    type=doc.GetElement(new ElementId(HostedReadId(n,"textTypeId"))) as TextNoteType
                        ?? throw new ArgumentException("textTypeId must identify an existing native text type.");
                else
                {
                    var h=BuildingNumber(n,"heightMm",1.8,7);var name="WEAM — Arial "+h.ToString(System.Globalization.CultureInfo.InvariantCulture)+" мм";
                    var types=new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToArray();
                    type=types.FirstOrDefault(t=>t.Name==name)!;
                    if(type is null){type=(TextNoteType)types.First().Duplicate(name);type.get_Parameter(BuiltInParameter.TEXT_SIZE).Set(SheetUnits(h));type.get_Parameter(BuiltInParameter.TEXT_FONT).Set("Arial");}
                }
                var text=JsonTools.GetString(n,"text");if(text.Length is <1 or >4000)throw new ArgumentException("Note text length out of range.");
                var x=BuildingNumber(n,"xMm",20,410);var y=BuildingNumber(n,"yMm",10,290);var width=BuildingNumber(n,"widthMm",10,390);
                if(x+width>415)throw new ArgumentException("Note exceeds right frame.");
                var note=TextNote.Create(doc,sheet.Id,Position(x,y),SheetUnits(width),text,new TextNoteOptions(type.Id){HorizontalAlignment=HorizontalTextAlignment.Left});notes.Add(note.Id.Value);
            }
        }
        return new {sheetId=sheet.Id.Value,viewportIds=moved,textNoteIds=notes};
    }
}
