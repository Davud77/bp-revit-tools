using Autodesk.Revit.DB;

namespace Ac2Revit;

public static class SourceNames
{
    public static void RenameFamily(Document doc,Family family,string description)
    {
        string clean=new(description.Select(c=>"\\:{}[]|;<>?`~".Contains(c)||char.IsControl(c)?'_':c).ToArray());
        var occupied=new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
            .Where(f=>f.Id!=family.Id).Select(f=>f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        family.Name=GridLabels.Unique("АС \""+clean.Trim()+"\"",occupied);
    }
}
