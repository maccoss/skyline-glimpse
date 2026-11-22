using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace Glimpse.Tests;

/// <summary>
/// Instrument files the tests read, which live outside this repository. Each is taken from the local
/// ProteoWizard checkout when it is there, else from a download cache, else downloaded into the cache:
/// pwiz's small test files from GitHub (public, no credentials), larger runs from Panorama through the
/// user's netrc. Downloads are checked against the size and SHA-256 recorded here.
/// </summary>
/// <remarks>
/// The cache is <c>%LOCALAPPDATA%\SkylineGlimpse\test-data</c>, or the GLIMPSE_TEST_DATA environment variable.
/// </remarks>
internal static class TestData
{
    /// <summary>
    /// Trait category of the tests that download from Panorama. CI has no Panorama credentials, so it runs
    /// <c>dotnet test --filter "Category!=PanoramaData"</c>.
    /// </summary>
    public const string PanoramaData = "PanoramaData";

    /// <summary>The pwiz commit the GitHub downloads come from: the one CI pins in .github/actions/checkout-pwiz-sharp.</summary>
    private const string PwizCommit = "52acb7fd79baa0bd046899f48ca422ca9ab6e87d";

    private const string PanoramaFiles = "https://panoramaweb.org/_webdav/MacCoss/DIA-Search-Test-Data/@files/";

    private static readonly object DownloadLock = new();

    /// <summary>pwiz's Thermo reader test file BSA-FT-HCD.raw (MS2 only, HCD).</summary>
    public static string BsaHcdRaw => PwizFile("pwiz/data/vendor_readers/Thermo/Reader_Thermo_Test.data/BSA-FT-HCD.raw",
        351_135, "45DBAEA253856F3FE02C71EBF4ABC747874F3FE6E3214EF0521809062164E381");

    /// <summary>pwiz's Thermo reader test file BSA-FT-ETD.raw (MS2 only, ETD).</summary>
    public static string BsaEtdRaw => PwizFile("pwiz/data/vendor_readers/Thermo/Reader_Thermo_Test.data/BSA-FT-ETD.raw",
        348_555, "D63A86285709D16115B7CC13453652B72589D6D428ADC912DF9B0BA6063190B3");

    /// <summary>pwiz example_data/small.RAW.</summary>
    public static string SmallRaw => PwizFile("example_data/small.RAW",
        1_504_354, "071F845DEC57B5FE3A6023326ACBA6B179D03EAE4A8E86DBA416B8B50EC42E63");

    /// <summary>pwiz example_data/small.pwiz.1.1.mzML: small.RAW converted by msconvert.</summary>
    public static string SmallMzml => PwizFile("example_data/small.pwiz.1.1.mzML",
        5_103_183, "87DDDE776C9933857EDB22F404116F23E85CA8BDDADC84D10308EB8853A58768");

    /// <summary>
    /// A Stellar HeLa DIA run (4 m/z windows, 400-900, centroided mzML, 1.5 GB) from the MacCoss lab's
    /// DIA-Search-Test-Data folder on Panorama. Null when it is not cached and cannot be downloaded (no
    /// netrc entry for panoramaweb.org, or no access to the folder).
    /// </summary>
    public static string? StellarHela => PanoramaFile("RawFiles/Stellar-HeLa/Ste-2024-12-02_HeLa_4mz_sDIA_400-900_20.mzML",
        1_486_391_329, "D2877014B494E4EE568BF174C9A3E64C0B79A6F5DAF80DDC53DC95A720762195");

    /// <summary>The repository root (the folder holding Glimpse.sln).</summary>
    public static string RepoRoot => Metadata("RepoRoot");

    /// <summary>
    /// A large real-world run for timing, from the GLIMPSE_BIG_RAW environment variable; null when unset
    /// so the test is skipped on machines without one.
    /// </summary>
    public static string? BigRaw =>
        Environment.GetEnvironmentVariable("GLIMPSE_BIG_RAW") is { Length: > 0 } p && File.Exists(p) ? p : null;

    /// <summary>Where downloads are kept.</summary>
    public static string CacheDir =>
        Environment.GetEnvironmentVariable("GLIMPSE_TEST_DATA") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkylineGlimpse", "test-data");

    private static string Metadata(string key) =>
        typeof(TestData).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value ?? "";

    private static string PwizFile(string repoPath, long size, string sha256)
    {
        var local = Path.Combine(Metadata("PwizRepoDir"), repoPath);
        if (File.Exists(local))
            return local;
        var cached = Path.Combine(CacheDir, "pwiz", repoPath);
        var url = $"https://raw.githubusercontent.com/ProteoWizard/pwiz/{PwizCommit}/{repoPath}";
        return Fetch(cached, url, size, sha256, netrc: false)
            ?? throw new InvalidOperationException($"Could not download {url} (see the curl message in the test output).");
    }

    private static string? PanoramaFile(string path, long size, string sha256) =>
        Fetch(Path.Combine(CacheDir, "panorama", path), PanoramaFiles + path, size, sha256, netrc: true);

    /// <summary>
    /// The cached file, downloading it first when missing. A download goes to a .part file that a later
    /// call resumes, and becomes the cached file only once its size and SHA-256 match. Null when curl fails.
    /// </summary>
    private static string? Fetch(string path, string url, long size, string sha256, bool netrc)
    {
        lock (DownloadLock)
        {
            if (File.Exists(path) && new FileInfo(path).Length == size)
                return path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var part = path + ".part";
            if (!File.Exists(part) || new FileInfo(part).Length < size)
            {
                // curl reads the netrc itself (-n): the tests never see the credentials.
                var start = new ProcessStartInfo("curl") { RedirectStandardError = true, UseShellExecute = false };
                foreach (var arg in new[] { "-sS", "-f", "-L", "--retry", "3", "-C", "-", "-o", part, url })
                    start.ArgumentList.Add(arg);
                if (netrc)
                    start.ArgumentList.Insert(0, "-n");
                using var curl = Process.Start(start)!;
                var error = curl.StandardError.ReadToEnd();
                curl.WaitForExit();
                if (curl.ExitCode != 0)
                {
                    Console.Error.WriteLine($"curl {url}: exit {curl.ExitCode} {error.Trim()}");
                    return null;
                }
            }
            var length = new FileInfo(part).Length;
            var hash = HashFile(part);
            if (length != size || (sha256.Length > 0 && !hash.Equals(sha256, StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(part);
                throw new InvalidDataException($"{url} downloaded as {length:N0} bytes, SHA-256 {hash}; expected {size:N0} bytes, {sha256}.");
            }
            File.Move(part, path, overwrite: true);
            return path;
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
