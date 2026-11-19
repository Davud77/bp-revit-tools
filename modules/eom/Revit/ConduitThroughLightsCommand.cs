using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitCanceled = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace BotPlus.Eom;

[Transaction(TransactionMode.Manual)]
public sealed class ConduitThroughLightsCommand : EomCommand
{
    const string BoxFamily = "TSL_CNF_т_Короб_Коробка распределительная";
    const string ElbowFamily = "TSL_CNF_т_Короб_Поворот на 90°";
    const double PointTolerance = 0.0001; // футы
    const double DirectionTolerance = 0.99999;

    protected override Result Run(UIDocument ui)
    {
        var doc = ui.Document;
        var symbol = FittingType(doc, BoxFamily);
        if (symbol == null) throw new InvalidOperationException($"Загрузите семейство «{BoxFamily}» в проект.");

        var startPick = ui.Selection.PickObject(ObjectType.Element, new RouteFilter(false),
            "Начало трассы: выберите светильник, свободный конец короба или распределительную коробку.");
        var source = doc.GetElement(startPick.ElementId);
        XYZ? picked = startPick.GlobalPoint;
        var type = ChooseType(doc, source, symbol);
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToArray();
        var visited = new HashSet<ElementId> { source.Id };
        double? diameter = source is Conduit conduit ? conduit.Diameter : null;
        bool built = false;

        while (true)
        {
            Reference pick;
            try
            {
                pick = ui.Selection.PickObject(ObjectType.Element, new RouteFilter(true, visited),
                    "Выберите следующий светильник для установки коробки. Esc — завершить трассу.");
            }
            catch (RevitCanceled) { return built ? Result.Succeeded : Result.Cancelled; }

            var fixture = (FamilyInstance)doc.GetElement(pick.ElementId);
            try
            {
                using var transaction = new Transaction(doc, "ЭОМ: короб до светильника");
                transaction.Start();
                var (box, size) = AddLeg(doc, source, picked, fixture, symbol, type, levels, diameter);
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Revit отменил построение участка.");
                source = box;
                diameter = size;
                picked = null;
                visited.Add(fixture.Id);
                built = true;
                ui.RefreshActiveView();
            }
            catch (Exception error) when (error is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                TaskDialog.Show("Короб через светильники", error.Message);
            }
        }
    }

    static FamilySymbol? FittingType(Document doc, string familyName) => new FilteredElementCollector(doc)
        .OfCategory(BuiltInCategory.OST_ConduitFitting).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
        .Where(s => string.Equals(s.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(s => s.Name == familyName).ThenBy(s => s.Id.Value).FirstOrDefault();

    static ConduitType ChooseType(Document doc, Element source, FamilySymbol box)
    {
        if (source is Conduit conduit) return (ConduitType)doc.GetElement(conduit.GetTypeId());
        var connected = Ports(source).SelectMany(p => p.AllRefs.Cast<Connector>()).Select(p => p.Owner)
            .OfType<Conduit>().FirstOrDefault();
        if (connected != null) return (ConduitType)doc.GetElement(connected.GetTypeId());
        var usedWithBox = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ConduitFitting)
            .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(f => f.GetTypeId() == box.Id)
            .SelectMany(Ports).SelectMany(p => p.AllRefs.Cast<Connector>()).Select(p => p.Owner)
            .OfType<Conduit>().FirstOrDefault();
        if (usedWithBox != null) return (ConduitType)doc.GetElement(usedWithBox.GetTypeId());
        var preferred = doc.GetDefaultElementTypeId(ElementTypeGroup.ConduitType);
        return new FilteredElementCollector(doc).OfClass(typeof(ConduitType)).Cast<ConduitType>()
            .Where(t => t.IsWithFitting).OrderByDescending(t => t.Id == preferred).ThenBy(t => t.Id.Value).FirstOrDefault()
            ?? throw new InvalidOperationException("В проекте нет типа короба с соединительными деталями.");
    }

    static (FamilyInstance Box, double Diameter) AddLeg(Document doc, Element source, XYZ? picked, FamilyInstance fixture,
        FamilySymbol symbol, ConduitType type, Level[] levels, double? diameter)
    {
        var target = FixturePoint(fixture);
        var sourcePorts = Ports(source);
        XYZ startPoint = sourcePorts.FirstOrDefault()?.Origin ?? (source is FamilyInstance light && IsLight(light)
            ? FixturePoint(light) : throw new InvalidOperationException("У начала трассы нет концевого соединителя короба."));
        var level = doc.GetElement(source.LevelId) as Level
            ?? levels.MinBy(l => Math.Abs(l.ProjectElevation - startPoint.Z))
            ?? throw new InvalidOperationException("В проекте нет уровня для построения короба.");

        if (sourcePorts.Length == 0)
        {
            // Семейство светильника может иметь только электрический соединитель.
            var direction = Horizontal(target - startPoint);
            source = PutBox(doc, symbol, level, startPoint, direction, diameter);
            sourcePorts = Ports(source);
        }

        var free = sourcePorts.Where(p => !p.IsConnected && MatchesDiameter(p, diameter));
        var start = free.OrderBy(p => picked != null && source is Conduit ? Horizontal(p.Origin - picked).GetLength() : 0)
            .ThenByDescending(p => DirectionScore(p, target))
            .ThenBy(p => p.Id).FirstOrDefault()
            ?? throw new InvalidOperationException("У начала трассы нет свободного вывода подходящего диаметра.");
        double size = diameter ?? start.Radius * 2;
        var axis = start.CoordinateSystem.BasisZ;
        var toward = Horizontal(target - start.Origin);
        if (toward.GetLength() < doc.Application.ShortCurveTolerance)
            throw new InvalidOperationException("Светильник слишком близко к началу участка.");

        XYZ? corner = null;
        if (Math.Abs(axis.Z) >= DirectionTolerance)
        {
            var lead = UnitUtils.ConvertToInternalUnits(150, UnitTypeId.Millimeters);
            corner = start.Origin + axis * lead;
        }
        else if (Math.Abs(axis.Z) > 1 - DirectionTolerance)
            throw new InvalidOperationException("Наклонный вывод в начале участка требует отдельного перехода.");
        else if (axis.DotProduct(toward.Normalize()) < DirectionTolerance)
        {
            double along = toward.DotProduct(axis);
            if (along <= doc.Application.ShortCurveTolerance)
                throw new InvalidOperationException("Свободный выход направлен в сторону от светильника. Выберите следующий светильник по направлению выхода.");
            corner = start.Origin + axis * along;
        }

        var approach = corner ?? start.Origin;
        var center = new XYZ(target.X, target.Y, approach.Z);
        var boxFace = Horizontal(approach - center);
        var box = PutBox(doc, symbol, level, center, boxFace, size);
        var inlet = FacingPort(box, boxFace, size);
        if (corner == null)
        {
            var segment = BuildSegment(doc, type, level, start.Origin, inlet.Origin, size);
            Join(start, EndAt(segment, start.Origin));
            Join(EndAt(segment, inlet.Origin), inlet);
        }
        else if (Math.Abs(axis.Z) < 1 - DirectionTolerance && FittingType(doc, ElbowFamily) is { } elbow)
        {
            var (elbowInlet, elbowOutlet) = PutPlanElbow(doc, elbow, level, corner, axis.Negate(), boxFace.Normalize().Negate(), size);
            var first = BuildSegment(doc, type, level, start.Origin, elbowInlet.Origin, size);
            var last = BuildSegment(doc, type, level, elbowOutlet.Origin, inlet.Origin, size);
            Join(start, EndAt(first, start.Origin));
            Join(elbowInlet, EndAt(first, elbowInlet.Origin));
            Join(elbowOutlet, EndAt(last, elbowOutlet.Origin));
            Join(inlet, EndAt(last, inlet.Origin));
        }
        else
        {
            var first = BuildSegment(doc, type, level, start.Origin, corner, size);
            var last = BuildSegment(doc, type, level, corner, inlet.Origin, size);
            Join(start, EndAt(first, start.Origin));
            Join(inlet, EndAt(last, inlet.Origin));
            var firstEnd = EndAt(first, corner);
            var lastEnd = EndAt(last, corner);
            if (!firstEnd.IsConnected && !lastEnd.IsConnected)
            {
                doc.Create.NewElbowFitting(firstEnd, lastEnd);
                firstEnd = EndAt(first, corner);
                lastEnd = EndAt(last, corner);
            }
            if (!firstEnd.AllRefs.Cast<Connector>().Where(p => p.Owner.Id != first.Id)
                .Select(p => p.Owner.Id).Intersect(lastEnd.AllRefs.Cast<Connector>().Where(p => p.Owner.Id != last.Id)
                    .Select(p => p.Owner.Id)).Any())
                throw new InvalidOperationException("Revit не соединил оба участка через отвод.");
        }
        return (box, size);
    }

    static (Connector Inlet, Connector Outlet) PutPlanElbow(Document doc, FamilySymbol symbol, Level level,
        XYZ corner, XYZ incoming, XYZ outgoing, double diameter)
    {
        if (!symbol.IsActive) symbol.Activate();
        var elbow = doc.Create.NewFamilyInstance(corner, symbol, level, StructuralType.NonStructural);
        doc.Regenerate();
        var ports = Ports(elbow);
        if (ports.Length != 2 || ports.Any(p => !MatchesDiameter(p, diameter)))
            throw new InvalidOperationException("Диаметр отводов TSL должен совпадать с диаметром короба.");
        var first = ports[0];
        var second = ports[1];
        XYZ firstAxis = first.CoordinateSystem.BasisZ;
        XYZ secondAxis = second.CoordinateSystem.BasisZ;
        double orientation = firstAxis.CrossProduct(secondAxis).Z;
        if (Math.Abs(orientation) < DirectionTolerance)
            throw new InvalidOperationException("Для поворота требуются два горизонтальных вывода под 90°.");
        if (orientation * incoming.CrossProduct(outgoing).Z < 0)
        {
            (first, second) = (second, first);
            firstAxis = secondAxis;
        }
        double angle = Math.Atan2(firstAxis.CrossProduct(incoming).Z, firstAxis.DotProduct(incoming));
        if (Math.Abs(angle) > PointTolerance)
            ElementTransformUtils.RotateElement(doc, elbow.Id, Line.CreateUnbound(corner, XYZ.BasisZ), angle);
        doc.Regenerate();
        var origin = first.Origin;
        var intersection = origin + incoming * (second.Origin - origin).DotProduct(incoming);
        ElementTransformUtils.MoveElement(doc, elbow.Id, corner - intersection);
        doc.Regenerate();
        return (first, second);
    }

    static FamilyInstance PutBox(Document doc, FamilySymbol symbol, Level level, XYZ center, XYZ facing, double? diameter)
    {
        if (facing.GetLength() < doc.Application.ShortCurveTolerance)
            throw new InvalidOperationException("Недостаточно места между коробкой и началом участка.");
        var extent = new XYZ(PointTolerance, PointTolerance, PointTolerance);
        var nearby = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ConduitFitting)
            .OfClass(typeof(FamilyInstance)).WherePasses(new BoundingBoxIntersectsFilter(new Outline(center - extent, center + extent)))
            .Cast<FamilyInstance>().Any(box => box.GetTypeId() == symbol.Id
                && box.Location is LocationPoint point && Horizontal(point.Point - center).GetLength() <= PointTolerance);
        if (nearby) throw new InvalidOperationException("В этой точке уже есть распределительная коробка. Её можно выбрать как начало трассы.");
        if (!symbol.IsActive) symbol.Activate();
        var box = doc.Create.NewFamilyInstance(new XYZ(center.X, center.Y, level.ProjectElevation), symbol, level, StructuralType.NonStructural);
        doc.Regenerate();
        var ports = Ports(box);
        if (ports.Length < 2) throw new InvalidOperationException("У распределительной коробки должно быть не менее двух круглых концевых соединителей.");
        var inlet = FacingPort(box, facing, diameter);
        var current = inlet.CoordinateSystem.BasisZ;
        var direction = facing.Normalize();
        double angle = Math.Atan2(current.CrossProduct(direction).Z, current.DotProduct(direction));
        if (Math.Abs(angle) > PointTolerance)
            ElementTransformUtils.RotateElement(doc, box.Id, Line.CreateUnbound(center, XYZ.BasisZ), angle);
        double shift = center.Z - inlet.Origin.Z;
        if (Math.Abs(shift) > PointTolerance)
            ElementTransformUtils.MoveElement(doc, box.Id, XYZ.BasisZ * shift);
        doc.Regenerate();
        return box;
    }

    static Connector FacingPort(Element box, XYZ facing, double? diameter) => Ports(box)
        .Where(p => Math.Abs(p.CoordinateSystem.BasisZ.Z) < 1 - DirectionTolerance && MatchesDiameter(p, diameter))
        .OrderByDescending(p => p.CoordinateSystem.BasisZ.DotProduct(facing.Normalize())).ThenBy(p => p.Id).FirstOrDefault()
        ?? throw new InvalidOperationException("У распределительной коробки нет горизонтального вывода нужного диаметра.");

    static Conduit BuildSegment(Document doc, ConduitType type, Level level, XYZ start, XYZ end, double diameter)
    {
        if (start.DistanceTo(end) <= doc.Application.ShortCurveTolerance)
            throw new InvalidOperationException("Недостаточно места для участка короба.");
        var segment = Conduit.Create(doc, type.Id, start, end, level.Id);
        var parameter = segment.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
        if (Math.Abs(segment.Diameter - diameter) > PointTolerance && (parameter == null || !parameter.Set(diameter)))
            throw new InvalidOperationException("Выбранный тип короба не поддерживает диаметр вывода.");
        doc.Regenerate();
        if (Math.Abs(segment.Diameter - diameter) > PointTolerance)
            throw new InvalidOperationException("Revit округлил диаметр до другого типоразмера. Выберите совместимый тип короба.");
        return segment;
    }

    static Connector EndAt(Conduit segment, XYZ point) => Ports(segment).MinBy(p => p.Origin.DistanceTo(point))
        ?? throw new InvalidOperationException("Revit не создал коннектор участка короба.");

    static void Join(Connector a, Connector b)
    {
        if (a.IsConnectedTo(b)) return;
        if (a.IsConnected || b.IsConnected || a.Origin.DistanceTo(b.Origin) > PointTolerance
            || a.CoordinateSystem.BasisZ.DotProduct(b.CoordinateSystem.BasisZ) > -DirectionTolerance)
            throw new InvalidOperationException("Концы короба и вывода не совпадают или уже заняты.");
        a.ConnectTo(b);
        if (!a.IsConnectedTo(b)) throw new InvalidOperationException("Revit не соединил короб с выводом.");
    }

    static Connector[] Ports(Element element)
    {
        var manager = element switch
        {
            Conduit conduit => conduit.ConnectorManager,
            FamilyInstance instance => instance.MEPModel?.ConnectorManager,
            _ => null
        };
        return manager?.Connectors.Cast<Connector>().Where(p => p.Domain == Domain.DomainCableTrayConduit
            && p.ConnectorType == ConnectorType.End && p.Shape == ConnectorProfileType.Round).ToArray() ?? [];
    }

    static XYZ FixturePoint(FamilyInstance fixture) => (fixture.Location as LocationPoint)?.Point
        ?? throw new InvalidOperationException("У выбранного светильника нет точки установки.");

    static bool MatchesDiameter(Connector port, double? diameter) => diameter == null || Math.Abs(port.Radius * 2 - diameter.Value) <= PointTolerance;
    static XYZ Horizontal(XYZ point) => new(point.X, point.Y, 0);
    static double DirectionScore(Connector port, XYZ point) => port.CoordinateSystem.BasisZ.DotProduct(Horizontal(point - port.Origin).Normalize());
    static bool IsLight(Element element) => element is FamilyInstance && element.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures;

    sealed class RouteFilter(bool lightsOnly, HashSet<ElementId>? visited = null) : ISelectionFilter
    {
        public bool AllowElement(Element e) => (visited == null || !visited.Contains(e.Id)) &&
            (IsLight(e) || !lightsOnly && (e is Conduit || e is FamilyInstance && e.Category?.BuiltInCategory == BuiltInCategory.OST_ConduitFitting));
        public bool AllowReference(Reference reference, XYZ position) => true;
    }
}
