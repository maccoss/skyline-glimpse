using System.Reflection;

namespace Glimpse.Tests;

/// <summary>Locations of test data that lives outside this repository.</summary>
internal static class TestData
{
    /// <summary>
    /// Trait category of the tests that read instrument files from the ProteoWizard checkout. CI checks
    /// out only the pwiz-sharp sources, so it runs <c>dotnet test --filter "Category!=RawData"</c>.
    /// </summary>
    public const string RawData = "RawData";

    private static string Metadata(string key) =>
        typeof(TestData).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value ?? "";

    /// <summary>pwiz's Thermo reader test files (BSA-FT-HCD.raw, BSA-FT-ETD.raw, ...).</summary>
    public static string ThermoTestFile(string name) => Path.Combine(Metadata("PwizTestDataDir"), name);

    /// <summary>The repository root (the folder holding Glimpse.sln).</summary>
    public static string RepoRoot => Metadata("RepoRoot");

    /// <summary>pwiz example_data/small.RAW.</summary>
    public static string SmallRaw => Path.Combine(Metadata("PwizExampleDataDir"), "small.RAW");

    /// <summary>
    /// A large real-world run for timing, from the GLIMPSE_BIG_RAW environment variable; null when unset
    /// so the test is skipped on machines without one.
    /// </summary>
    public static string? BigRaw =>
        Environment.GetEnvironmentVariable("GLIMPSE_BIG_RAW") is { Length: > 0 } p && File.Exists(p) ? p : null;
}
