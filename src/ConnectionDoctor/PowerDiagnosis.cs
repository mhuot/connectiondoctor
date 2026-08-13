namespace ConnectionDoctor;

internal static class PowerDiagnosis
{
    public static IReadOnlyList<Finding> Analyze(PowerState power)
    {
        if (!power.IsDeficit || power.BatteryRateMilliwatts is null)
        {
            return [];
        }

        var watts = Math.Abs(power.BatteryRateMilliwatts.Value) / 1000.0;
        return
        [
            new Finding(
                "warning",
                "Battery is supplying power while AC is connected",
                $"The battery is covering {watts:F1} W while Windows reports AC power, so the supply is not meeting demand.",
                "Use a higher-wattage supply or reduce the devices powered through the dock.")
        ];
    }
}
