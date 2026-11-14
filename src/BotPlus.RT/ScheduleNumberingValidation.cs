using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BotPlus.RT;

// Explicit integration fixture, run inside Revit via an external-command runner.
// Creates and closes its own unsaved project; never writes the active user project.
[Transaction(TransactionMode.Manual)]
public sealed class ScheduleNumberingValidationCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        Document? doc = null;
        try
        {
            doc = data.Application.Application.NewProjectDocument(UnitSystem.Metric);
            var ids = new List<long>(); ViewSchedule schedule;
            long target = (long)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS;
            using (var setup = new Transaction(doc, "Numbering fixture"))
            {
                setup.Start();
                var level = Level.Create(doc, 0);
                var type = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t => t.Kind == WallKind.Basic);
                foreach (int length in new[] { 12, 4, 8 })
                {
                    var wall = Wall.Create(doc, Line.CreateBound(new XYZ(0, ids.Count * 10, 0), new XYZ(length, ids.Count * 10, 0)), type.Id, level.Id, 10, 0, false, false);
                    ids.Add(wall.Id.Value);
                }
                schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Walls));
                schedule.Definition.AddField(ScheduleFieldType.Instance, new ElementId(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS));
                var lengthField = schedule.Definition.AddField(ScheduleFieldType.Instance, new ElementId(BuiltInParameter.CURVE_ELEM_LENGTH));
                schedule.Definition.AddSortGroupField(new ScheduleSortGroupField(lengthField.FieldId) { SortOrder = ScheduleSortOrder.Ascending });
                schedule.Definition.IsItemized = true;
                if (setup.Commit() != TransactionStatus.Committed) throw new Exception("Fixture setup");
            }
            void Assert(bool ok, string text) { if (!ok) throw new Exception(text); }
            var candidates = ScheduleNumbering.Elements(doc, schedule);
            int parameterCount = new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).GetElementCount();
            int scheduleCount = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).GetElementCount();
            string previousFile = doc.Application.SharedParametersFilename;
            var rows = ScheduleNumbering.ReadRows(doc, schedule, candidates);
            Assert(rows.SelectMany(x => x).SequenceEqual(new[] { ids[1], ids[2], ids[0] }), "Native length order differs");
            Assert(new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).GetElementCount() == parameterCount &&
                new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).GetElementCount() == scheduleCount &&
                doc.Application.SharedParametersFilename == previousFile, "Probe left resources/settings");
            var parameter = new NumberingParameter(target, "Комментарии", StorageType.String);
            var result = ScheduleNumbering.Apply(doc, rows, parameter, 10, null);
            Assert(result.Updated == 3 && result.Rows == 3, "Numbered count");
            foreach (var (id, expected) in new[] { (ids[1], "10"), (ids[2], "11"), (ids[0], "12") })
                Assert(ScheduleNumbering.ParameterOf(doc.GetElement(new ElementId(id)), target)!.AsString() == expected, "Native value");
            result = ScheduleNumbering.Apply(doc, rows, parameter, 20, [ids[2]]);
            Assert(result.Updated == 1 && ScheduleNumbering.ParameterOf(doc.GetElement(new ElementId(ids[1])), target)!.AsString() == "10", "Selected-only altered unrelated wall");
            using (var grouping = new Transaction(doc, "Grouped fixture"))
            {
                grouping.Start(); schedule.Definition.ClearSortGroupFields(); schedule.Definition.IsItemized = false;
                if (grouping.Commit() != TransactionStatus.Committed) throw new Exception("Grouping fixture");
            }
            rows = ScheduleNumbering.ReadRows(doc, schedule, candidates);
            Assert(rows.Count == 1 && rows[0].Count == 3, "Grouped native membership");
            result = ScheduleNumbering.Apply(doc, rows, parameter, 30, null);
            Assert(result.Rows == 1 && result.Updated == 3, "Grouped numbering");
            TaskDialog.Show("Проверка нумерации", "PASS: порядок строк, все/выделенные элементы, общая нумерация группы, откат временных данных.");
            return Result.Succeeded;
        }
        catch (Exception error) { message = error.ToString(); return Result.Failed; }
        finally { doc?.Close(false); }
    }
}
