using Ac2Revit;

// Routine checkpoint saves at most once per interval; the first one is always taken.
{
    var t0=new DateTime(2026,11,5,1,0,0,DateTimeKind.Utc);
    if(!CheckpointPolicy.SaveDue(null,t0))throw new Exception("First checkpoint must save");
    if(CheckpointPolicy.SaveDue(t0,t0.AddSeconds(30)))throw new Exception("Checkpoint 30 s after a save must be skipped");
    if(!CheckpointPolicy.SaveDue(t0,t0+CheckpointPolicy.SaveInterval))throw new Exception("Checkpoint at the interval must save");
    Console.WriteLine("PASS checkpoint saves are time-based");
}

// Site: Archicad north / survey point -> Revit shared coordinates and true north.
{
    System.Text.Json.JsonElement SiteJson(string text)=>System.Text.Json.JsonDocument.Parse(text).RootElement;
    bool SiteNear(double a,double b,double tol=1e-9)=>Math.Abs(a-b)<=tol;
    // Real Kaspiysk / Kizlyar capture: Archicad defaults, north along project +Y, no georeferencing.
    var kaspiysk=SiteGeometry.Plan(SiteJson("""{"request":{},"response":{"succeeded":true,"result":{"addOnCommandResponse":{"projectLocation":{"longitude":37.583333333,"latitude":55.75,"altitude":0,"north":1.570796327},"surveyPoint":{"position":{"eastings":0,"northings":0,"elevation":0},"positionInProject":{"x":0,"y":0,"z":0},"geoReferencingParameters":{"crsName":"","xAxisAbscissa":1,"xAxisOrdinate":0,"scale":1},"isVisible":false,"isLocked":false}}}}}"""))!;
    if(kaspiysk.Mode!="trueNorth" || !SiteNear(kaspiysk.AngleRadians,0,1e-8) || kaspiysk.EastWest!=0 || kaspiysk.NorthSouth!=0 || kaspiysk.LatitudeDegrees!=55.75)throw new Exception("Default Archicad site must keep the project axes");
    if(!kaspiysk.Notes.Any(n=>n.Contains("по умолчанию")))throw new Exception("Archicad default location must be reported");
    // North rotated 30° clockwise from project +Y: Archicad north = 60°; the Archicad north vector must become shared +Y.
    var rotated=SiteGeometry.Plan(SiteJson("""{"projectLocation":{"latitude":43.0,"longitude":47.5,"altitude":-25,"north":1.0471975511965976},"surveyPoint":{"position":{"eastings":0,"northings":0,"elevation":0},"positionInProject":{"x":0,"y":0,"z":0},"geoReferencingParameters":{"xAxisAbscissa":1,"xAxisOrdinate":0,"scale":1}}}"""))!;
    var north=rotated.ToShared(Math.Cos(Math.PI/3),Math.Sin(Math.PI/3),0);
    if(rotated.Mode!="trueNorth" || !SiteNear(north.E,0,1e-12) || !SiteNear(north.N,1,1e-12) || rotated.Elevation!=-25 || rotated.Notes.Count!=0)throw new Exception("Archicad north must map to Revit true north");
    // Georeferenced: survey point at project (12, -4, 1.5) has E/N/H; the grid x axis is turned 10°.
    double g=10*Math.PI/180;
    string SiteF(double v)=>v.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
    var survey=SiteGeometry.Plan(SiteJson("{\"projectLocation\":{\"latitude\":44.04,\"longitude\":43.06,\"altitude\":0,\"north\":"+SiteF(Math.PI/2-g+0.002)+"},\"surveyPoint\":{\"position\":{\"eastings\":512345.25,\"northings\":4876543.5,\"elevation\":318.2},\"positionInProject\":{\"x\":12,\"y\":-4,\"z\":1.5},\"geoReferencingParameters\":{\"crsName\":\"МСК-26\",\"xAxisAbscissa\":"+SiteF(Math.Cos(g))+",\"xAxisOrdinate\":"+SiteF(Math.Sin(g))+",\"scale\":1}}}"))!;
    var point=survey.ToShared(12,-4,1.5);
    if(survey.Mode!="survey" || !SiteNear(point.E,512345.25,1e-6) || !SiteNear(point.N,4876543.5,1e-6) || !SiteNear(point.Z,318.2,1e-9) || !SiteNear(survey.AngleRadians,g,1e-12))throw new Exception("Survey point must keep its eastings/northings/elevation");
    var along=survey.ToShared(13,-4,1.5);
    if(!SiteNear(along.E-point.E,Math.Cos(g),1e-9) || !SiteNear(along.N-point.N,Math.Sin(g),1e-9))throw new Exception("Project X must follow the grid rotation");
    if(!survey.Notes.Any(n=>n.Contains("МСК-26")) || !survey.Notes.Any(n=>n.Contains("отличается")))throw new Exception("CRS and grid / true north difference must be reported");
    if(SiteGeometry.Plan(SiteJson("{}"))!=null || SiteGeometry.Plan(null)!=null)throw new Exception("Missing geolocation must not invent a site");
    bool rejected=false;try{SiteGeometry.Plan(SiteJson("""{"projectLocation":{"latitude":95,"longitude":0,"north":1.5707963}}"""));}catch(InvalidDataException){rejected=true;}
    if(!rejected)throw new Exception("Invalid latitude accepted");
    // Units: Russian practice (mm / m² / m³ / degrees) and an unsupported angle unit.
    var units=SiteGeometry.Units(SiteJson("""{"length":{"unit":"Millimeter","accuracy":"Off","decimals":0},"area":{"unit":"SquareMeter","accuracy":"Off","decimals":2},"volume":{"unit":"CubicMeter","accuracy":"Off","decimals":3},"angle":{"unit":"DecimalDegree","decimals":2,"accuracy":0}}"""))!;
    if(units.Length!="Millimeters" || units.LengthDecimals!=0 || units.Area!="SquareMeters" || units.VolumeDecimals!=3 || units.Angle!="Degrees" || units.Notes.Count!=0)throw new Exception("Archicad units mapped incorrectly");
    var feet=SiteGeometry.Units(SiteJson("""{"length":{"unit":"FootFracInch","accuracy":"Off","decimals":0},"angle":{"unit":"Surveyors","decimals":0,"accuracy":0}}"""))!;
    if(feet.Length!="FeetFractionalInches" || feet.Area!="SquareMeters" || feet.Angle!="Degrees")throw new Exception("Imperial / surveyor units mapped incorrectly");
    if(SiteGeometry.Units(SiteJson("{}"))!=null)throw new Exception("Missing units must keep the template");
    Console.WriteLine("PASS site: Archicad north, survey point, grid rotation and units map to Revit shared coordinates");
}

// Session folders: the 1.6.3 failure on «МКД_8 эт_Кизляр_Общая модель_» crossed MAX_PATH in a deep stage file.
{
    var when=new DateTime(2026,11,4,22,4,5);
    // Deepest file of a real offline Kizlyar run, with the shortened stage folder names.
    string Deepest(string sessions,string name)=>Path.Combine(sessions,name,"prep-20261104-223308","run-20261104-193831","beam-void-families","AC2R_BeamCuts_A77A26224D8E445385C083CFA1056667.rfa");
    string perUser=@"C:\Users\davud\AppData\Local\BotPlus.RT\Ac2Revit\sessions";
    string kizlyar=SessionPaths.FolderName("МКД_8 эт_Кизляр_Общая модель_",perUser.Length,when);
    if(kizlyar!="МКД_8 эт_Кизляр_Общая модель-20261104-220405")throw new Exception("Per-user sessions keep the readable project name: "+kizlyar);
    if(Deepest(perUser,kizlyar).Length>SessionPaths.MaxPath)throw new Exception("Kizlyar stage file exceeds MAX_PATH");
    // A development tree or an unusually deep profile still stays within the limit by shortening the name.
    string installed=@"C:\Users\davud\AppData\Local\BotPlus.RT\Application\versions\1.6.3\modules\ac2revit\artifacts\sessions";
    string shortened=SessionPaths.FolderName("МКД_8 эт_Кизляр_Общая модель_",installed.Length,when);
    if(Deepest(installed,shortened).Length>SessionPaths.MaxPath)throw new Exception("Deep session root not compensated: "+Deepest(installed,shortened).Length);
    if(!shortened.StartsWith("МКД_8"))throw new Exception("Shortened name must keep its beginning: "+shortened);
    string longName=SessionPaths.FolderName(new string('Ж',90)+" ",perUser.Length,when);
    if(longName!=new string('Ж',32)+"-20261104-220405")throw new Exception("Long project names are capped at 32 characters: "+longName);
    string deepProfile=@"C:\Users\Очень_Длинное_Имя_Пользователя_Компании_Проектировщиков\AppData\Local\BotPlus.RT\Application\versions\10.12.300\modules\ac2revit\artifacts\sessions";
    if(SessionPaths.FolderName("Проект ДОО Каспийск",deepProfile.Length,when)!="Проект Д-20261104-220405")throw new Exception("At least eight readable characters are kept");
    if(SessionPaths.FolderName(" ..<>:|?* ",perUser.Length,when)!="Archicad-20261104-220405")throw new Exception("Invalid characters must not form a folder name");
    if(SessionPaths.FolderName("Проект. ",perUser.Length,when)!="Проект-20261104-220405")throw new Exception("Trailing dot/space are invalid for Windows folders");
    Console.WriteLine("PASS session folders keep stage files within MAX_PATH");
}

var zeroWall=new ElementRecord{Kind="Wall",Raw=System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("""
{"details":{"geometryType":"Straight","structureType":"Basic","profileType":"Normal","begThickness":0,"endThickness":0,"arcAngle":0,"offset":0,"zCoordinate":8.4,"height":3.3,"zoneRel":"Boundary","begCoordinate":{"x":-350,"y":-966},"endCoordinate":{"x":-350,"y":-965.8}}}
""")};
var zero=SourceZeroThicknessWall.Read(zeroWall);
if(!zero.RoomBoundary || Math.Abs(zero.Height-3.3)>1e-9 || zero.Curve.Start.Z!=8.4)throw new Exception("Zero-thickness source geometry changed");
foreach(string replacement in new[]{"\"begThickness\":0.001","\"begThickness\":-0.001","\"absentThickness\":0"})
{
    var other=new ElementRecord{Kind="Wall",Raw=System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(zeroWall.Raw.GetRawText().Replace("\"begThickness\":0",replacement))};
    if(SourceZeroThicknessWall.IsCandidate(other))throw new Exception("Nonzero/missing thickness treated as a zero-width boundary");
}
Console.WriteLine("PASS: zero-thickness source classification preserves axis/height and rejects missing/nonzero width");

long gb=1024L*1024*1024;
if(CheckpointPolicy.ShouldReopen(false,7*gb,10*gb))throw new Exception("Rejected bodies must not trigger a reopen");
if(CheckpointPolicy.ShouldReopen(true,9*gb,9*gb))throw new Exception("High baseline without growth must not loop");
if(!CheckpointPolicy.ShouldReopen(true,7*gb,9*gb))throw new Exception("Committed growth must checkpoint");
if(!CheckpointPolicy.ShouldReopen(true,9*gb,10*gb))throw new Exception("Growth after an already high baseline must checkpoint");
Console.WriteLine("PASS: checkpoint progress and memory-growth guards");
var gridNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach(string label in new[]{"1*","1*","Н*","Н*","A","a"})
{
    string name=GridLabels.Unique(label,gridNames);
    if(GridLabels.Visible(name)!=label || name.Contains("IFC"))throw new Exception("Visible source grid label changed");
}
if(gridNames.Count!=6)throw new Exception("Duplicate visible grid labels were lost");
Console.WriteLine("PASS: repeated grid labels remain visually identical and internally unique");
var slabScope=new ImportSelection(["Slab"]);
if(!slabScope.AllowsStage("wallsSlabsMorphs") || slabScope.AllowsStage("wallCompletion") || slabScope.AllowsStage("wallJoinsAndRooms"))throw new Exception("Slab scope leaked into walls/rooms");
if(!slabScope.IncludesType("Floor") || slabScope.IncludesType("Wall"))throw new Exception("Unselected host types leaked into resources");
var windowScope=new ImportSelection(["Window"]);
if(!windowScope.Includes("Wall") || windowScope.Includes("Door") || windowScope.Includes("Zone"))throw new Exception("Window dependencies differ from the declared scope");
var voidScope=new ImportSelection(["Opening"],[("Opening","Slab"),("Opening","Beam")]);
if(!voidScope.Includes("Slab") || !voidScope.Includes("Beam") || voidScope.Includes("Object"))throw new Exception("Explicit opening hosts were not included precisely");
var roomScope=new ImportSelection(["Zone"]);
if(!roomScope.Includes("Column") || !roomScope.Includes("Wall") || roomScope.Includes("Object"))throw new Exception("Spatial dependency scope is incorrect");
try{_ =new ImportSelection([]);throw new Exception("Empty selection accepted");}catch(InvalidDataException){}
Console.WriteLine("PASS: category selection and physical host dependencies");

string referenceFile=Path.GetTempFileName();
try
{
    File.WriteAllText(referenceFile,"original IFC fixture");
    string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(referenceFile)));
    using(var scope=new VerifiedReferenceScope())
    {
        VerifiedReferenceScope.Verify(referenceFile,hash);
        VerifiedReferenceScope.Verify(referenceFile,hash.ToLowerInvariant());
        using(var reader=File.OpenRead(referenceFile))if(reader.Length==0)throw new Exception("Concurrent reference readers must work");
        try{using var writer=File.Open(referenceFile,FileMode.Open,FileAccess.Write,FileShare.ReadWrite);throw new Exception("Reference writes allowed during verified lease");}
        catch(IOException){}
        try{VerifiedReferenceScope.Verify(referenceFile,new string('0',64));throw new Exception("Conflicting expected hash accepted");}
        catch(InvalidDataException){}
    }
    File.WriteAllText(referenceFile,"changed after lease released");
    try{VerifiedReferenceScope.Verify(referenceFile,hash);throw new Exception("Changed file accepted by a new scope");}
    catch(InvalidDataException){}
    File.WriteAllText(referenceFile,"reopened after failed verification");
}
finally{File.Delete(referenceFile);}
Console.WriteLine("PASS: reference lease allows readers, blocks writes, rejects conflicting/changed inputs and releases handles");

Point P(double x,double y)=>new(){X=x,Y=y};
void Near(double actual,double expected,double tolerance=1e-9)
{if(Math.Abs(actual-expected)>tolerance)throw new Exception($"Source analytic area {actual:G17} != {expected:G17}");}
foreach(double sweep in new[]{Math.PI,-Math.PI,1.5*Math.PI,-1.5*Math.PI,1e-4})
{
    var arc=new Edge{Kind="arc",Start=P(0,0),Mid=P(1,-Math.Tan(sweep/4)),End=P(2,0)};
    double radius=1/Math.Abs(Math.Sin(sweep/2));
    Near(SourcePlanarGeometry.SignedArea(new[]{arc,new Edge{Start=P(2,0),End=P(0,0)}}),radius*radius*(sweep-Math.Sin(sweep))/2,1e-8);
}
var largeArc=new Edge{Kind="arc",Start=P(500000,-200000),Mid=P(500001,-200001),End=P(500002,-200000)};
Near(SourcePlanarGeometry.SignedArea(new[]{largeArc,new Edge{Start=largeArc.End,End=largeArc.Start}}),Math.PI/2);
Console.WriteLine("PASS: analytic minor/major/reversed/tiny arcs and large-coordinate area checks");
var limited=new ImportSelection(new[]{"Zone","Wall"});limited.RestrictTo(new[]{"Zone","Wall"});
if(limited.Includes("Door") || limited.Includes("Window"))throw new Exception("Excluded export fillings returned as room dependencies");
try{new ImportSelection(new[]{"Door"}).RestrictTo(new[]{"Wall"});throw new Exception("Empty export intersection accepted");}
catch(InvalidDataException){ }
Console.WriteLine("PASS: export category limits override automatic dependencies");
if(SourceLabel.Format("1-й этаж")!="АС «1-й этаж»" || SourceLabel.Format(" Кирпич 250 ")!="АС «Кирпич 250»" || SourceLabel.Format("")!="АС «Без названия»")
    throw new Exception("Source label formatting changed source text or appended an identity");
if(SourceLabel.Format("Стена: [250]")!="АС «Стена_ _250_»")throw new Exception("Invalid Revit name characters were retained");
Console.WriteLine("PASS: readable source labels preserve floor numbers/dimensions and contain no generated suffix");
var meshScope=new ImportSelection(["Mesh"]);
if(!meshScope.AllowsStage("nativeTerrain") || new ImportSelection(["Wall"]).AllowsStage("nativeTerrain"))throw new Exception("Mesh checkbox does not control native terrain stage");
Console.WriteLine("PASS Mesh checkbox enables only the selected native terrain stage");
var progress=new Ac2Revit.ImportProgressState();
progress.Update("pipeline:start",0,5);progress.Update("pipeline:stage:walls",2,5);progress.Update("elements",80,100);var high=progress.Percent;
progress.Update("openingCuts",1,100);if(progress.Percent<high)throw new Exception("Global progress moved backwards between subphases");
progress.Update("memoryCheckpoint",1,128);progress.Update("pipeline:start",0,5);progress.Update("pipeline:stage:resources",0,5);if(progress.Percent<high)throw new Exception("Checkpoint reopen reset global progress");
progress.Update("pipeline:end:sourceCoverage",5,5);if(progress.Complete||progress.Percent>=100)throw new Exception("Progress completed before final report");progress.Update("pipeline:complete",5,5);if(!progress.Complete||progress.Percent!=100)throw new Exception("Missing final 100 percent");
var dim=new Ac2Revit.SourceDimension{Direction=new(){X=1},LinePosition=new(){Y=1},Witnesses=[new(){SourceGuid=Guid.NewGuid().ToString(),Coordinate=new(){X=0}},new(){SourceGuid=Guid.NewGuid().ToString(),Coordinate=new(){X=3}}]};dim.Validate();
bool failed=false;try{dim.Witnesses[1].Coordinate.X=0;dim.Validate();}catch(InvalidDataException){failed=true;}if(!failed)throw new Exception("Coincident native dimension witnesses accepted");
Console.WriteLine("PASS monotonic stage progress, checkpoint reopen, explicit completion and dimension witness validation");

if(args.Length==0)return;

var manifest = Manifest.Load(args[0]);
var ordered = manifest.OrderedElements();
var position = ordered.Select((e, i) => (e.SourceGuid, i)).ToDictionary(x => x.SourceGuid, x => x.i);
foreach (var e in ordered)
    foreach (var h in e.Dependencies)
        if (position[h] >= position[e.SourceGuid]) throw new Exception("Host must precede child");
Console.WriteLine($"PASS: {manifest.Levels.Count} levels, {manifest.Types.Count} types, {ordered.Count} elements; GUIDs and dependencies valid");
if(args.Length>1)
{
    var rows=ordered.Where(e=>e.Kind=="Zone" && e.Status=="ready").Select(e=>
    {
        var loops=e.Payload.GetProperty("loops").Read<List<List<Edge>>>();
        double area=Math.Abs(SourcePlanarGeometry.SignedArea(loops[0]))-loops.Skip(1).Sum(l=>Math.Abs(SourcePlanarGeometry.SignedArea(l)));
        return new{sourceGuid=e.SourceGuid,areaM2=area,arcCount=loops.Sum(l=>l.Count(c=>c.Kind=="arc"))};
    }).ToArray();
    File.WriteAllText(args[1],System.Text.Json.JsonSerializer.Serialize(new{sourceManifest=Path.GetFullPath(args[0]),sourceUnits="m",areas=rows},Json.Options));
    Console.WriteLine($"PASS: {rows.Length} source room areas; {rows.Count(r=>r.arcCount>0)} have analytic arcs");
}
if (ordered.Count > 1)
{
    manifest.Elements.Add(ordered[0]);
    try { manifest.Validate(); throw new Exception("Duplicate GUID accepted"); }
    catch (InvalidDataException) { Console.WriteLine("PASS: duplicate GUID rejected"); }
}

