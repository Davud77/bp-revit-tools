using System.Globalization;
using Autodesk.Revit.DB;

namespace BotPlus.RT;

internal sealed record NumberingParameter(long Id, string Name, StorageType StorageType, bool Ambiguous = false)
{
    public string Label => Name + (StorageType == StorageType.Integer ? " (целое число)" : " (текст)") + (Ambiguous ? $" · ID {Id}" : "");
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

    // A temporary shared parameter exposes exact element identities in native row order.
    // The whole probe (binding, values, duplicate schedule) is rolled back before writing.
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
            using var probe = new TransactionGroup(doc, "BotPlus: чтение строк спецификации");
            if (probe.Start() != TransactionStatus.Started) throw new InvalidOperationException("Нельзя начать чтение строк.");
            try
            {
                ViewSchedule clone;
                int markerColumn;
                using (var t = new Transaction(doc, "Временная таблица"))
                {
                    t.Start();
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
                    var parameter = SharedParameterElement.Lookup(doc, guid) ?? throw new InvalidOperationException("Временный параметр отсутствует.");
                    var marker = clone.Definition.AddField(ScheduleFieldType.Instance, parameter.Id);
                    markerColumn = clone.Definition.GetFieldOrder().Select(id => clone.Definition.GetField(id)).Count(f => !f.IsHidden) - 1;
                    Commit(t);
                }
                var ordered = ReadTokens(clone, markerColumn, elements.Select(e => e.Id.Value.ToString(CultureInfo.InvariantCulture)).ToHashSet());
                if (ordered.Count != elements.Count || ordered.Distinct().Count() != elements.Count)
                    throw new InvalidOperationException("Не все строки однозначно сопоставлены. Нумерация отменена.");
                if (original.Definition.IsItemized)
                    return ordered.Select(id => (IReadOnlyList<long>)new[] { long.Parse(id, CultureInfo.InvariantCulture) }).ToArray();

                // Derive candidate membership from raw sorting values, then validate it against
                // Revit's own collapsed rows. Any formatted/rounded grouping disagreement fails.
                var groupFields = original.Definition.GetSortGroupFields().Select(s => original.Definition.GetField(s.FieldId)).ToArray();
                foreach (var f in groupFields)
                    if (f.IsCalculatedField || f.IsCombinedParameterField || f.ParameterId == ElementId.InvalidElementId)
                        throw new InvalidOperationException($"Сортировка или группировка по полю «{f.GetName()}» (вычисляемое или объединённое) не поддерживается.");
                var finders = groupFields.Select(f => Finder(doc, f.ParameterId.Value)).ToArray();
                var byId = elements.ToDictionary(e => e.Id.Value);
                var groups = new List<List<long>>(); string? lastKey = null;
                foreach (var token in ordered)
                {
                    long id = long.Parse(token, CultureInfo.InvariantCulture);
                    string key = System.Text.Json.JsonSerializer.Serialize(finders.Select(find => GroupValue(doc, byId[id], find)).ToArray());
                    if (groups.Count == 0 || key != lastKey) { groups.Add([]); lastKey = key; }
                    groups[^1].Add(id);
                }
                using (var t = new Transaction(doc, "Проверка групп строк"))
                {
                    t.Start();
                    for (int i = 0; i < groups.Count; i++) foreach (long id in groups[i])
                        if (!byId[id].get_Parameter(guid).Set("BP_GROUP_" + i)) throw new InvalidOperationException("Не удалось проверить группу.");
                    clone.Definition.IsItemized = false;
                    Commit(t);
                }
                var tokens = groups.Select((_, i) => "BP_GROUP_" + i).ToHashSet();
                var actual = ReadTokens(clone, markerColumn, tokens);
                if (actual.Count != groups.Count || actual.Distinct().Count() != groups.Count)
                    throw new InvalidOperationException("Группировка строк неоднозначна. Параметры не изменены.");
                return actual.Select(token => (IReadOnlyList<long>)groups[int.Parse(token[9..], CultureInfo.InvariantCulture)].ToArray()).ToArray();
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

    static List<string> ReadTokens(ViewSchedule schedule, int markerColumn, HashSet<string> expected)
    {
        var body = schedule.GetTableData().GetSectionData(SectionType.Body);
        var result = new List<string>();
        for (int row = body.FirstRowNumber; row <= body.LastRowNumber; row++)
        {
            string token = schedule.GetCellText(SectionType.Body, row, body.FirstColumnNumber + markerColumn).Trim();
            if (expected.Contains(token)) result.Add(token);
            else if (token.Length != 0) throw new InvalidOperationException("В строке таблицы нет однозначного идентификатора.");
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
