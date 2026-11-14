using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace BotPlus.Eom;

/// <summary>
/// Условные обозначения проводок на планах по ГОСТ 21.210-2014: «на лотке», «в коробе», «в трубе», «открыто», «под плинтусом» и т.д.
/// Обозначения не рисуются образцами линий (Revit не умеет ставить в образец значки), а ставятся линейным элементом узла
/// шаблона АДСК «ADSK_Обозначение проводок» вдоль оси лотка или короба. Типы семейства — способы прокладки.
/// Каждое обозначение помнит свою трассу (Extensible Storage) и следует за ней при изменении и удалении.
/// </summary>
internal static class WiringSymbols
{
    internal const string Family = "ADSK_Обозначение проводок";
    internal const string Auto = "Авто: лоток, короб, труба";
    internal const string None = "Не обозначать";
    /// <summary>Способы прокладки ГОСТ 21.210 — имена типов семейства АДСК.</summary>
    internal static readonly string[] Methods =
    [
        "На лотке", "В коробе", "В трубе", "В трубе открыто", "Открыто", "Открытая прокладка под перекрытием",
        "Под плинтусом", "Проводка в металлорукаве", "Линия на напряжение 36 В и ниже"
    ];
    internal static readonly BuiltInCategory[] HostCategories = [BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Conduit];
    const double MinLength = 50 / 304.8;

    internal static Dictionary<string, FamilySymbol> Types(Document doc)
    {
        var result = new Dictionary<string, FamilySymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().Where(s => s.FamilyName == Family))
            result.TryAdd(s.Name, s);
        return result;
    }

    /// <summary>Способ прокладки для трассы: явный выбор на ленте или по виду трассы.</summary>
    internal static string MethodFor(Element host, string choice)
    {
        if (choice != Auto) return choice;
        if (host is CableTray) return "На лотке";
        string type = host.Document.GetElement(host.GetTypeId())?.Name ?? "";
        return type.Contains("короб", StringComparison.OrdinalIgnoreCase) || type.Contains("канал", StringComparison.OrdinalIgnoreCase) ? "В коробе" : "В трубе";
    }

    internal static FamilySymbol? Resolve(Dictionary<string, FamilySymbol> types, string method) =>
        types.TryGetValue(method, out var exact) ? exact
        : types.Values.FirstOrDefault(s => s.Name.Contains(method, StringComparison.OrdinalIgnoreCase));

    /// <summary>Последняя ошибка размещения: показывается кнопкой «Обозначения проводок» и после построения трассы.</summary>
    internal static string? LastError;

    /// <summary>Ось трассы в плоскости плана. Вертикальные участки (стояки) не обозначаются.</summary>
    static Line? PlanLine(Element host, View view)
    {
        if (host.Location is not LocationCurve { Curve: Line line }) return null;
        double z = view is ViewPlan { GenLevel: { } level } ? level.ProjectElevation : view.Origin.Z;
        XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
        a = new XYZ(a.X, a.Y, z); b = new XYZ(b.X, b.Y, z);
        return a.DistanceTo(b) < MinLength ? null : Line.CreateBound(a, b);
    }

    /// <summary>Описание семейства для диагностики: категория и способ размещения.</summary>
    internal static string Describe(FamilySymbol symbol) => $"категория «{symbol.Category?.Name}», размещение {symbol.Family.FamilyPlacementType}";

    /// <summary>
    /// Параметр длины обозначения. В семействе АДСК (точечный элемент узла) линия со значками тянется вдоль оси X на заданную длину.
    /// Берём изменяемый параметр длины экземпляра, предпочитая «Длина»/«Length».
    /// </summary>
    internal static Parameter? LengthParameter(Element instance) =>
        instance.Parameters.Cast<Parameter>()
            .Where(p => !p.IsReadOnly && p.StorageType == StorageType.Double && p.Definition.GetDataType() == SpecTypeId.Length)
            .OrderBy(p => p.Definition.Name.Contains("Длина", StringComparison.OrdinalIgnoreCase) || p.Definition.Name.Contains("Length", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.Definition.Name.Length)
            .FirstOrDefault();

    /// <summary>Ставит обозначение вдоль трассы на плане. Требует транзакции. Возвращает поставленные экземпляры.</summary>
    internal static List<ElementId> Place(Document doc, View view, Element host, FamilySymbol symbol)
    {
        var result = new List<ElementId>();
        if (view is not ViewPlan || view.IsTemplate) return result;
        var line = PlanLine(host, view); if (line == null) return result;
        if (!symbol.IsActive) symbol.Activate();
        var type = symbol.Family.FamilyPlacementType;
        if (type == FamilyPlacementType.CurveBasedDetail)
            result.Add(doc.Create.NewFamilyInstance(line, symbol, view).Id);
        else if (type == FamilyPlacementType.ViewBased)
            result.AddRange(PlacePointBased(doc, view, line, symbol));
        else throw new InvalidOperationException($"Семейство «{Family}» нельзя поставить вдоль трассы: {Describe(symbol)}.");
        // Закрепляем: перетаскивание мышью двигает трассу, а не обозначение (выбор перенаправляется на трассу, см. RouteService).
        foreach (var id in result) { var e = doc.GetElement(id); EomStore.SaveWiringHost(e, host.Id); e.Pinned = true; }
        return result;
    }

    /// <summary>
    /// Точечный элемент узла АДСК — это «плитка» повторяющегося обозначения: отрезок проводки со значком (ромб, знак лотка, засечка).
    /// Отрезок в семействе может идти по любой оси (у АДСК — вдоль Y, под повторяющийся элемент узла с поворотом 90°),
    /// поэтому направление и длину плитки берём из её геометрии: самый длинный отрезок. Плитку поворачиваем так, чтобы этот отрезок
    /// лёг на ось трассы, и повторяем встык. Остаток делится поровну по концам — обозначение не выходит за участок.
    /// Если в семействе есть параметр длины, он задаётся по длине участка и ставится один экземпляр.
    /// </summary>
    static List<ElementId> PlacePointBased(Document doc, View view, Line line, FamilySymbol symbol)
    {
        XYZ start = line.GetEndPoint(0), dir = line.Direction;
        double length = line.Length;
        var instance = doc.Create.NewFamilyInstance(start, symbol, view);
        var parameter = LengthParameter(instance);
        parameter?.Set(length);
        doc.Regenerate();
        var tile = LongestLine(instance, view);
        if (tile == null) { doc.Delete(instance.Id); throw new InvalidOperationException($"У семейства «{Family}» ({symbol.Name}) не найдено линий в геометрии на виде «{view.Name}»."); }
        XYZ a = Flat(tile.GetEndPoint(0), start.Z), b = Flat(tile.GetEndPoint(1), start.Z);
        double tileLength = a.DistanceTo(b);
        int count = parameter != null ? 1 : (int)Math.Floor((length + 1e-6) / tileLength);
        if (tileLength < 1e-6 || count < 1) { doc.Delete(instance.Id); return []; }
        count = Math.Min(count, 300);
        // Поворот вокруг точки вставки: направление плитки a→b совмещаем с направлением трассы.
        var u = (b - a).Normalize();
        double angle = Math.Atan2(dir.Y, dir.X) - Math.Atan2(u.Y, u.X);
        var axis = Line.CreateBound(start, start + XYZ.BasisZ);
        if (Math.Abs(angle) > 1e-9) ElementTransformUtils.RotateElement(doc, instance.Id, axis, angle);
        var rotation = Transform.CreateRotationAtPoint(XYZ.BasisZ, angle, start);
        double margin = parameter != null ? 0 : (length - count * tileLength) / 2;
        var move = start + dir * margin - rotation.OfPoint(a);
        if (move.GetLength() > 1e-9) ElementTransformUtils.MoveElement(doc, instance.Id, move);
        var ids = new List<ElementId> { instance.Id };
        for (int i = 1; i < count; i++) ids.AddRange(ElementTransformUtils.CopyElement(doc, instance.Id, dir * (tileLength * i)));
        return ids;
    }

    static XYZ Flat(XYZ p, double z) => new(p.X, p.Y, z);

    static Line? LongestLine(Element instance, View view)
    {
        Line? best = null;
        void Walk(GeometryElement? geometry)
        {
            if (geometry == null) return;
            foreach (var g in geometry)
            {
                if (g is GeometryInstance gi) Walk(gi.GetInstanceGeometry());
                else if (g is Line l && (best == null || l.Length > best.Length)) best = l;
            }
        }
        Walk(instance.get_Geometry(new Options { View = view }));
        if (best != null || instance.get_BoundingBox(view) is not { } box) return best;
        // Только дуги (металлорукав): ось плитки — по большей стороне габарита.
        double cx = (box.Min.X + box.Max.X) / 2, cy = (box.Min.Y + box.Max.Y) / 2;
        return box.Max.X - box.Min.X >= box.Max.Y - box.Min.Y
            ? (box.Max.X - box.Min.X > 1e-6 ? Line.CreateBound(new XYZ(box.Min.X, cy, box.Min.Z), new XYZ(box.Max.X, cy, box.Min.Z)) : null)
            : Line.CreateBound(new XYZ(cx, box.Min.Y, box.Min.Z), new XYZ(cx, box.Max.Y, box.Min.Z));
    }

    static IEnumerable<FamilyInstance> Placed(Document doc, View? view) =>
        (view == null ? new FilteredElementCollector(doc) : new FilteredElementCollector(doc, view.Id))
            .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(i => i.Symbol?.FamilyName == Family);

    /// <summary>Обозначения следуют за трассой: при изменении ставятся заново тем же типом, при удалении трассы удаляются.</summary>
    internal static void Follow(Document doc, ICollection<ElementId> modified, ICollection<ElementId> deleted)
    {
        var changed = modified.Select(i => i.Value).ToHashSet(); var gone = deleted.Select(i => i.Value).ToHashSet();
        if (changed.Count == 0 && gone.Count == 0) return;
        var remove = new List<ElementId>(); var redo = new Dictionary<(long Host, ElementId View), FamilySymbol>();
        foreach (var instance in Placed(doc, null))
        {
            if (EomStore.WiringHost(instance) is not { } host) continue;
            if (gone.Contains(host)) { remove.Add(instance.Id); continue; }
            if (!changed.Contains(host)) continue;
            remove.Add(instance.Id);
            redo[(host, instance.OwnerViewId)] = instance.Symbol;
        }
        if (remove.Count > 0) doc.Delete(remove);
        foreach (var ((host, viewId), symbol) in redo)
            if (doc.GetElement(new ElementId(host)) is { } element && doc.GetElement(viewId) is View view)
                Place(doc, view, element, symbol);
    }

    internal sealed record Report(int Added, int Updated, int Skipped, IReadOnlyCollection<string> MissingTypes, IReadOnlyCollection<string> Errors);

    /// <summary>Обозначает все лотки и короба активного плана; существующие обозначения ставятся заново. Требует транзакции.</summary>
    internal static Report Mark(Document doc, View view, string choice)
    {
        var types = Types(doc);
        var existing = Placed(doc, view).Where(i => EomStore.WiringHost(i) != null).GroupBy(i => EomStore.WiringHost(i)!.Value).ToDictionary(g => g.Key, g => g.Select(i => i.Id).ToList());
        var hosts = new FilteredElementCollector(doc, view.Id).WherePasses(new ElementMulticategoryFilter(HostCategories)).WhereElementIsNotElementType().ToList();
        int added = 0, updated = 0, skipped = 0; var missing = new SortedSet<string>(); var errors = new SortedSet<string>();
        foreach (var host in hosts)
        {
            string method = MethodFor(host, choice);
            var symbol = Resolve(types, method);
            if (symbol == null) { missing.Add(method); skipped++; continue; }
            bool had = existing.TryGetValue(host.Id.Value, out var old);
            if (had) doc.Delete(old!);
            if (PlanLine(host, view) == null) { skipped++; continue; }
            try { if (Place(doc, view, host, symbol).Count > 0) { if (had) updated++; else added++; } else skipped++; }
            catch (Exception error) { skipped++; errors.Add(error.Message); }
        }
        return new(added, updated, skipped, missing, errors);
    }

    sealed class UseDestination : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
    }

    /// <summary>
    /// Если в проекте нет семейства обозначений, копирует его из другого открытого файла (шаблона или проекта АДСК). Требует транзакции.
    /// Возвращает пустую строку, если семейство уже есть, иначе сообщение для пользователя.
    /// </summary>
    internal static (bool Ready, string Message) Ensure(Document doc, Autodesk.Revit.ApplicationServices.Application app)
    {
        if (Types(doc).Count > 0) return (true, "");
        foreach (Document other in app.Documents)
        {
            if (other.Equals(doc) || other.IsFamilyDocument) continue;
            var source = Types(other); if (source.Count == 0) continue;
            var options = new CopyPasteOptions(); options.SetDuplicateTypeNamesHandler(new UseDestination());
            ElementTransformUtils.CopyElements(other, source.Values.Select(s => s.Id).ToList(), doc, Transform.Identity, options);
            return (true, $"Семейство «{Family}» ({source.Count} типов) скопировано из «{other.Title}».");
        }
        return (false, $"В проекте нет семейства «{Family}» (условные обозначения проводок ГОСТ 21.210).\n\n" +
            "Откройте рядом файл по шаблону АДСК ЭОМ (например, проект или шаблон ADSK_RU_ШаблонПроекта_ЭОМ) и повторите — семейство будет скопировано автоматически. " +
            "Либо загрузите семейство вручную: «Вставка → Загрузить семейство».");
    }

    /// <summary>Удаляет образцы линий и фильтры, которые создавали версии 1.9.13–1.9.15 (не по ГОСТ 21.210). Требует транзакции.</summary>
    internal static int RemoveOldGraphics(Document doc)
    {
        string[] patterns = ["ГОСТ Штриховая", "ГОСТ Штрихпунктирная", "ГОСТ Штрихпунктирная 2 точки"];
        string[] filters = ["ЭОМ — Рабочее освещение", "ЭОМ — Аварийное освещение", "ЭОМ — Силовая сеть", "ЭОМ — Слаботочные системы"];
        var ids = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Where(f => filters.Contains(f.Name)).Select(f => f.Id).ToList();
        foreach (var name in patterns) if (LinePatternElement.GetLinePatternElementByName(doc, name) is { } p) ids.Add(p.Id);
        if (ids.Count > 0) doc.Delete(ids);
        return ids.Count;
    }
}
