using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BuiltInCategory = Autodesk.Revit.DB.BuiltInCategory;
using Point = System.Windows.Point;

namespace BotPlus.Eom;

internal static class EquipmentCategoryIcons
{
    static readonly Geometry All = Shape("M2,2 H6 V6 H2 Z M10,2 H14 V6 H10 Z M2,10 H6 V14 H2 Z M10,10 H14 V14 H10 Z");
    static readonly Geometry Mark = Shape("M9,2 H14 V6 L11,9 L8,6 Z M11,4 H12");
    static readonly Dictionary<(BuiltInCategory Category, string Label), Geometry> Cache = [];

    internal static Geometry Get(BuiltInCategory? category, string label = "")
    {
        if (category is not { } id) return All;
        var key = (id, label);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        string name = id.ToString();
        string? model = ModelShape(name);
        Geometry geometry;
        if (model != null) geometry = Shape(model);
        else if (name.EndsWith("Tags", StringComparison.Ordinal))
        {
            string stem = name[..^4];
            string? target = ModelShape(stem) ?? ModelShape(stem + "s");
            geometry = new GeometryGroup
            {
                Children = { Fit(target == null ? Initials(label) : Shape(target), new Rect(1, 6, 8, 8)), Mark }
            };
        }
        else
        {
            geometry = new GeometryGroup
            {
                Children = { Shape("M1.5,3 H14.5 V13 H1.5 Z"), Fit(Initials(label), new Rect(3, 5, 10, 6)) }
            };
        }
        geometry.Freeze();
        Cache.Add(key, geometry);
        return geometry;
    }

    static Geometry Shape(string path)
    {
        var shape = Geometry.Parse(path);
        shape.Freeze();
        return shape;
    }

    static Geometry Initials(string label)
    {
        var words = label.Split([' ', '-', '–', '—', '_', '/', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => char.IsLetterOrDigit(word[0]) &&
                !word.Equals("Марки", StringComparison.OrdinalIgnoreCase) &&
                !word.Equals("Tags", StringComparison.OrdinalIgnoreCase))
            .Take(3).ToArray();
        string text = words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(words[0].Length, 2)],
            _ => string.Concat(words.Select(word => word[0]))
        };
        return new FormattedText(text.ToUpperInvariant(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, Brushes.Black, 1).BuildGeometry(new Point());
    }

    static Geometry Fit(Geometry geometry, Rect area)
    {
        Rect bounds = geometry.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return Geometry.Empty;
        double scale = Math.Min(area.Width / bounds.Width, area.Height / bounds.Height);
        return new GeometryGroup
        {
            Children = { geometry },
            Transform = new MatrixTransform(scale, 0, 0, scale,
                area.X + (area.Width - bounds.Width * scale) / 2 - bounds.X * scale,
                area.Y + (area.Height - bounds.Height * scale) / 2 - bounds.Y * scale)
        };
    }

    static string? ModelShape(string name) => name switch
    {
        "OST_LightingFixtures" => "M8,2 A6,6 0 1 1 7.99,2 Z M4,4 L12,12 M12,4 L4,12",
        "OST_LightingDevices" => "M1,11 H4 M4,11 L12,4 M12,11 H15",
        "OST_ElectricalFixtures" => "M2,12 A6,6 0 0 1 14,12 Z M8,6 V2",
        "OST_ElectricalEquipment" => "M2,3 H14 V13 H2 Z M9,4 L6,8 H10 L7,12",
        "OST_CommunicationDevices" => "M8,9 V14 M4,14 H12 M5,7 Q8,4 11,7 M2,4 Q8,-1 14,4",
        "OST_TelephoneDevices" => "M2,3 L5,2 L7,5 L5,7 Q7,10 10,11 L12,9 L15,11 L14,14 Q4,14 2,3 Z",
        "OST_DataDevices" => "M2,2 H14 V11 H2 Z M8,11 V14 M5,14 H11",
        "OST_FireAlarmDevices" => "M8,1 Q7,5 4,8 A4.5,4.5 0 1 0 12,8 Q11,6 10,5 L9,8 Q9,4 8,1 Z",
        "OST_SecurityDevices" => "M8,2 L14,4 V8 Q13,12 8,14 Q3,12 2,8 V4 Z",
        "OST_NurseCallDevices" => "M3,2 H13 V14 H3 Z M5,8 H11 M8,5 V11",
        "OST_MechanicalEquipment" => "M2,2 H14 V14 H2 Z M8,4 A4,4 0 1 1 7.99,4 Z M8,8 L11,6 M8,8 L5,7 M8,8 V12",
        "OST_MechanicalControlDevices" => "M3,3 H13 V13 H3 Z M5,10 L7,6 L9,10 L11,6",
        "OST_PlumbingEquipment" => "M4,4 Q4,2 8,2 Q12,2 12,4 V11 Q12,13 8,13 Q4,13 4,11 Z M6,13 V15 M10,13 V15 M7,7 H9",
        "OST_PlumbingFixtures" => "M2,7 H14 Q13,13 8,13 Q3,13 2,7 Z M8,7 V3 H11 V5",
        "OST_Sprinklers" => "M8,1 V5 M4,6 H12 M4,8 L2,11 M8,8 V12 M12,8 L14,11",
        "OST_DuctTerminal" => "M2,3 H14 V13 H2 Z M5,5 V11 M8,5 V11 M11,5 V11",
        "OST_DuctCurves" => "M1,6 L6,2 H15 V10 L10,14 H1 Z M1,6 H10 L15,2 M10,6 V14",
        "OST_DuctFitting" => "M1,2 H7 V8 H15 V14 H5 Q1,14 1,10 Z",
        "OST_DuctAccessory" => "M1,4 H15 V12 H1 Z M4,11 L12,5 M8,8 V2",
        "OST_DuctInsulations" or "OST_DuctLinings" => "M1,3 H15 V13 H1 Z M4,5 H12 V11 H4 Z M1,3 L4,5 M12,11 L15,13",
        "OST_PipeCurves" => "M4,4 A2,4 0 1 0 4,12 A2,4 0 0 0 4,4 Z M4,4 H12 A2,4 0 0 1 12,12 H4",
        "OST_PipeFitting" => "M2,3 H6 V7 Q6,10 9,10 H14 V14 H9 Q2,14 2,7 Z",
        "OST_PipeAccessory" => "M1,4 L8,8 L15,4 V12 L8,8 L1,12 Z M8,8 V2 M5,2 H11",
        "OST_PipeInsulations" => "M8,2 A6,6 0 1 1 7.99,2 Z M8,5 A3,3 0 1 1 7.99,5 Z M12,4 L10,6 M4,12 L6,10",
        "OST_CableTray" => "M2,3 V13 H14 V3 M2,9 H14 M5,9 V13 M8,9 V13 M11,9 V13",
        "OST_CableTrayFitting" => "M1,2 H6 V7 H14 V13 H1 Z M3,4 V11 H12",
        "OST_Conduit" => "M2,12 L11,3 A2,2 0 0 1 14,6 L5,15 M3,11 L6,14",
        "OST_ConduitFitting" => "M3,2 H7 V8 Q7,11 10,11 H14 V14 H10 Q3,14 3,8 Z",
        "OST_Wire" => "M1,12 Q4,3 8,8 Q12,13 15,4",
        "OST_Doors" => "M2,14 V2 H13 V14 M5,14 V3 L11,5 V12 Z M9,8 H10",
        "OST_Windows" => "M2,2 H14 V14 H2 Z M8,2 V14 M2,8 H14",
        "OST_CurtainWallPanels" => "M1,3 H15 V13 H1 Z M5.5,3 V13 M10.5,3 V13",
        "OST_CurtainWallMullions" => "M4,2 H12 V5 H9 V11 H12 V14 H4 V11 H7 V5 H4 Z",
        "OST_Walls" => "M1,3 H15 V13 H1 Z M1,8 H15 M5,3 V8 M11,3 V8 M8,8 V13",
        "OST_Floors" => "M1,7 L7,3 L15,7 L9,11 Z M1,7 V10 L9,14 L15,10 V7",
        "OST_Roofs" => "M1,9 L8,2 L15,9 M3,9 L8,4 L13,9 M4,9 V14 H12 V9",
        "OST_Ceilings" => "M2,3 H14 V12 H2 Z M2,6 H14 M6,6 V12 M10,6 V12",
        "OST_Stairs" or "OST_StairsRuns" => "M1,14 H4 V10 H8 V6 H12 V2 H15",
        "OST_StairsLandings" => "M1,11 H6 V6 H15 M1,13 H8 V8 H15",
        "OST_StairsRailing" or "OST_Railings" or "OST_RailingSystem" => "M2,14 V4 L14,2 V14 M6,13 V4 M10,13 V3 M2,9 L14,7",
        "OST_StairsRailingBaluster" or "OST_RailingSystemBaluster" => "M5,2 H11 M5,14 H11 M7,2 V5 L6,8 L7,11 V14 M9,2 V5 L10,8 L9,11 V14",
        "OST_Columns" => "M3,2 H13 V4 H11 V12 H13 V14 H3 V12 H5 V4 H3 Z",
        "OST_StructuralColumns" => "M5,2 H10 L12,4 V13 L7,15 L5,13 Z M5,2 L7,4 H12 M7,4 V15",
        "OST_StructuralFraming" => "M1,4 H15 V6 H9 V10 H15 V12 H1 V10 H7 V6 H1 Z",
        "OST_StructuralFoundation" => "M6,2 H10 V8 H13 V11 H15 V14 H1 V11 H3 V8 H6 Z",
        "OST_StructuralTruss" => "M1,13 L8,3 L15,13 Z M1,13 L8,8 L15,13 M8,3 V13",
        "OST_StructuralStiffener" => "M2,14 V2 H14 Z M5,10 V5 H10",
        "OST_StructConnections" => "M2,3 H14 V13 H2 Z M5,6 H6 M10,6 H11 M5,10 H6 M10,10 H11",
        "OST_Rebar" or "OST_RebarShape" => "M2,13 V6 Q2,3 5,3 H13 V7 M2,9 H4 M6,3 V5 M10,3 V5",
        "OST_FabricReinforcement" or "OST_AreaRein" or "OST_PathRein" => "M2,3 H14 V13 H2 Z M6,3 V13 M10,3 V13 M2,7 H14 M2,10 H14",
        "OST_Coupler" => "M1,8 H4 M12,8 H15 M4,5 H12 V11 H4 Z M7,5 V11 M9,5 V11",
        "OST_BoundaryConditions" => "M8,2 V7 M8,7 L3,12 H13 Z M2,14 H14",
        "OST_Furniture" => "M2,5 H5 V8 H11 V5 H14 V12 H2 Z M4,12 V14 M12,12 V14",
        "OST_FurnitureSystems" => "M2,4 H14 V7 H2 Z M3,7 V14 M13,7 V14 M7,7 V11 H12",
        "OST_Casework" => "M2,3 H14 V13 H2 Z M8,3 V13 M5,8 H6 M10,8 H11",
        "OST_Entourage" => "M8,2 A2,2 0 1 1 7.99,2 Z M8,6 V10 M3,8 L8,6 L13,8 M5,14 L8,10 L11,14",
        "OST_Planting" => "M8,14 V8 M8,10 Q2,9 2,3 Q8,3 8,9 M8,8 Q8,2 14,2 Q14,7 8,8",
        "OST_Site" or "OST_Toposolid" or "OST_Topography" => "M1,13 L4,7 L7,10 L11,3 L15,13 Z M9,7 L11,8 L12,6",
        "OST_Parking" => "M2,2 H14 V14 H2 Z M6,12 V4 H9 A2,2 0 0 1 9,8 H6",
        "OST_GenericModel" => "M8,2 L14,5 V11 L8,14 L2,11 V5 Z M2,5 L8,8 L14,5 M8,8 V14",
        "OST_SpecialityEquipment" => "M2,5 H14 V13 H2 Z M5,5 V2 H11 V5 M2,9 H14 M7,8 H9 V10 H7 Z",
        "OST_DetailComponents" => "M2,13 V3 H12 M5,13 V6 H15 M8,13 V9 H15",
        "OST_ProfileFamilies" => "M2,13 V3 H14 V6 H6 V13 Z",
        "OST_TitleBlocks" or "OST_Sheets" => "M3,2 H13 V14 H3 Z M3,11 H13 M8,11 V14 M5,5 H11",
        "OST_GenericAnnotation" => "M2,3 H14 V13 H2 Z M5,10 L8,5 L11,10 M6,8 H10",
        "OST_GridHeads" or "OST_Grids" => "M8,2 A4,4 0 1 1 7.99,2 Z M8,10 V15 M6,8 L8,4 L10,8",
        "OST_LevelHeads" or "OST_Levels" => "M1,9 H14 M9,9 A3,3 0 1 0 14.99,9 A3,3 0 1 0 9,9 M12,6 V12",
        "OST_SectionHeads" or "OST_Sections" => "M1,9 H5 M5,5 L12,2 V12 L5,9 Z M13,12 H15",
        "OST_CalloutHeads" => "M2,3 H11 V11 H2 Z M11,8 L14,11 V14",
        "OST_Viewers" or "OST_Views" => "M1,8 Q8,1 15,8 Q8,15 1,8 Z M8,6 A2,2 0 1 1 7.99,6 Z",
        "OST_ViewportLabel" => "M5,3 A3,3 0 1 1 4.99,3 Z M8,6 H15 M2,12 H15 M2,14 H10",
        "OST_Rooms" or "OST_MEPSpaces" => "M2,2 H14 V14 H2 Z M2,9 H7 V14 M10,5 H12",
        "OST_SpotElevations" => "M2,11 H14 M4,11 L8,5 L12,11 M8,5 V2",
        "OST_Dimensions" => "M2,4 V12 M14,4 V12 M2,8 H14 M1,10 L3,6 M13,10 L15,6",
        _ => null
    };
}
