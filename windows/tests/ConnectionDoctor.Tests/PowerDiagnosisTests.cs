namespace ConnectionDoctor.Tests;

public sealed class PowerDiagnosisTests
{
    [Fact]
    public void WarnsWhenBatteryCoversMeaningfulDemandOnAc()
    {
        var findings = PowerDiagnosis.Analyze(new PowerState(true, 80, -10_500));

        var finding = Assert.Single(findings);
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("10.5 W", finding.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, -1_999)]
    [InlineData(true, 0)]
    [InlineData(false, -10_500)]
    public void IgnoresTrickleAndIntentionalBatteryUse(bool online, int rate)
    {
        Assert.Empty(PowerDiagnosis.Analyze(new PowerState(online, 80, rate)));
    }
}
