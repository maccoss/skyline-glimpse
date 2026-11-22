using System.Globalization;

namespace Glimpse.Core.Data;

/// <summary>The mass analyzer that recorded a scan, as far as ion calibration needs to know.</summary>
public enum MassAnalyzer : byte
{
    Unknown = 0,
    Orbitrap = 1,

    /// <summary>The Astral analyzer (asymmetric track lossless time-of-flight).</summary>
    Astral = 2,

    /// <summary>Any ion trap analyzer (linear, radial or axial ejection, quadrupole ion trap).</summary>
    IonTrap = 3,

    Other = 4,
}

/// <summary>One row of the calibration table: the α of one analyzer on one instrument model.</summary>
/// <param name="Instrument">Instrument model as the data file names it ("Orbitrap Astral", "Stellar").</param>
/// <param name="Analyzer">The analyzer the α applies to.</param>
/// <param name="Alpha">Signal per ion: ions = intensity x injection time / α.</param>
public sealed record AlphaEntry(string Instrument, MassAnalyzer Analyzer, double Alpha);

/// <summary>
/// The α factors of one run's analyzers, from Hsu et al., J. Proteome Res. 2025, 24, 5742 (eq 2):
/// intensity I = α N / t, so the calibrated ion count is N = I t / α. α is about 1 for the Astral and
/// ion trap analyzers and about 10 for Orbitraps. NaN for an analyzer the table has no α for: its ions
/// stay uncalibrated (I t).
/// </summary>
public sealed record IonCalibration(double Orbitrap, double Astral, double IonTrap)
{
    /// <summary>No α known: ions are intensity x injection time, Skyline's LcPeakIonMetrics convention.</summary>
    public static IonCalibration Uncalibrated { get; } = new(double.NaN, double.NaN, double.NaN);

    /// <summary>The α for scans from <paramref name="analyzer"/>, NaN when unknown.</summary>
    public double AlphaFor(MassAnalyzer analyzer) => analyzer switch
    {
        MassAnalyzer.Orbitrap => Orbitrap,
        MassAnalyzer.Astral => Astral,
        MassAnalyzer.IonTrap => IonTrap,
        _ => double.NaN,
    };

    /// <summary>True when scans from <paramref name="analyzer"/> are converted with a known α.</summary>
    public bool IsCalibrated(MassAnalyzer analyzer) => AlphaFor(analyzer) is var a && double.IsFinite(a) && a > 0;

    /// <summary>What uncalibrated ions (I t) are divided by: α when known, else 1.</summary>
    public double Divisor(MassAnalyzer analyzer) => IsCalibrated(analyzer) ? AlphaFor(analyzer) : 1;

    /// <summary>True when any analyzer has an α.</summary>
    public bool Any => IsCalibrated(MassAnalyzer.Orbitrap) || IsCalibrated(MassAnalyzer.Astral) || IsCalibrated(MassAnalyzer.IonTrap);

    /// <summary>
    /// The α factors the table gives <paramref name="instrumentModel"/> (matched ignoring case and
    /// surrounding spaces). Uncalibrated when the model is unknown or not in the table.
    /// </summary>
    public static IonCalibration For(string? instrumentModel, IEnumerable<AlphaEntry> table)
    {
        if (string.IsNullOrWhiteSpace(instrumentModel))
            return Uncalibrated;
        var model = instrumentModel.Trim();
        double Find(MassAnalyzer analyzer) => table
            .Where(e => e.Analyzer == analyzer && string.Equals(e.Instrument.Trim(), model, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Alpha)
            .Where(a => double.IsFinite(a) && a > 0)
            .DefaultIfEmpty(double.NaN)
            .First();
        return new IonCalibration(Find(MassAnalyzer.Orbitrap), Find(MassAnalyzer.Astral), Find(MassAnalyzer.IonTrap));
    }

    /// <summary>"Orbitrap α 10.62, Astral α 1.53", or null when uncalibrated.</summary>
    public string? Describe()
    {
        var parts = new List<string>();
        void Add(string name, double a)
        {
            if (double.IsFinite(a) && a > 0)
                parts.Add($"{name} α {a.ToString("0.##", CultureInfo.InvariantCulture)}");
        }
        Add("Orbitrap", Orbitrap);
        Add("Astral", Astral);
        Add("ion trap", IonTrap);
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}

/// <summary>The α factors measured by Hsu et al. (J. Proteome Res. 2025, 24, 5742, Table 1).</summary>
public static class IonCalibrationTable
{
    /// <summary>
    /// The first row whose instrument (matched as <see cref="IonCalibration.For"/> matches it) and analyzer an
    /// earlier row already has, or null. <see cref="IonCalibration.For"/> would use only the earlier one.
    /// </summary>
    public static AlphaEntry? FirstDuplicate(IEnumerable<AlphaEntry> table)
    {
        var seen = new HashSet<(string, MassAnalyzer)>();
        foreach (var e in table)
        {
            if (!seen.Add((e.Instrument.Trim().ToUpperInvariant(), e.Analyzer)))
                return e;
        }
        return null;
    }

    /// <summary>
    /// Table 1, with the instrument names the data files use. The paper's Astral Zoom was a prototype that
    /// is, in effect, the commercial Orbitrap Astral Zoom; its "Orbitrap Fusion" is the Fusion Lumos Tribrid.
    /// </summary>
    public static IReadOnlyList<AlphaEntry> Published { get; } = new[]
    {
        new AlphaEntry("Orbitrap Astral Zoom", MassAnalyzer.Astral, 1.31),
        new AlphaEntry("Orbitrap Astral", MassAnalyzer.Astral, 1.53),
        new AlphaEntry("Orbitrap Astral Zoom", MassAnalyzer.Orbitrap, 10.30),
        new AlphaEntry("Orbitrap Astral", MassAnalyzer.Orbitrap, 10.62),
        new AlphaEntry("Orbitrap Exploris 480", MassAnalyzer.Orbitrap, 10.95),
        new AlphaEntry("Orbitrap Ascend", MassAnalyzer.Orbitrap, 9.40),
        new AlphaEntry("Orbitrap Fusion Lumos", MassAnalyzer.Orbitrap, 15.44),
        new AlphaEntry("Stellar", MassAnalyzer.IonTrap, 0.97),
        new AlphaEntry("Orbitrap Ascend", MassAnalyzer.IonTrap, 0.98),
        new AlphaEntry("Orbitrap Fusion Lumos", MassAnalyzer.IonTrap, 1.09),
    };
}
