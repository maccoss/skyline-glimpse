using System.Text.RegularExpressions;
using Xunit;

namespace Glimpse.Tests;

/// <summary>
/// The release version lives in two places that Skyline and .NET each read: Directory.Build.props (every
/// assembly) and the tool manifest tool-inf/info.properties (what Skyline's Tool Store shows and uses to
/// decide whether an install is an upgrade). They must agree, and both follow the Skyline-PRISM scheme.
/// </summary>
public class VersionTests
{
    private static string PropsVersion()
    {
        var text = File.ReadAllText(Path.Combine(TestData.RepoRoot, "Directory.Build.props"));
        return Regex.Match(text, @"<Version>([^<]*)</Version>").Groups[1].Value.Trim();
    }

    private static string ManifestVersion()
    {
        var lines = File.ReadAllLines(Path.Combine(TestData.RepoRoot, "src", "Glimpse.App", "tool-inf", "info.properties"));
        return lines.Single(l => l.StartsWith("Version", StringComparison.Ordinal)).Split('=', 2)[1].Trim();
    }

    [Fact]
    public void ToolManifestAndAssembliesCarryTheSameVersion()
    {
        Assert.Equal(PropsVersion(), ManifestVersion());
        // And that is the version the built assemblies actually carry.
        var built = typeof(Glimpse.Core.Data.ScanHeader).Assembly.GetName().Version!;
        Assert.Equal(PropsVersion() + ".0", built.ToString());
    }

    [Fact]
    public void TheSolutionRunsTheTests()
    {
        // package-and-verify.ps1 and CLAUDE.md test through Glimpse.sln. Without the test project in it,
        // "dotnet test Glimpse.sln" succeeds having run nothing, and the release gate passes untested.
        var solution = File.ReadAllText(Path.Combine(TestData.RepoRoot, "Glimpse.sln"));
        Assert.Contains(@"tests\Glimpse.Tests\Glimpse.Tests.csproj", solution);
    }

    [Fact]
    public void VersionIsCalVerYearFeaturePatch()
    {
        // YY.feature.patch, e.g. 26.1.0: a two-digit year from 2026 on, then the feature and patch counters.
        var match = Regex.Match(PropsVersion(), @"^(\d{2})\.(\d+)\.(\d+)$");
        Assert.True(match.Success, $"'{PropsVersion()}' is not YY.feature.patch");
        Assert.InRange(int.Parse(match.Groups[1].Value), 26, 99);
        Assert.True(int.Parse(match.Groups[2].Value) >= 1, "the feature counter starts at 1");
    }
}
