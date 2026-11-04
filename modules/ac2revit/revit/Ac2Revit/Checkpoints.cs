using Autodesk.Revit.DB;

namespace Ac2Revit;

/// <summary>Time-based checkpoint saves of the document being built (see CheckpointPolicy.SaveDue).</summary>
public static class Checkpoints
{
    static readonly Dictionary<string,DateTime> lastSave=new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Saves when due, or always with <paramref name="force"/>. Returns whether the document was saved.</summary>
    public static bool Save(Document doc,bool force=false)
    {
        if(string.IsNullOrEmpty(doc.PathName))return false;
        var now=DateTime.UtcNow;
        if(!force && !CheckpointPolicy.SaveDue(lastSave.TryGetValue(doc.PathName,out var last)?last:null,now))return false;
        doc.Save();lastSave[doc.PathName]=DateTime.UtcNow;return true;
    }
    /// <summary>A newly opened document starts its interval now (it was just saved or loaded from disk).</summary>
    public static void Opened(Document doc){if(!string.IsNullOrEmpty(doc.PathName))lastSave[doc.PathName]=DateTime.UtcNow;}
}