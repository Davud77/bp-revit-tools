namespace HeatLossViewer.Export;

public sealed class ProjectCalculationInputs
{
    public Dictionary<string, double> Building { get; set; } = [];
    public Dictionary<string, Dictionary<string, double>> FloorOverrides { get; set; } = [];
    public Dictionary<string, Dictionary<string, double>> RoomOverrides { get; set; } = [];
    public bool Reviewed { get; set; }
    public bool OrientationAdditions { get; set; } = true;

    public static readonly (string Key, string Label, double Default, double Min, double Max)[] Fields =
    [
        ("indoor", "Внутри, °C", 20, -100, 70), ("outdoor", "Снаружи, °C", -25, -100, 70),
        ("ground", "Под полом, °C", 5, -100, 70),
        ("wall", "Стена · U, Вт/(м²·К)", .28, 0, 100), ("window", "Окно · U, Вт/(м²·К)", 1.1, 0, 100),
        ("door", "Дверь · U, Вт/(м²·К)", 1.5, 0, 100), ("roof", "Покрытие · U, Вт/(м²·К)", .2, 0, 100),
        ("floor", "Пол · U, Вт/(м²·К)", .3, 0, 100),
        ("windSpeed", "Скорость ветра, м/с", 0, 0, 150), ("buildingHeight", "Высота здания над землёй, м", 0, 0, 2000),
        ("groundElevation", "Отметка земли в координатах модели, м", 0, -10000, 10000),
        ("northAngle", "Север от оси +Y, °", 0, -360, 360),
        ("windowGn", "Окна · Gn, кг/(м²·ч)", 5, 0, 10000), ("doorGn", "Двери · Gn, кг/(м²·ч)", 7, 0, 10000),
        ("windowF", "Окна · показатель f", .67, .001, 10), ("doorF", "Двери · показатель f", .5, .001, 10),
        ("windowAi", "Окна · коэффициент ai", 1, 0, 100), ("doorAi", "Двери · коэффициент ai", 1, 0, 100),
        ("cornerBonus", "Угловая добавка, доля", 0, 0, 10), ("heatGain", "Теплопоступления на помещение, Вт", 0, 0, 1e9)
    ];
    public static readonly string[] RoomKeys = ["indoor", "outdoor", "cornerBonus", "heatGain"];
    public void Validate()
    {
        void Check(Dictionary<string, double> values, bool scope)
        {
            foreach (var (key, value) in values)
            {
                var field = Fields.FirstOrDefault(f => f.Key == key);
                if (field.Key is null || (scope && !RoomKeys.Contains(key)) || !double.IsFinite(value) || value < field.Min || value > field.Max)
                    throw new ArgumentException($"Недопустимый параметр расчёта: {key}.");
            }
        }
        Check(Building, false);
        foreach (var values in FloorOverrides.Values.Concat(RoomOverrides.Values)) Check(values, true);
    }
}
