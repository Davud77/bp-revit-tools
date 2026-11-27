using System.Globalization;
using Autodesk.Revit.DB;

namespace BotPlus.RT;

internal sealed record NumberingParameter(long Id, string Name, StorageType StorageType, bool Ambiguous = false)
{
    public string Label => Name + (StorageType == StorageType.Integer ? " (целое число)" : " (текст)") + (Ambiguous ? $" · ID {Id}" : "");
    public override string ToString() => Label;
}
internal sealed record NumberingSortParameter(long Id, string Name, bool Ambiguous = false)
{
    public string Label => Ambiguous ? Name + $" · поле {Id}" : Name;
    public override string ToString() => Label;
}
internal sealed record NumberingResult(int Updated, int Rows, int Skipped, IReadOnlyList<string> Errors);

internal static class ScheduleNumbering
{
    internal static void Validate(Document doc, ViewSchedule schedule)
    {
        var d = schedule.Definition;
        if (doc.IsReadOnly || doc.IsFamilyDocument || doc.IsModifiable)
            throw new InvalidOperationException("Нужен доступный для записи проект без открытой транзакции.");
        if (schedule.IsTemplate || schedule.IsTitleblockRevisionSchedule || d.IsKeySchedule || d.IsMaterialTakeoff ||
            d.HasEmbeddedSchedule || d.IncludeLinkedFiles || d.IsFilteredBySheet)
            throw new InvalidOperationException("Нумерация поддерживает обычные спецификации проекта без связанных файлов, вложенных таблиц, отбора по листу, ключей и подсчёта материалов.");
    }

    internal static List<Element> Elements(Document doc, ViewSchedule schedule) =>
        new FilteredElementCollector(doc, schedule.Id).WhereElementIsNotElementType().ToElements().ToList();

    internal static Parameter? ParameterOf(Element element, long id) =>
        element.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Id.Value == id);

    /// <summary>
    /// Fast parameter access by id: built-in parameters by enum, shared by GUID, project parameters by definition.
    /// Scanning element.Parameters for every element and field made large schedules very slow.
    /// </summary>
    internal static Func<Element, Parameter?> Finder(Document doc, long id)
    {
        if (id < 0) { var bip = (BuiltInParameter)id; return e => e.get_Parameter(bip); }
        var owner = doc.GetElement(new ElementId(id));
        if (owner is SharedParameterElement shared)
        {
            var guid = shared.GuidValue;
            return e => e.get_Parameter(guid) is { } p && p.Id.Value == id ? p : null;
        }
        if (owner is ParameterElement project)
        {
            var definition = project.GetDefinition();
            return e => ExactParameter(e, e.get_Parameter(definition), id);
        }
        return e => ParameterOf(e, id);
    }

    static Parameter? ExactParameter(Element element, Parameter? parameter, long id) =>
        parameter?.Id.Value == id ? parameter : ParameterOf(element, id);

    internal static IReadOnlyList<NumberingParameter> Parameters(Document doc, ViewSchedule schedule, IReadOnlyList<Element> elements)
    {
        var samples = elements.DistinctBy(e => (e.GetTypeId().Value, e.Category?.Id.Value)).ToArray();
        var parameters = new List<NumberingParameter>();
        foreach (var fieldId in schedule.Definition.GetFieldOrder())
        {
            var field = schedule.Definition.GetField(fieldId);
            if (field.FieldType != ScheduleFieldType.Instance || field.ParameterId == ElementId.InvalidElementId) continue;
            if (UnitUtils.IsMeasurableSpec(field.GetSpecTypeId())) continue;
            var find = Finder(doc, field.ParameterId.Value);
            var parameter = samples.Select(find).FirstOrDefault(p => p is { IsReadOnly: false, StorageType: StorageType.String or StorageType.Integer })
                ?? elements.Select(find).FirstOrDefault(p => p is { IsReadOnly: false, StorageType: StorageType.String or StorageType.Integer });
            if (parameter != null)
                parameters.Add(new(parameter.Id.Value, field.GetName(), parameter.StorageType));
        }
        var ambiguous = parameters.GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return parameters.Select(p => p with { Ambiguous = ambiguous.Contains(p.Name) }).ToArray();
    }

    internal static IReadOnlyList<NumberingSortParameter> SortParameters(ViewSchedule schedule)
    {
        var fields = schedule.Definition.GetFieldOrder()
            .Select(id => new NumberingSortParameter(id.IntegerValue, schedule.Definition.GetField(id).GetName())).ToArray();
        var ambiguous = fields.GroupBy(f => f.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return fields.Select(f => f with { Ambiguous = ambiguous.Contains(f.Name) }).ToArray();
    }

    internal static void Clear(Document doc, IReadOnlyList<Element> elements, NumberingParameter chosen, HashSet<long> selected)
    {
        var find = Finder(doc, chosen.Id);
        var targets = elements.Where(e => selected.Contains(e.Id.Value)).Select(e => (Element: e, Parameter: find(e))).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("Выделите строки спецификации для очистки.");
        if (targets.Any(t => t.Parameter == null || t.Parameter.IsReadOnly || t.Parameter.StorageType != chosen.StorageType))
            throw new InvalidOperationException("Выбранный параметр отсутствует или недоступен для записи у части выделенных элементов. Очистка отменена.");
        using var group = new TransactionGroup(doc, "Очистка ячеек спецификации");
        group.Start();
        using var transaction = new Transaction(doc, "Очистка выбранного параметра");
        transaction.Start();
        foreach (var (_, p) in targets)
        {
            if (p!.StorageType == StorageType.String)
            {
                if (!string.IsNullOrEmpty(p.AsString()) && !p.Set("")) throw new InvalidOperationException("Revit не принял пустое значение.");
            }
            else if (p.HasValue)
            {
                try
                {
                    if (!p.ClearValue()) throw new InvalidOperationException("Revit не очистил числовой параметр.");
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException("Этот целочисленный параметр Revit не допускает пустым. Выберите текстовый параметр. Очистка отменена.", error);
                }
            }
        }
        Commit(transaction);
        foreach (var (element, _) in targets)
        {
            var p = find(element)!;
            if (p.StorageType == StorageType.String ? !string.IsNullOrEmpty(p.AsString()) : p.HasValue)
                throw new InvalidOperationException("После фиксации значение осталось непустым. Очистка отменена.");
        }
        if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Не подтверждена очистка ячеек.");
    }

    internal static IReadOnlyList<IReadOnlyList<long>> ReadRows(Document doc, ViewSchedule original,
        IReadOnlyList<Element> elements, NumberingParameter? preferredMarker = null,
        NumberingSortParameter? sortParameter = null, HashSet<long>? selected = null)
    {
        var targets = selected == null ? elements : elements.Where(e => selected.Contains(e.Id.Value)).ToArray();
        if (targets.Count == 0) return [];
        if (targets.Count == 1 || !original.Definition.IsItemized && original.Definition.GetSortGroupFieldCount() == 0)
            return [targets.Select(e => e.Id.Value).ToArray()];

        int sortIndex = sortParameter == null ? -1 : original.Definition.GetFieldOrder().ToList()
            .FindIndex(id => id.IntegerValue == sortParameter.Id);
        if (sortParameter != null && sortIndex < 0)
            throw new InvalidOperationException("Выбранное поле сортировки отсутствует в спецификации.");

        if (targets.Count == elements.Count)
            return ReadRowsCore(doc, original, targets, preferredMarker, sortIndex, false);

        try { return ReadRowsCore(doc, original, targets, preferredMarker, sortIndex, true); }
        catch (SelectionFilterException) { }
        // Fall back to the original schedule filters if the marker cannot be used as a filter.
        return ReadRowsCore(doc, original, elements, preferredMarker, sortIndex, false)
            .Select(row => (IReadOnlyList<long>)row.Where(selected!.Contains).ToArray())
            .Where(row => row.Count > 0).ToArray();
    }

    static IReadOnlyList<IReadOnlyList<long>> ReadRowsCore(Document doc, ViewSchedule original,
        IReadOnlyList<Element> elements, NumberingParameter? preferredMarker, int sortIndex, bool scoped)
    {
        var definition = original.Definition;
        var relevant = definition.GetSortGroupFields().Select(f => f.FieldId)
            .Concat(definition.GetFilters().Select(f => f.FieldId)).Select(definition.GetField).ToList();
        if (sortIndex >= 0) relevant.Add(definition.GetField(definition.GetFieldOrder()[sortIndex]));
        if (preferredMarker != null && relevant.All(f => !f.IsCalculatedField && !f.IsCombinedParameterField) &&
            elements.All(e => e.GroupId == ElementId.InvalidElementId))
        {
            var inUse = relevant.Select(f => f.ParameterId.Value).ToHashSet();
            var available = definition.GetFieldOrder().Select(definition.GetField)
                .Where(f => f.FieldType == ScheduleFieldType.Instance).Select(f => f.ParameterId.Value);
            if (preferredMarker.StorageType == StorageType.String && !inUse.Contains(preferredMarker.Id) &&
                definition.GetSchedulableFields().Any(f => f.FieldType == ScheduleFieldType.Instance && f.ParameterId.Value == preferredMarker.Id))
                available = available.Prepend(preferredMarker.Id);

            foreach (long markerId in available.Distinct().Where(id => !inUse.Contains(id)))
            {
                var find = Finder(doc, markerId);
                var parameters = new Dictionary<long, Parameter>();
                foreach (var element in elements)
                {
                    var p = find(element);
                    if (p is not { StorageType: StorageType.String, IsReadOnly: false }) break;
                    parameters.Add(element.Id.Value, p);
                }
                if (parameters.Count != elements.Count) continue;
                try { return ProbeRows(doc, original, elements, markerId, parameters, sortIndex, scoped); }
                catch (MarkerProbeException)
                {
                    // A rejected marker leaves no changes after rollback. Use a dedicated field.
                    break;
                }
                catch (SelectionFilterException) { break; }
            }
        }
        return ReadRowsWithSharedParameter(doc, original, elements, sortIndex, scoped);
    }

    sealed class MarkerProbeException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

    sealed class SelectionFilterException : InvalidOperationException;

    static IReadOnlyList<IReadOnlyList<long>> ReadRowsWithSharedParameter(Document doc, ViewSchedule original,
        IReadOnlyList<Element> elements, int sortIndex, bool scoped)
    {
        string previous = doc.Application.SharedParametersFilename;
        string file = Path.Combine(Path.GetTempPath(), "BotPlus-Schedule-" + Guid.NewGuid().ToString("N") + ".txt");
        Guid guid = Guid.NewGuid();
        File.WriteAllText(file, "# Revit shared parameter file\n*META\tVERSION\tMINVERSION\nMETA\t2\t1\n");
        try
        {
            doc.Application.SharedParametersFilename = file;
            var definitionFile = doc.Application.OpenSharedParameterFile() ?? throw new InvalidOperationException("Не удалось открыть временный файл параметров.");
            var external = definitionFile.Groups.Create("BotPlus probe").Definitions.Create(
                new ExternalDefinitionCreationOptions("BP_Row_" + guid.ToString("N"), SpecTypeId.String.Text) { GUID = guid, Visible = false });
            return ProbeRows(doc, original, elements, 0, null, sortIndex, scoped, external, guid);
        }
        finally
        {
            doc.Application.SharedParametersFilename = previous;
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    // All changes for reading native rows are in a single rolled-back transaction.
    static IReadOnlyList<IReadOnlyList<long>> ProbeRows(Document doc, ViewSchedule original, IReadOnlyList<Element> elements,
        long markerId, Dictionary<long, Parameter>? parameters, int sortIndex, bool scoped, Definition? external = null, Guid guid = default)
    {
        using var probe = new Transaction(doc, "BotPlus: чтение строк спецификации");
        if (probe.Start() != TransactionStatus.Started) throw new InvalidOperationException("Нельзя начать чтение строк.");
        try
        {
            if (parameters == null)
            {
                var categories = doc.Application.Create.NewCategorySet();
                foreach (var category in elements.Select(e => e.Category).Where(c => c != null).DistinctBy(c => c.Id.Value))
                    if (category.AllowsBoundParameters) categories.Insert(category);
                if (categories.Size == 0 || !doc.ParameterBindings.Insert(external!, doc.Application.Create.NewInstanceBinding(categories), GroupTypeId.Data))
                    throw new InvalidOperationException("Нельзя временно сопоставить строки этой категории с элементами.");
                doc.Regenerate();
                var parameter = SharedParameterElement.Lookup(doc, guid) ?? throw new InvalidOperationException("Временный параметр отсутствует.");
                markerId = parameter.Id.Value;
                if (elements.Any(e => e.GroupId != ElementId.InvalidElementId))
                    ((InternalDefinition)parameter.GetDefinition()).SetAllowVaryBetweenGroups(doc, true);
                parameters = new Dictionary<long, Parameter>(elements.Count);
                foreach (var element in elements)
                {
                    var p = element.get_Parameter(guid);
                    if (p == null || p.IsReadOnly)
                        throw new MarkerProbeException($"Нельзя определить строку элемента {element.Id.Value}.");
                    parameters.Add(element.Id.Value, p);
                }
            }

            void SetMarker(long id, string value)
            {
                try
                {
                    var parameter = parameters[id];
                    if (parameter.AsString() == value || parameter.Set(value)) return;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException error)
                {
                    throw new MarkerProbeException($"Не удалось пометить строку элемента {id}.", error);
                }
                throw new MarkerProbeException($"Не удалось пометить строку элемента {id}.");
            }

            var prefix = scoped ? "BP_" + Guid.NewGuid().ToString("N") + "_" : "";
            foreach (var id in parameters.Keys)
                SetMarker(id, prefix + id.ToString(CultureInfo.InvariantCulture));
            return ReadMarkedRows(doc, original, elements, markerId, SetMarker, sortIndex, prefix);
        }
        finally
        {
            if (probe.GetStatus() == TransactionStatus.Started && probe.RollBack() != TransactionStatus.RolledBack)
                throw new InvalidOperationException("Не подтверждён откат временной таблицы.");
        }
    }

    static IReadOnlyList<IReadOnlyList<long>> ReadMarkedRows(Document doc, ViewSchedule original, IReadOnlyList<Element> elements,
        long markerId, Action<long, string> setMarker, int sortIndex, string prefix)
    {
        var clone = (ViewSchedule)doc.GetElement(original.Duplicate(ViewDuplicateOption.Duplicate));
        clone.ViewTemplateId = ElementId.InvalidElementId;
        var definition = clone.Definition;
        bool itemized = original.Definition.IsItemized;
        definition.ShowTitle = false;
        definition.ShowHeaders = false;
        definition.ShowGrandTotal = false;
        var sort = definition.GetSortGroupFields();
        foreach (var field in sort) { field.ShowHeader = false; field.ShowFooter = false; field.ShowBlankLine = false; }
        definition.SetSortGroupFields(sort);

        // Hidden sort fields still determine native row order. Render only the probe columns.
        var fields = definition.GetFieldOrder().Select(definition.GetField).ToArray();
        foreach (var field in fields) field.IsHidden = true;
        var marker = fields.FirstOrDefault(f => f.FieldType == ScheduleFieldType.Instance && f.ParameterId.Value == markerId)
            ?? definition.AddField(ScheduleFieldType.Instance, new ElementId(markerId));
        // Revit 2026 can reject setting DisplayType even when a text field already uses Standard.
        if (marker.DisplayType != ScheduleFieldDisplayType.Standard)
            throw new MarkerProbeException("Для служебной метки требуется текстовое поле без расчёта итогов.");
        marker.IsHidden = false;
        var sortField = sortIndex < 0 ? null : fields[sortIndex];
        if (sortField != null) sortField.IsHidden = false;
        if (prefix.Length > 0)
        {
            if (!definition.CanFilterBySubstring(marker.FieldId)) throw new SelectionFilterException();
            // Targets already belong to the original schedule. A unique marker
            // filters the copy to exactly those targets even if all filter slots were used.
            definition.ClearFilters();
            try { definition.AddFilter(new ScheduleFilter(marker.FieldId, ScheduleFilterType.BeginsWith, prefix)); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { throw new SelectionFilterException(); }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { throw new SelectionFilterException(); }
        }

        ScheduleField? count = null;
        if (!itemized)
        {
            marker.MultipleValuesDisplayType = ScheduleFieldMultipleValuesDisplayType.Custom;
            marker.MultipleValuesCustomText = "BP_MULTIPLE";
            count = fields.FirstOrDefault(f => f.FieldType == ScheduleFieldType.Count)
                ?? definition.AddField(ScheduleFieldType.Count);
            count.IsHidden = false;
        }
        var expected = elements.Select(e => e.Id.Value).ToHashSet();
        int Column(ScheduleField field) => definition.GetFieldOrder()
            .Where(id => !definition.GetField(id).IsHidden).ToList().IndexOf(field.FieldId);
        long ReadId(string text) => text.StartsWith(prefix, StringComparison.Ordinal) &&
            long.TryParse(text.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long id) ? id : 0;
        bool Complete(long[] ids) => ids.Length == expected.Count && expected.SetEquals(ids);
        IReadOnlyList<IReadOnlyList<long>> Finish(IReadOnlyList<IReadOnlyList<long>> rows)
        {
            if (sortField == null || rows.Count < 2) return rows;
            var values = ReadColumn(clone, Column(sortField));
            if (values.Length != rows.Count)
                throw new MarkerProbeException("Не удалось прочитать значения выбранного поля сортировки.");
            var spec = sortField.GetSpecTypeId();
            var units = doc.GetUnits();
            bool countField = sortField.FieldType == ScheduleFieldType.Count;
            return ScheduleNumberingPolicy.Sort(rows, values.Select(text => SortValue(text, spec, units, countField)).ToArray());
        }

        Refresh(doc, clone);
        var nativeIds = ReadColumn(clone, Column(marker)).Select(ReadId).ToArray();
        if (Complete(nativeIds))
            return Finish(nativeIds.Select(id => (IReadOnlyList<long>)new[] { id }).ToArray());
        if (itemized)
            throw new MarkerProbeException("Не все строки удалось однозначно сопоставить с элементами спецификации.");
        var counts = ReadColumn(clone, Column(count!)).Select(ParseCount).ToArray();
        if (counts.Length == 1 && counts[0] == expected.Count)
            return [elements.Select(e => e.Id.Value).ToArray()];
        definition.IsItemized = true;
        if (!clone.RefreshData()) Refresh(doc, clone);
        var ordered = ReadColumn(clone, Column(marker)).Select(ReadId).ToArray();
        if (!Complete(ordered))
        {
            Refresh(doc, clone);
            ordered = ReadColumn(clone, Column(marker)).Select(ReadId).ToArray();
        }
        if (!Complete(ordered))
            throw new MarkerProbeException("Не все строки удалось однозначно сопоставить с элементами спецификации.");
        var rows = ScheduleNumberingPolicy.SplitRows(ordered, counts);
        if (rows != null && ConfirmGroups(rows, definition, doc, clone, marker, setMarker, prefix)) return Finish(rows);

        // Count may be unavailable with some field formats. Compare native sort text
        // in that case, and still require a unanimous unique marker for every row.
        foreach (var id in ordered) setMarker(id, prefix + id.ToString(CultureInfo.InvariantCulture));
        definition.IsItemized = true;
        var sortIds = sort.Select(f => f.FieldId).ToHashSet();
        foreach (var id in sortIds) definition.GetField(id).IsHidden = false;
        Refresh(doc, clone);
        var columns = definition.GetFieldOrder().Where(id => !definition.GetField(id).IsHidden).ToList();
        var memberIds = ReadColumn(clone, columns.IndexOf(marker.FieldId));
        var keys = sort.Select(f => ReadColumn(clone, columns.IndexOf(f.FieldId))).ToArray();
        var members = memberIds.Select(ReadId).ToArray();
        if (Complete(members))
        {
            var sizes = new List<int>();
            int first = 0;
            for (int i = 1; i < members.Length; i++)
            {
                if (keys.All(column => column[i] == column[first])) continue;
                sizes.Add(i - first);
                first = i;
            }
            sizes.Add(members.Length - first);
            rows = ScheduleNumberingPolicy.SplitRows(members, sizes);
            if (rows != null && ConfirmGroups(rows, definition, doc, clone, marker, setMarker, prefix)) return Finish(rows);
        }
        throw new MarkerProbeException("Не удалось подтвердить состав сгруппированных строк. Проверьте поля сортировки спецификации.");
    }

    static bool ConfirmGroups(IReadOnlyList<IReadOnlyList<long>> rows, ScheduleDefinition definition, Document doc,
        ViewSchedule clone, ScheduleField marker, Action<long, string> setMarker, string prefix)
    {
        // Singletons can keep their element marker while multi-element rows need a common label.
        var labels = rows.Select((row, i) => prefix + (row.Count == 1
            ? row[0].ToString(CultureInfo.InvariantCulture) : "ROW_" + i.ToString(CultureInfo.InvariantCulture))).ToArray();
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Count > 1)
                foreach (var id in rows[i]) setMarker(id, labels[i]);

        definition.IsItemized = false;
        Refresh(doc, clone);
        var columns = definition.GetFieldOrder().Where(id => !definition.GetField(id).IsHidden).ToList();
        return ReadColumn(clone, columns.IndexOf(marker.FieldId)).SequenceEqual(labels);
    }

    static NumberingSortValue SortValue(string text, ForgeTypeId spec, Units units, bool count)
    {
        if (text.Length == 0) return new(0, 0, "");
        if (UnitUtils.IsMeasurableSpec(spec) && UnitFormatUtils.TryParse(units, spec, text, out double value))
            return new(1, value, "");
        if ((count || spec == SpecTypeId.Int.Integer || spec == SpecTypeId.Number) &&
            double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value))
            return new(1, value, "");
        return new(2, 0, text);
    }

    static int ParseCount(string text)
    {
        string number = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        return int.TryParse(number, NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out int count) ? count : 0;
    }

    static void Refresh(Document doc, ViewSchedule schedule)
    {
        doc.Regenerate();
        if (!schedule.RefreshData()) throw new InvalidOperationException("Не удалось обновить временную таблицу.");
    }

    static string[] ReadColumn(ViewSchedule schedule, int column)
    {
        using var data = schedule.GetTableData();
        using var body = data.GetSectionData(SectionType.Body);
        var values = new string[body.NumberOfRows];
        int firstRow = body.FirstRowNumber;
        int index = body.FirstColumnNumber + column;
        for (int i = 0; i < values.Length; i++)
            values[i] = schedule.GetCellText(SectionType.Body, firstRow + i, index).Trim();
        return values;
    }

    internal static NumberingResult Apply(Document doc, IReadOnlyList<IReadOnlyList<long>> rows, NumberingParameter chosen, int start, HashSet<long>? selected)
    {
        var find = Finder(doc, chosen.Id);
        var parameters = new Dictionary<long, Parameter>();
        var repeatedGroups = new Dictionary<ElementId, bool>();
        var errors = new List<string>();
        var input = rows.Select(ids => new NumberingRow(ids.Select(id =>
        {
            if (selected != null && !selected.Contains(id)) return new NumberingTarget(id, false, false);
            var element = doc.GetElement(new ElementId(id));
            var p = element == null ? null : find(element);
            if (p == null || p.IsReadOnly || p.StorageType != chosen.StorageType) return new NumberingTarget(id, false, true);
            if (element!.GroupId != ElementId.InvalidElementId && p.Definition is InternalDefinition { VariesAcrossGroups: false })
            {
                var groupId = element.GroupId;
                if (!repeatedGroups.TryGetValue(groupId, out bool repeated))
                {
                    repeated = doc.GetElement(groupId) is Group group && group.GroupType.Groups.Size > 1;
                    repeatedGroups.Add(groupId, repeated);
                }
                if (repeated)
                {
                    if (errors.Count < 10) errors.Add($"У элемента {id} значение общее для экземпляров группы модели.");
                    return new NumberingTarget(id, false, true);
                }
            }
            parameters.TryAdd(id, p);
            return new NumberingTarget(id, true, true);
        }).ToArray())).ToArray();
        var plan = ScheduleNumberingPolicy.Build(input, start, selected != null);
        int updated = 0, writtenRows = 0, skipped = plan.Skipped;
        if (plan.Groups.Count == 0) return new NumberingResult(0, 0, skipped, errors);
        using var groupTransaction = new TransactionGroup(doc, "Нумерация спецификации");
        if (groupTransaction.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Revit не начал нумерацию.");
        var expected = new Dictionary<long, int>();
        using var transaction = new Transaction(doc, "Запись номеров");
        if (transaction.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Revit не начал запись номеров.");
        bool changed = false;
        foreach (var group in plan.Groups)
        {
            int number = checked(start + writtenRows); bool written = false;
            string text = number.ToString(CultureInfo.InvariantCulture);
            foreach (long id in group.ElementIds)
            {
                try
                {
                    var p = parameters[id];
                    // Unchanged values are not rewritten: fewer modified elements, faster commit.
                    bool same = chosen.StorageType == StorageType.Integer ? p.HasValue && p.AsInteger() == number : p.AsString() == text;
                    if (!same)
                    {
                        if (!(chosen.StorageType == StorageType.Integer ? p.Set(number) : p.Set(text)))
                            throw new InvalidOperationException("Revit не принял значение.");
                        changed = true;
                    }
                    updated++; written = true; expected[id] = number;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException error)
                {
                    skipped++; if (errors.Count < 10) errors.Add(id + ": " + error.Message);
                }
                catch (InvalidOperationException error)
                {
                    skipped++; if (errors.Count < 10) errors.Add(id + ": " + error.Message);
                }
            }
            if (written) writtenRows++;
        }
        if (!changed)
        {
            transaction.RollBack();
            groupTransaction.RollBack();
            return new NumberingResult(updated, writtenRows, skipped, errors);
        }
        Commit(transaction);
        foreach (var (id, number) in expected)
        {
            var element = doc.GetElement(new ElementId(id));
            var p = element == null ? null : find(element);
            string? actual = p is { HasValue: true } ? p.StorageType switch
            {
                StorageType.Integer => p.AsInteger().ToString(CultureInfo.InvariantCulture),
                StorageType.String => p.AsString(),
                _ => null
            } : null;
            string planned = number.ToString(CultureInfo.InvariantCulture);
            if (actual != planned)
                throw new InvalidOperationException(
                    $"После записи у элемента {id} в параметре «{chosen.Name}» получено " +
                    $"{(string.IsNullOrEmpty(actual) ? "пустое значение" : $"«{actual}»")} вместо «{planned}». " +
                    "Нумерация отменена. Проверьте, не управляет ли этим параметром правило автозаполнения или другая надстройка.");
        }
        if (groupTransaction.Assimilate() != TransactionStatus.Committed)
            throw new InvalidOperationException("Не подтверждена итоговая нумерация.");
        return new NumberingResult(updated, writtenRows, skipped, errors);
    }

    static void Commit(Transaction t)
    {
        if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit не подтвердил транзакцию.");
    }
}
