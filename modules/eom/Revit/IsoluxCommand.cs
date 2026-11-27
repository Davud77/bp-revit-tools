using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BotPlus.Eom;

internal sealed class PhotometryAssignment
{
    public string Type { get; init; } = "";
    public int Count { get; set; }
    public string File { get; set; } = "";
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double OffsetZ { get; set; }
    public double Rotation { get; set; }
}

internal sealed record IsoluxResult(PointLightMap Map, List<LightVector> Sources, RoomRef Room, double PlaneZ);

/// <summary>Прямой свет по IES фактических экземпляров; просмотр карты и вывод изолиний на план.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class IsoluxCommand : EomCommand
{
    protected override Result Run(UIDocument ui)
    {
        var rooms = Rooms.Collect(ui.Document);
        if (rooms.Count == 0) throw new InvalidOperationException("В проекте и загруженных связях нет размещённых помещений.");
        var catalog = EomCatalog.Load();
        var fixtures = new FilteredElementCollector(ui.Document).OfCategory(BuiltInCategory.OST_LightingFixtures)
            .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(f => f.SuperComponent == null).ToList();
        var phase = ui.ActiveView.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
        if (phase != null && phase != ElementId.InvalidElementId)
            fixtures = fixtures.Where(f => f.GetPhaseStatus(phase) is ElementOnPhaseStatus.New or ElementOnPhaseStatus.Existing).ToList();
        double M(double value) => UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Meters);
        double F(double value) => UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Meters);
        static string Type(FamilyInstance f) => $"{f.Symbol.FamilyName}: {f.Symbol.Name}";
        List<FamilyInstance> InRoom(RoomRef room)
        {
            var box = room.Room.get_BoundingBox(null) ?? throw new InvalidOperationException("Нет габаритов помещения.");
            double floor = box.Min.Z;
            var inverse = room.Transform.Inverse;
            return fixtures.Where(f =>
            {
                var p = inverse.OfPoint(f.GetTransform().Origin);
                return p.Z > floor && p.Z <= box.Max.Z + F(0.1)
                    && room.Room.IsPointInRoom(new XYZ(p.X, p.Y, Rooms.ProbeZ(box)));
            }).ToList();
        }
        var assignmentCache = new Dictionary<string, PhotometryAssignment>();
        string DefaultIes(FamilySymbol symbol, string type)
        {
            string? fromCatalog = catalog.Luminaires.FirstOrDefault(l => l.RevitType == type && !string.IsNullOrWhiteSpace(l.Photometry))?.Photometry;
            if (fromCatalog != null) return fromCatalog;
            var parameter = symbol.get_Parameter(BuiltInParameter.FBX_LIGHT_PHOTOMETRIC_FILE);
            var file = parameter is { StorageType: StorageType.String } ? parameter.AsString() : null;
            return !string.IsNullOrWhiteSpace(file) && Path.IsPathFullyQualified(file) && File.Exists(file) ? file : "";
        }
        List<PhotometryAssignment> Assignments(RoomRef room) => InRoom(room).GroupBy(Type).Select(g =>
        {
            if (!assignmentCache.TryGetValue(g.Key, out var value))
                assignmentCache[g.Key] = value = new() { Type = g.Key, File = DefaultIes(g.First().Symbol, g.Key) };
            value.Count = g.Count();
            return value;
        }).OrderBy(a => a.Type).ToList();

        IsoluxResult Calculate(RoomRef room, List<PhotometryAssignment> assignments, double height, double step, double kz)
        {
            if (!double.IsFinite(height) || height < 0) throw new InvalidOperationException("Высота рабочей поверхности должна быть неотрицательной.");
            if (Math.Abs(room.Transform.BasisZ.DotProduct(XYZ.BasisZ) - 1) > 1e-6)
                throw new InvalidOperationException("Наклонённые связи помещений не поддерживаются.");
            var box = room.Room.get_BoundingBox(null) ?? throw new InvalidOperationException("Нет габаритов помещения.");
            double floor = box.Min.Z, z = M(floor) + height;
            var photometry = new Dictionary<string, IesPhotometry>(StringComparer.OrdinalIgnoreCase);
            var sources = new List<PointLightSource>();
            LightVector Vector(XYZ v) => new(v.X, v.Y, v.Z);
            LightVector Position(XYZ v) => new(M(v.X), M(v.Y), M(v.Z));
            foreach (var f in InRoom(room))
            {
                var a = assignments.Single(v => v.Type == Type(f));
                if (new[] { a.OffsetX, a.OffsetY, a.OffsetZ, a.Rotation }.Any(v => !double.IsFinite(v)))
                    throw new InvalidOperationException($"{a.Type}: проверьте смещение и угол C0.");
                string path = a.File.Trim();
                if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".ies", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    throw new InvalidOperationException($"{a.Type}: укажите существующий абсолютный путь к IES. LDT пока не поддерживается.");
                if (!photometry.TryGetValue(path, out var ies))
                {
                    if (new FileInfo(path).Length > 16_000_000) throw new InvalidOperationException("Файл IES слишком большой.");
                    try { ies = IesPhotometry.Parse(File.ReadAllText(path)); }
                    catch (InvalidOperationException e) { throw new InvalidOperationException($"{a.Type}: {e.Message}", e); }
                    photometry[path] = ies;
                }
                var t = room.Transform.Inverse.Multiply(f.GetTransform());
                double angle = a.Rotation * Math.PI / 180;
                var x = t.OfVector(new XYZ(Math.Cos(angle), Math.Sin(angle), 0));
                var y = t.OfVector(new XYZ(-Math.Sin(angle), Math.Cos(angle), 0));
                var position = t.OfPoint(new XYZ(F(a.OffsetX), F(a.OffsetY), F(a.OffsetZ)));
                sources.Add(new(Position(position), Vector(x), Vector(y), Vector(-t.BasisZ), ies));
            }
            var map = PointLightMap.Calculate(M(box.Min.X), M(box.Min.Y), M(box.Max.X), M(box.Max.Y), z, step, kz, sources,
                (x, y) => room.Room.IsPointInRoom(new XYZ(F(x), F(y), F(z))));
            return new(map, sources.Select(s => s.Position).ToList(), room, z);
        }

        void Export(IsoluxResult result, List<LuxContour> contours, bool grid)
        {
            if (ui.ActiveView is not ViewPlan view || view.IsTemplate || view.GenLevel == null || Math.Abs(view.ViewDirection.Z) < 0.999)
                throw new InvalidOperationException("Для вывода изолиний откройте горизонтальный план этажа.");
            var room = result.Room;
            double floor = room.Room.Level?.ProjectElevation ?? room.Room.get_BoundingBox(null).Min.Z;
            if (Math.Abs(room.Transform.OfPoint(new XYZ(0, 0, floor)).Z - view.GenLevel.ProjectElevation) > F(0.5))
                throw new InvalidOperationException("Уровень активного плана не совпадает с этажом помещения.");
            if (!grid && contours.Count == 0) throw new InvalidOperationException("Изолинии выбранных уровней не пересекают расчётную область.");
            if (grid && result.Map.Points.Count > 2000) throw new InvalidOperationException("Больше 2000 узлов для вывода на план: увеличьте шаг. Полная сетка доступна в CSV.");
            if (contours.Count > 15000) throw new InvalidOperationException("Больше 15 000 сегментов: увеличьте шаг сетки или сократите число уровней.");
            XYZ P(LuxPoint p)
            {
                var host = room.Transform.OfPoint(new XYZ(F(p.X), F(p.Y), F(result.PlaneZ)));
                return new XYZ(host.X, host.Y, view.GenLevel.ProjectElevation);
            }
            using var transaction = new Transaction(ui.Document, "ЭОМ: изолинии освещённости"); transaction.Start();
            // Привязка включает положение связи: два экземпляра одного файла имеют разные результаты.
            var transform = room.Transform;
            var signature = new[] { transform.Origin, transform.BasisX, transform.BasisY, transform.BasisZ }
                .SelectMany(p => new[] { p.X, p.Y, p.Z }).Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            string key = room.Room.UniqueId + "|" + string.Join("|", signature) + (grid ? "|grid" : "|contours");
            var old = new FilteredElementCollector(ui.Document, view.Id).OfClass(typeof(Group)).Cast<Group>()
                .Where(g => EomStore.IsoluxHost(g) == key).ToList();
            foreach (var g in old)
            {
                // Удаляем только помеченные результаты BotPlus на этом виде, включая копии группы.
                var members = g.UngroupMembers();
                ui.Document.Delete(members);
            }
            var ids = new List<ElementId>();
            foreach (var c in grid ? [] : contours)
            {
                var a = P(c.A); var b = P(c.B);
                if (a.DistanceTo(b) <= ui.Application.Application.ShortCurveTolerance) continue;
                var curve = ui.Document.Create.NewDetailCurve(view, Line.CreateBound(a, b));
                var comment = curve.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (comment is { IsReadOnly: false }) comment.Set($"{c.Level:0.##} лк — прямой свет, точечный метод");
                ids.Add(curve.Id);
            }
            var textType = new FilteredElementCollector(ui.Document).OfClass(typeof(TextNoteType)).FirstElementId();
            if (textType != ElementId.InvalidElementId)
                foreach (var group in (grid ? [] : contours).GroupBy(c => c.Level))
                {
                    var c = group.MaxBy(c => Math.Pow(c.A.X - c.B.X, 2) + Math.Pow(c.A.Y - c.B.Y, 2));
                    ids.Add(TextNote.Create(ui.Document, view.Id, P(c.A), $"{c.Level:0.##} лк", textType).Id);
                }
            if (grid)
            {
                if (textType == ElementId.InvalidElementId) throw new InvalidOperationException("В проекте нет типа текста для сетки.");
                foreach (var p in result.Map.Points)
                    ids.Add(TextNote.Create(ui.Document, view.Id, P(p), $"{p.Lux:0.#}", textType).Id);
            }
            if (ids.Count == 0) throw new InvalidOperationException("Все сегменты короче допуска Revit: увеличьте шаг сетки.");
            var detailGroup = ui.Document.Create.NewGroup(ids);
            detailGroup.GroupType.Name = $"ЭОМ {(grid ? "Сетка лк" : "Изолюксы")} {room.Number} {DateTime.Now:yyyyMMdd HHmmss fff}";
            EomStore.SaveIsoluxHost(detailGroup, key);
            transaction.Commit();
            TaskDialog.Show("BotPlus ЭОМ", $"{(grid ? "Сетка освещённости" : "Изолинии")} помещены на план «{view.Name}» отдельной группой. Элементов: {ids.Count}. Прежних групп заменено: {old.Count}.");
        }

        var selected = ui.Selection.GetElementIds().Select(ui.Document.GetElement).OfType<Autodesk.Revit.DB.Architecture.Room>().FirstOrDefault();
        IsoluxWindow.Show(rooms, selected == null ? null : rooms.FirstOrDefault(r => ReferenceEquals(r.Room.Document, ui.Document) && r.Room.Id == selected.Id),
            EomStore.RoomLights(ui.Document), Assignments, Calculate, Export);
        return Result.Succeeded;
    }
}
