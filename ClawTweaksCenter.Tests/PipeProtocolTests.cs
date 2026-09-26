using System.Text.Json;
using ClawTweaksCenter.Core;
using Shared.Enums;

namespace ClawTweaksCenter.Tests;

internal static class PipeProtocolTests
{
    [RegressionTest]
    private static void WindowsPathsSurviveRoundTrip()
    {
        const string expected = @"ok=1;path=C:\temp\new\report.zip";
        string wire = JsonSerializer.Serialize(new { Function = 1, Content = expected });
        AssertEx.True(HelperPipeProtocol.TryParse(wire, out var function, out var content));
        AssertEx.Equal((Function)1, function);
        AssertEx.Equal(expected, content);
    }

    [RegressionTest]
    private static void UnicodeQuotesAndNestedJsonSurviveRoundTrip()
    {
        foreach (var expected in new[] { "caffè 日本語 🎮", "quoted \"text\"\nline\tcolumn",
                     JsonSerializer.Serialize(new { path = @"C:\temp\new", title = "日本語" }) })
        {
            string wire = JsonSerializer.Serialize(new { Function = 123, Content = expected });
            AssertEx.True(HelperPipeProtocol.TryParse(wire, out _, out var content));
            AssertEx.Equal(expected, content);
        }
    }

    [RegressionTest]
    private static void MalformedAndNestedPropertiesAreRejected()
    {
        foreach (var wire in new[] { "{\"Function\":1,\"Content\":\"unfinished\"", "{\"Function\":1,\"Content\":42}",
                     "{\"nested\":{\"Function\":1},\"Content\":\"not a property push\"}",
                     "[]", "null", "", "{\"Function\":2147483648}" })
            AssertEx.False(HelperPipeProtocol.TryParse(wire, out _, out _), "Accepted malformed property push: " + wire);
    }

    [RegressionTest]
    private static void OptionalContentAndUnknownOrdinalsRemainCompatible()
    {
        AssertEx.True(HelperPipeProtocol.TryParse("{\"Function\":2147483647}", out var function, out var content));
        AssertEx.Equal((Function)int.MaxValue, function);
        AssertEx.Equal<string?>(null, content);
        AssertEx.True(HelperPipeProtocol.TryParse("{\"Function\":0,\"Content\":null}", out function, out content));
        AssertEx.Equal(Function.None, function);
        AssertEx.Equal<string?>(null, content);
    }
}
