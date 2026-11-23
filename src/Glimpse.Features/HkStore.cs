using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Glimpse.Core.Data;
using Glimpse.Features.Hardklor;

namespace Glimpse.Features;

/// <summary>
/// Where a run's .hk file is kept: beside the raw file (<c>run.hk</c>), or, when that folder cannot be written
/// (a read-only share), in Glimpse's own cache folder.
/// </summary>
public sealed class HkStore
{
    private readonly string _cacheDir;

    /// <param name="cacheDir">Glimpse's cache folder, for runs whose own folder cannot be written.</param>
    public HkStore(string cacheDir) => _cacheDir = cacheDir;

    /// <summary>The cache-folder file of a run: its name and a hash of its full path, so runs of the same name differ.</summary>
    public string CachePath(string rawPath)
    {
        var full = Path.GetFullPath(rawPath.TrimEnd('\\', '/'));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..16];
        return Path.Combine(_cacheDir, $"{Path.GetFileNameWithoutExtension(full)}-{hash}.hk");
    }

    /// <summary>The .hk file made from <paramref name="stamp"/>, beside the run or in the cache; null when neither is.</summary>
    public string? FindUsable(string rawPath, HkFile.Stamp stamp) =>
        new[] { HkFile.PathBeside(rawPath), CachePath(rawPath) }.FirstOrDefault(p => HkFile.Matches(p, stamp));

    /// <summary>Save the run's signal beside it, else in the cache. Returns where it went.</summary>
    public string Save(string rawPath, PeptideLikeSignal signal, HkFile.Stamp stamp)
    {
        var beside = HkFile.PathBeside(rawPath);
        try
        {
            HkFile.Write(beside, signal, stamp);
            return beside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(beside + ".tmp");
            var cache = CachePath(rawPath);
            HkFile.Write(cache, signal, stamp);
            return cache;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; the next save replaces it.
        }
    }

    /// <summary>
    /// The settings a search depends on, as text for <see cref="HkFile.Stamp.Settings"/>: the isotope model
    /// (<paramref name="model"/>), Hardklör's settings for each analyzer searched, Kronik's linking, the DIA rule
    /// and whether peaks were vendor-centroided. Any change makes earlier .hk files stale.
    /// </summary>
    public static string SearchSettings(string model, bool centroid) => SearchSettings(model, centroid, HardklorSettings.Default.MaxCharge, KronikSettings.Default);

    /// <summary>The settings text for a search of charges 1 to <paramref name="maxCharge"/>, linked with <paramref name="kronik"/>.</summary>
    public static string SearchSettings(string model, bool centroid, int maxCharge, KronikSettings kronik)
    {
        static string Of(HardklorSettings? s) => s is null ? "none" : string.Create(CultureInfo.InvariantCulture,
            $"{s.Instrument} {s.Resolution} z{s.MinCharge}-{s.MaxCharge} r{s.Correlation:R} d{s.Depth} max{s.MaxMoleculeMz} area{s.DistributionArea}");
        var k = kronik;
        return string.Create(CultureInfo.InvariantCulture,
            $"model {model}; orbitrap {Of(AnalyzerEnvelopeFinder.SettingsFor(MassAnalyzer.Orbitrap, maxCharge))}; "
            + $"astral {Of(AnalyzerEnvelopeFinder.SettingsFor(MassAnalyzer.Astral, maxCharge))}; iontrap {Of(AnalyzerEnvelopeFinder.SettingsFor(MassAnalyzer.IonTrap, maxCharge))}; "
            + $"kronik {k.PpmTolerance}ppm gap{k.GapTolerance} min{k.MinScans}; dia windows {PeptideLikeObserver.MaxDiaWindows}; centroid {centroid}");
    }
}
