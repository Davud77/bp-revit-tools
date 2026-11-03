namespace Ac2Revit;

/// <summary>Visible source labels contain no generated identity suffix; identity belongs to Storage.</summary>
public static class SourceLabel
{
    public static string Format(string? source)
    {
        string clean=new((source??"").Select(c=>"\\:{}[]|;<>?`~".Contains(c)||char.IsControl(c)?'_':c).ToArray());
        clean=clean.Trim();
        return "АС «"+(clean.Length==0?"Без названия":clean)+"»";
    }
}
