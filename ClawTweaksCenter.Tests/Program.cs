using System.Reflection;

namespace ClawTweaksCenter.Tests;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class RegressionTestAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
internal sealed class RegressionProbeAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        var methods = Assembly.GetExecutingAssembly().GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)).ToArray();
        if (args.Length >= 2 && args[0] == "--probe")
        {
            var probe = methods.Single(m => m.GetCustomAttribute<RegressionProbeAttribute>()?.Name == args[1]);
            var result = probe.Invoke(null, probe.GetParameters().Length == 0 ? null : new object[] { args.Skip(2).ToArray() });
            Console.WriteLine(result);
            return 0;
        }
        var filter = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var tests = methods.Where(m => m.GetCustomAttribute<RegressionTestAttribute>() != null)
            .Where(m => filter == null || Name(m).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(Name).ToArray();
        if (args.Contains("--list"))
        {
            foreach (var test in tests) Console.WriteLine(Name(test));
            return 0;
        }
        if (tests.Length == 0) { Console.Error.WriteLine("No matching regression tests."); return 1; }
        int failed = 0;
        foreach (var test in tests)
        {
            try
            {
                var result = test.Invoke(null, null);
                if (result is Task task) await task;
                Console.WriteLine("PASS " + Name(test));
            }
            catch (Exception ex)
            {
                failed++;
                var cause = ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException : ex;
                Console.Error.WriteLine("FAIL " + Name(test) + ": " + cause);
            }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static string Name(MethodInfo method) => method.DeclaringType!.Name + "." + method.Name;
}

internal static class AssertEx
{
    public static void True(bool condition, string message = "Expected true.")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void False(bool condition, string message = "Expected false.") => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message ?? $"Expected <{expected}>, actual <{actual}>.");
    }

    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }

    public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
        => True(expected.SequenceEqual(actual), "Sequences differ.");
}
