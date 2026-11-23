using System.Globalization;
using Autodesk.Revit.DB;

namespace BotPlus.RT;

internal sealed record NumberingParameter(long Id, string Name, StorageType StorageType, bool Ambiguous = false)
{
    public string Label => Name + (StorageType == StorageType.Integer ? " (целое число)" : " (текст)") + (Ambiguous ? $" · ID {Id}" : "");
    public override string ToString() => Label;
}
internal sealed record NumberingSortParameter(long Id, string Name)
{
    public string Label => Id == 0 ? Name : Name + $" · ID {Id}";
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
        if (owner is SharedParameterElement shared) { var guid = shared.GuidValue; return e => e.get_Parameter(guid); }
        if (owner is ParameterElement project) { var definition = project.GetDefinition(); return e => e.get_Parameter(definition); }
        return e => ParameterOf(e, id);
    }

    internal static IReadOnlyList<NumberingParameter> Parameters(IEnumerable<Element> elements)
    {
        // One sample per type and category: instances of a type carry the same parameter set.
        var parameters = elements.GroupBy(e => (e.GetTypeId().Value, e.Category?.Id.Value)).Select(g => g.First()).SelectMany(e => e.Parameters.Cast<Parameter>())
            .Where(p => p.StorageType is StorageType.String or StorageType.Integer)
            .Select(p => new NumberingParameter(p.Id.Value, p.Definition.Name, p.StorageType))
            .DistinctBy(p => p.Id).OrderBy(p => p.Name, StringComparer.CurrentCulture).ToArray();
        var ambiguous = parameters.GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return parameters.Select(p => p with { Ambiguous = ambiguous.Contains(p.Name) }).ToArray();
    }

    internal static IReadOnlyList<NumberingSortParameter> SortParameters(Document doc, IReadOnlyList<Element> elements) =>
        elements.GroupBy(e => (e.GetTypeId().Value, e.Category?.Id.Value)).Select(g => g.First())
            .SelectMany(e => new[] { e, doc.GetElement(e.GetTypeId()) }.Where(x => x != null))
            .SelectMany(e => e!.Parameters.Cast<Parameter>()).Where(p => p.StorageType != StorageType.None)
            .Select(p => new NumberingSortParameter(p.Id.Value, p.Definition.Name)).DistinctBy(p => p.Id)
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    internal static IReadOnlyList<IReadOnlyList<long>> SortRows(Document doc, IReadOnlyList<IReadOnlyList<long>> rows, NumberingSortParameter? sort, HashSet<long>? selected)
    {
        if (sort == null) return rows;
        var find = Finder(doc, sort.Id);
        NumberingSortValue Value(long id)
        {
            var element = doc.GetElement(new ElementId(id));
            var p = element == null ? null : find(element);
            if (p == null && element != null && doc.GetElement(element.GetTypeId()) is { } type) p = find(type);
            if (p == null || !p.HasValue) return new(0, 0, "");
            return p.StorageType switch
            {
                StorageType.Integer => new(1, p.AsInteger(), ""),
                StorageType.Double => new(1, p.AsDouble(), ""),
                StorageType.String => new(2, 0, p.AsString() ?? ""),
                _ => new(2, 0, p.AsValueString() ?? doc.GetElement(p.AsElementId())?.Name ?? "")
            };
        }
        return ScheduleNumberingPolicy.Sort(rows, Value, selected);
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

    // A temporary shared parameter exposes exact element identities in native row order.
    // One uncommitted transaction holds the entire probe, avoiding intermediate transaction
    // commits for temporary IDs/group markers. Refresh the temporary table explicitly for reading.
    internal static IReadOnlyList<IReadOnlyList<long>> ReadRows(Document doc, ViewSchedule original, IReadOnlyList<Element> elements)
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
            using var probe = new Transaction(doc, "BotPlus: чтение строк спецификации");
            if (probe.Start() != TransactionStatus.Started) throw new InvalidOperationException("Нельзя начать чтение строк.");
            try
            {
                ViewSchedule clone;
                int markerColumn;
                int[] sortColumns;
                int countColumn;
                {
                    var categories = doc.Application.Create.NewCategorySet();
                    foreach (var category in elements.Select(e => e.Category).Where(c => c != null).DistinctBy(c => c.Id.Value))
                        if (category.AllowsBoundParameters) categories.Insert(category);
                    if (categories.Size == 0 || !doc.ParameterBindings.Insert(external, doc.Application.Create.NewInstanceBinding(categories), GroupTypeId.Data))
                        throw new InvalidOperationException("Нельзя временно сопоставить строки этой категории с элементами.");
                    doc.Regenerate();
                    foreach (var element in elements)
                    {
                        var p = element.get_Parameter(guid);
                        if (p == null || p.IsReadOnly || !p.Set(element.Id.Value.ToString(CultureInfo.InvariantCulture)))
                            throw new InvalidOperationException($"Нельзя определить строку элемента {element.Id.Value}. Модель не изменена.");
                    }
                    clone = (ViewSchedule)doc.GetElement(original.Duplicate(ViewDuplicateOption.Duplicate));
                    clone.ViewTemplateId = ElementId.InvalidElementId;
                    clone.Definition.IsItemized = true;
                    clone.Definition.ShowTitle = false;
                    clone.Definition.ShowHeaders = false;
                    clone.Definition.ShowGrandTotal = false;
                    var sort = clone.Definition.GetSortGroupFields();
                    foreach (var field in sort) { field.ShowHeader = false; field.ShowFooter = false; field.ShowBlankLine = false; }
                    clone.Definition.SetSortGroupFields(sort);
                    // Keep filter/formula dependencies, but do not render the whole user table per instance.
                    var sortIds = sort.Select(g => g.FieldId).ToHashSet();
                    foreach (var fieldId in clone.Definition.GetFieldOrder())
                        clone.Definition.GetField(fieldId).IsHidden = !sortIds.Contains(fieldId);
                    // Count column: in the collapsed table it tells how many elements each row holds.
                    var countField = clone.Definition.GetFieldOrder().Select(clone.Definition.GetField).FirstOrDefault(fl => fl.FieldType == ScheduleFieldType.Count)
                        ?? clone.Definition.AddField(ScheduleFieldType.Count);
                    countField.IsHidden = false;
                    var parameter = SharedParameterElement.Lookup(doc, guid) ?? throw new InvalidOperationException("Временный параметр отсутствует.");
                    clone.Definition.AddField(ScheduleFieldType.Instance, parameter.Id);
                    var order = clone.Definition.GetFieldOrder().Where(id => !clone.Definition.GetField(id).IsHidden).ToList();
                    markerColumn = order.Count - 1;
                    countColumn = order.IndexOf(countField.FieldId);
                    sortColumns = clone.Definition.GetSortGroupFields().Select(g => order.IndexOf(g.FieldId)).Where(i => i >= 0).ToArray();
                    Refresh(doc, clone);
                }
                var tokens = elements.Select(e => e.Id.Value.ToString(CultureInfo.InvariantCulture)).ToHashSet();
                var itemRows = ReadItemRows(clone, markerColumn, [], tokens);
                if (itemRows.Count != elements.Count || itemRows.Select(x => x.Token).Distinct().Count() != elements.Count)
                    throw new InvalidOperationException("Не все строки однозначно сопоставлены. Нумерация отменена.");
                var ordered = itemRows.Select(x => x.Token).ToList();
                if (original.Definition.IsItemized)
                    return ordered.Select(id => (IReadOnlyList<long>)new[] { long.Parse(id, CultureInfo.InvariantCulture) }).ToArray();

                // Exact rows: Revit's collapsed rows are consecutive runs of the itemized order (both follow the same sorting),
                // and the Count column gives each run's length. The split is then confirmed with one marker per row.
                var byIdExact = elements.ToDictionary(e => e.Id.Value);
                clone.Definition.IsItemized = false;
                Refresh(doc, clone);
                if (ReadCounts(clone, countColumn) is { } counts && counts.Sum() == itemRows.Count)
                {
                    var runs = new List<long[]>(); int at = 0;
                    foreach (int n in counts)
                    {
                        var run = new long[n];
                        for (int i = 0; i < n; i++) run[i] = long.Parse(itemRows[at++].Token, CultureInfo.InvariantCulture);
                        runs.Add(run);
                    }
                    for (int i = 0; i < runs.Count; i++) foreach (long id in runs[i])
                        if (!byIdExact[id].get_Parameter(guid).Set("BP_ROW_" + i)) throw new InvalidOperationException("Не удалось проверить строку.");
                    Refresh(doc, clone);
                    var labels = runs.Select((_, i) => "BP_ROW_" + i).ToList();
                    var shown = ReadCollapsed(clone, markerColumn, labels.ToHashSet());
                    if (shown.SequenceEqual(labels)) return runs;
                }
                clone.Definition.IsItemized = true;
                foreach (var element in elements)
                    if (!element.get_Parameter(guid).Set(element.Id.Value.ToString(CultureInfo.InvariantCulture)))
                        throw new InvalidOperationException("Не удалось повторно определить строку элемента.");
                Refresh(doc, clone);
                // Sort text is needed only by the fallback, not by the normal count-based path.
                itemRows = ReadItemRows(clone, markerColumn, sortColumns, tokens);

                // Fallback. Rows of the grouped table: elements are grouped by the sorting columns as Revit displays them, then checked against
                // Revit's own collapsed rows. A collapsed row with a blank marker merges our neighbouring groups (Revit grouped them
                // together); if Revit splits a group, raw parameter values refine it on the second attempt.
                var byId = elements.ToDictionary(e => e.Id.Value);
                var groupFields = original.Definition.GetSortGroupFields().Select(g => original.Definition.GetField(g.FieldId)).ToArray();
                var finders = groupFields.Where(fl => !fl.IsCalculatedField && !fl.IsCombinedParameterField && fl.ParameterId != ElementId.InvalidElementId)
                    .Select(fl => Finder(doc, fl.ParameterId.Value)).ToArray();
                string? failure = null;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var groups = new List<List<long>>(); string? lastKey = null;
                    foreach (var (token, text) in itemRows)
                    {
                        long id = long.Parse(token, CultureInfo.InvariantCulture);
                        string key = attempt == 0 ? text : text + "\u0001" + string.Join("\u0001", finders.Select(find => GroupValue(doc, byId[id], find)));
                        if (groups.Count == 0 || key != lastKey) { groups.Add([]); lastKey = key; }
                        groups[^1].Add(id);
                    }
                    {
                        for (int i = 0; i < groups.Count; i++) foreach (long id in groups[i])
                            if (!byId[id].get_Parameter(guid).Set("BP_GROUP_" + i)) throw new InvalidOperationException("Не удалось проверить группу.");
                        clone.Definition.IsItemized = false;
                        Refresh(doc, clone);
                    }
                    var collapsed = ReadCollapsed(clone, markerColumn, groups.Select((_, i) => "BP_GROUP_" + i).ToHashSet());
                    var rows = ScheduleNumberingPolicy.Reconcile(collapsed.Select(x => x is null ? (int?)null : int.Parse(x[9..], CultureInfo.InvariantCulture)).ToList(), groups.Count, out failure);
                    if (rows != null) return rows.Select(row => (IReadOnlyList<long>)row.SelectMany(i => groups[i]).ToArray()).ToArray();
                    clone.Definition.IsItemized = true;
                    Refresh(doc, clone);
                }
                throw new InvalidOperationException("Не удалось сопоставить сгруппированные строки спецификации с элементами (" + failure + "). Параметры не изменены. " +
                    "Включите «Для каждого экземпляра» в спецификации или уберите сортировку по вычисляемым полям.");
            }
            finally
            {
                if (probe.GetStatus() == TransactionStatus.Started && probe.RollBack() != TransactionStatus.RolledBack)
                    throw new InvalidOperationException("Не подтверждён откат временной таблицы.");
            }
        }
        finally
        {
            doc.Application.SharedParametersFilename = previous;
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    static void Refresh(Document doc, ViewSchedule schedule)
    {
        doc.Regenerate();
        if (!schedule.RefreshData()) throw new InvalidOperationException("Не удалось обновить временную таблицу.");
    }

    /// <summary>Itemized rows: element token and the sorting columns' text.</summary>
    static List<(string Token, string Key)> ReadItemRows(ViewSchedule schedule, int markerColumn, int[] sortColumns, HashSet<string> expected)
    {
        var body = schedule.GetTableData().GetSectionData(SectionType.Body);
        var result = new List<(string, string)>();
        for (int row = body.FirstRowNumber; row <= body.LastRowNumber; row++)
        {
            string token = schedule.GetCellText(SectionType.Body, row, body.FirstColumnNumber + markerColumn).Trim();
            if (expected.Contains(token))
                result.Add((token, string.Join("\u0001", sortColumns.Select(c => schedule.GetCellText(SectionType.Body, row, body.FirstColumnNumber + c).Trim()))));
            else if (token.Length != 0) throw new InvalidOperationException("В строке таблицы нет однозначного идентификатора.");
        }
        return result;
    }

    /// <summary>Element count of each collapsed row from the Count column; null if a cell is not a number.</summary>
    static List<int>? ReadCounts(ViewSchedule schedule, int countColumn)
    {
        if (countColumn < 0) return null;
        var body = schedule.GetTableData().GetSectionData(SectionType.Body);
        var result = new List<int>();
        for (int row = body.FirstRowNumber; row <= body.LastRowNumber; row++)
        {
            string digits = new(schedule.GetCellText(SectionType.Body, row, body.FirstColumnNumber + countColumn).Where(char.IsDigit).ToArray());
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n <= 0) return null;
            result.Add(n);
        }
        return result;
    }

    /// <summary>Collapsed data rows: our group token, or null where Revit merged several groups (marker cell blank).</summary>
    static List<string?> ReadCollapsed(ViewSchedule schedule, int markerColumn, HashSet<string> expected)
    {
        var body = schedule.GetTableData().GetSectionData(SectionType.Body);
        var result = new List<string?>();
        for (int row = body.FirstRowNumber; row <= body.LastRowNumber; row++)
        {
            string token = schedule.GetCellText(SectionType.Body, row, body.FirstColumnNumber + markerColumn).Trim();
            result.Add(expected.Contains(token) ? token : token.Length == 0 ? null : throw new InvalidOperationException("В строке таблицы нет однозначного идентификатора."));
        }
        return result;
    }

    /// <summary>Raw sorting value; an element without the parameter has a blank cell in Revit and groups as blank.</summary>
    static string GroupValue(Document doc, Element element, Func<Element, Parameter?> find)
    {
        var p = find(element);
        if (p == null && doc.GetElement(element.GetTypeId()) is { } type) p = find(type);
        if (p == null || !p.HasValue) return "-";
        return p.StorageType switch
        {
            StorageType.String => "S:" + p.AsString(),
            StorageType.Integer => "I:" + p.AsInteger().ToString(CultureInfo.InvariantCulture),
            StorageType.Double => "D:" + p.AsDouble().ToString("R", CultureInfo.InvariantCulture),
            StorageType.ElementId => "E:" + p.AsElementId().Value.ToString(CultureInfo.InvariantCulture),
            _ => "-"
        };
    }

    internal static NumberingResult Apply(Document doc, IReadOnlyList<IReadOnlyList<long>> rows, NumberingParameter chosen, int start, HashSet<long>? selected)
    {
        var find = Finder(doc, chosen.Id);
        var input = rows.Select(ids => new NumberingRow(ids.Select(id =>
        {
            var p = doc.GetElement(new ElementId(id)) is { } e ? find(e) : null;
            return new NumberingTarget(id, p != null && !p.IsReadOnly && p.StorageType == chosen.StorageType, selected?.Contains(id) ?? true);
        }).ToArray())).ToArray();
        var plan = ScheduleNumberingPolicy.Build(input, start, selected != null);
        int updated = 0, writtenRows = 0, skipped = plan.Skipped;
        var errors = new List<string>();
        using var groupTransaction = new TransactionGroup(doc, "Нумерация спецификации");
        groupTransaction.Start();
        var expected = new Dictionary<long, int>();
        using var transaction = new Transaction(doc, "Запись номеров");
        transaction.Start();
        foreach (var group in plan.Groups)
        {
            int number = checked(start + writtenRows); bool written = false;
            string text = number.ToString(CultureInfo.InvariantCulture);
            foreach (long id in group.ElementIds)
            {
                try
                {
                    var p = find(doc.GetElement(new ElementId(id)))!;
                    // Unchanged values are not rewritten: fewer modified elements, faster commit.
                    bool same = chosen.StorageType == StorageType.Integer ? p.HasValue && p.AsInteger() == number : p.AsString() == text;
                    if (!same && !(chosen.StorageType == StorageType.Integer ? p.Set(number) : p.Set(text)))
                        throw new InvalidOperationException("Revit не принял значение.");
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
        Commit(transaction);
        foreach (var (id, number) in expected)
        {
            var p = find(doc.GetElement(new ElementId(id)))!;
            if (chosen.StorageType == StorageType.Integer ? p.AsInteger() != number : p.AsString() != number.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("После фиксации Revit изменил номер. Нумерация отменена.");
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
