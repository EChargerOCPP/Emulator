namespace YKC.Emulator;

/// <summary>Батарея эмулируемого электромобиля: из неё собираются кадры BMS 0x15–0x25 (только DC).</summary>
public sealed record BmsProfile
{
    public string Vin { get; init; } = "LSVTEST0000000001";
    /// <summary>Тип батареи по GB/T 27930: 3 — LiFePO4.</summary>
    public byte BatteryType { get; init; } = 3;
    public decimal CapacityAh { get; init; } = 200m;
    public decimal RatedVoltageV { get; init; } = 380m;
    public decimal RatedEnergyKwh { get; init; } = 76m;
    public decimal MaxCellVoltageV { get; init; } = 3.65m;
    public decimal MaxChargeCurrentA { get; init; } = 150m;
    public decimal MaxChargeVoltageV { get; init; } = 418m;
    public int MaxTemperatureC { get; init; } = 55;
    public int StartSoc { get; init; } = 30;
    /// <summary>Прирост SOC за один тик зарядки; на 100% BMS сама прерывает зарядку (0x1D).</summary>
    public int SocPerTick { get; init; } = 1;
}
