namespace HeatLossViewer.Export;

/// <summary>Project-wide inputs in explicit SI units; null means not specified.</summary>
public sealed class ProjectConditions
{
    public string Name { get; set; } = "";
    public string Number { get; set; } = "";
    public string Address { get; set; } = "";
    public string ClimateRegion { get; set; } = "";
    public string ClimateSubregion { get; set; } = "";
    public double? OutdoorTemperature { get; set; }
    public double? IndoorTemperature { get; set; }
    public double? WindSpeed { get; set; }
    public double? HeatingPeriodTemperature { get; set; }
    public double? HeatingPeriodDays { get; set; }
    public double? BuildingHeight { get; set; }
    public double? FirstFloorHeight { get; set; }
    public string Notes { get; set; } = "";
    public ProjectCalculationInputs Calculation { get; set; } = new();

    public void Validate()
    {
        Calculation.Validate();
        foreach (var (label, value, min, max) in new (string, double?, double, double)[]
        {
            ("Наружная температура", OutdoorTemperature, -100, 70),
            ("Внутренняя температура", IndoorTemperature, -100, 70),
            ("Температура отопительного периода", HeatingPeriodTemperature, -100, 70),
            ("Скорость ветра", WindSpeed, 0, 150),
            ("Продолжительность отопительного периода", HeatingPeriodDays, 0, 366),
            ("Высота здания", BuildingHeight, 0, 2000),
            ("Высота от земли до первого этажа", FirstFloorHeight, 0, 2000)
        })
            if (value.HasValue && (!double.IsFinite(value.Value) || value < min || value > max))
                throw new ArgumentException($"{label}: допустимо от {min} до {max}.");
    }
}
