using ClawTweaksCenter.Core;

namespace ClawTweaksCenter.Tests;

internal static class FooterPowerStatusTests
{
    private const string ChargingMetrics = """{"batteryLevel":87,"timeRemaining":9000,"timeToFull":4500,"isCharging":true}""";

    [RegressionTest]
    private static void UnpluggedBatteryOverridesStaleChargingAndUsesRemainingTime()
    {
        var status = new FooterPowerStatus();
        AssertEx.Equal("87% · discharging · 2:30 h", status.Update(ChargingMetrics, onMains: false));
    }

    [RegressionTest]
    private static void UnpluggedBatteryNeverShowsTimeToFullAsRuntime()
    {
        var status = new FooterPowerStatus();
        AssertEx.Equal("87% · discharging", status.Update(
            """{"batteryLevel":87,"timeRemaining":-1,"timeToFull":4500,"isCharging":true}""", onMains: false));
    }

    [RegressionTest]
    private static void CachedChargingReadingIsReclassifiedWhenUnpluggedWithoutAHelperReply()
    {
        var status = new FooterPowerStatus();
        AssertEx.Equal("87% · charging · 1:15 h", status.Update(ChargingMetrics, onMains: true));
        AssertEx.Equal("87% · discharging · 2:30 h", status.Update(null, onMains: false));
        AssertEx.Equal("87% · discharging · 2:30 h", status.Update(null, onMains: false));
    }

    [RegressionTest]
    private static void CachedIdleReadingFollowsCurrentAcLineWithoutAHelperReply()
    {
        var status = new FooterPowerStatus();
        const string metrics = """{"batteryLevel":80,"timeRemaining":-1,"timeToFull":-1,"isCharging":false}""";
        AssertEx.Equal("80% · AC power · not charging", status.Update(metrics, onMains: true));
        AssertEx.Equal("80% · discharging", status.Update(null, onMains: false));
        AssertEx.Equal("80% · AC power · not charging", status.Update(null, onMains: true));
    }

    [RegressionTest]
    private static void ExistingChargingFullAndChargeLimitLabelsArePreserved()
    {
        var status = new FooterPowerStatus();
        AssertEx.Equal("87% · charging · 1:15 h", status.Update(ChargingMetrics, onMains: true));
        AssertEx.Equal("87% · charging", status.Update(
            """{"batteryLevel":87,"timeRemaining":-1,"timeToFull":-1,"isCharging":true}""", onMains: true));
        AssertEx.Equal("80% · AC power · not charging", status.Update(
            """{"batteryLevel":80,"isCharging":false}""", onMains: true));
        AssertEx.Equal("100% · AC power · fully charged", status.Update(
            """{"batteryLevel":100,"isCharging":false}""", onMains: true));
        AssertEx.Equal("99% · AC power · fully charged", status.Update(
            """{"batteryLevel":99,"isCharging":false}""", onMains: true));
        AssertEx.Equal("87% · discharging · 2:30 h", status.Update(
            """{"batteryLevel":87,"timeRemaining":9000,"isCharging":false}""", onMains: false));
    }

    [RegressionTest]
    private static void MissingOrUnavailablePercentageRemainsHidden()
    {
        var status = new FooterPowerStatus();
        AssertEx.Equal<string?>(null, status.Update(null, onMains: false));
        foreach (string metrics in new[] { "{}", """{"batteryLevel":-1}""", """{"batteryLevel":0}""" })
        {
            AssertEx.Equal<string?>(null, status.Update(metrics, onMains: false));
            AssertEx.Equal<string?>(null, status.Update(null, onMains: true));
        }
    }

    [RegressionTest]
    private static void LegacyCommaDecimalMetricsDoNotHideBatteryReading()
    {
        var status = new FooterPowerStatus();
        AssertEx.Equal("87% · discharging · 2:30 h", status.Update(
            """{"batteryDrain":9,4,"cpuWattage":12,5,"batteryLevel":87,"timeRemaining":9000,"isCharging":false}""", onMains: false));
    }
}
