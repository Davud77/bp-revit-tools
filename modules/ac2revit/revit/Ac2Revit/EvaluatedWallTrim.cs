using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>Remove only proven excess geometry; retain native wall types, layers and identity.</summary>
public static class EvaluatedWallTrim
{
    public static Report Run(Document doc,string input,string template,string folder,int limit=0)
    {
        using var json=JsonDocument.Parse(File.ReadAllText(input));var root=json.RootElement;
        if(root.S("format")!="ac2r.evaluated-wall-bodies/1")throw new InvalidDataException("Unknown evaluated wall format");
        foreach(var archive in root.GetProperty("sourceArchives").EnumerateArray())
            if(!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive.S("path")))).Equals(archive.S("sha256"),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Evaluated source archive changed");
        string project=root.S("projectId");var index=Storage.Index(doc,project);var report=new Report{ProjectId=project,Mode="evaluatedNativeWallTrim"};
        var coordinates=root.GetProperty("coordinates").Read<CoordinateInfo>().Transform;
        var sourceToWorld=Transform.CreateTranslation(NativeGeometry.At(coordinates.Translation)).Multiply(Transform.CreateRotation(XYZ.BasisZ,coordinates.RotationRadians));
        Directory.CreateDirectory(folder);
        var records=root.GetProperty("elements").EnumerateArray().Where(r=>index.GetValueOrDefault(r.S("sourceGuid")) is Wall).ToList();if(limit>0)records=records.Take(limit).ToList();
        foreach(var record in records)
        {
            string key=record.S("sourceGuid"),cutKey="wall-net-trim:"+key;
            string stage="source solids";
            using var group=new TransactionGroup(doc,"Подрезка стены по исходной геометрии");
            try
            {
                var wall=(Wall)index[key];var box=wall.get_BoundingBox(null)??throw new InvalidDataException("Wall bounds unavailable");
                var center=(box.Min+box.Max)/2;var toLocal=Transform.CreateTranslation(center.Negate());
                var local=record.GetProperty("bodies").EnumerateArray().SelectMany(b=>NativeGeometry.MeshSolids(b,toLocal.Multiply(sourceToWorld))).ToList();
                void Verify()
                {
                    var actual=NativeGeometry.Solids(wall).Select(s=>SolidUtils.CreateTransformed(s,toLocal)).ToList();
                    try{NativeGeometry.VerifySolids(actual,local,1e-5);}
                    catch(Autodesk.Revit.Exceptions.InvalidOperationException){PlanarSolidVerification.Verify(actual,local,folder,key);}
                    catch(Autodesk.Revit.Exceptions.InternalException){PlanarSolidVerification.Verify(actual,local,folder,key);}
                }
                var expected=local.Select(s=>SolidUtils.CreateTransformed(s,Transform.CreateTranslation(center))).ToList();
                var before=NativeGeometry.Solids(wall);if(before.Count==0 || expected.Count==0)throw new InvalidDataException("Native/source solid unavailable");
                if(NativeGeometry.DifferenceVolume(expected,before)>1e-5)throw new InvalidDataException("Source body extends outside the native wall; cutting alone cannot reproduce it");
                if(NativeGeometry.DifferenceVolume(before,expected)<=1e-5)
                {report.Add(key,"WallTrim","unchanged","Native solid already agrees with evaluated source",wall.UniqueId);continue;}
                if(index.ContainsKey(cutKey))throw new InvalidDataException("Existing trim differs; automatic replacement is not allowed");
                var cuts=new List<Solid>();
                foreach(var solid in before)
                {
                    var excess=solid;
                    foreach(var target in expected){if(excess.Volume<1e-12)break;excess=BooleanOperationsUtils.ExecuteBooleanOperation(excess,target,BooleanOperationsType.Difference);}
                    if(excess.Volume>1e-12)cuts.AddRange(SolidUtils.SplitVolumes(SolidUtils.CreateTransformed(excess,toLocal)));
                }
                if(cuts.Count==0)throw new InvalidDataException("No valid excess solids");
                string file=Path.Combine(folder,"trim-"+key+".rfa");
                stage="void family";
                CutBeamReplacer.BuildVoid(doc.Application,template,file,cuts,report,key);
                group.Start();
                using(var transaction=new Transaction(doc,"Native wall trim"))
                {
                    transaction.Start();NativeGeometry.Configure(transaction,report,key,"WallTrim");
                    if(!doc.LoadFamily(file,out var family) || family==null)throw new InvalidDataException("Cannot load trim family");
                    SourceNames.RenameFamily(doc,family,"Подрезка стены — "+record.S("mark"));
                    var symbol=(FamilySymbol)doc.GetElement(family.GetFamilySymbolIds().First());symbol.Activate();doc.Regenerate();
                    var cutter=doc.Create.NewFamilyInstance(center,symbol,StructuralType.NonStructural);doc.Regenerate();
                    if(!InstanceVoidCutUtils.CanBeCutWithVoid(wall))throw new InvalidDataException("Wall cannot be cut by a native void");
                    InstanceVoidCutUtils.AddInstanceVoidCut(doc,wall,cutter);doc.Regenerate();
                    stage="local Boolean proof before commit";
                    Verify();
                    Storage.Tag(cutter,project,cutKey,Json.Hash(record),record.GetRawText());
                    if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("Trim transaction rolled back");
                    index[cutKey]=cutter;
                }
                // Re-check after Revit's transaction-end cleanup before accepting the group.
                stage="local Boolean proof after commit";
                Verify();group.Assimilate();
                report.Add(key,"WallTrim","corrected","Native wall retained; verified against evaluated source by Boolean differences or complete planar coverage and solid volume",wall.UniqueId);
            }
            catch(Exception error)
            {
                if(group.GetStatus()==TransactionStatus.Started)group.RollBack();
                report.Add(key,"WallTrim","unverified",stage+": "+error.Message);
            }
        }
        return report;
    }
}
