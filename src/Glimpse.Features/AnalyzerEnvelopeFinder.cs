using System.Collections.Concurrent;
using Glimpse.Core.Data;
using Glimpse.Features.Hardklor;

namespace Glimpse.Features;

/// <summary>
/// Hardklör with the peak width of the analyzer that recorded each scan: one run can mix them (an Orbitrap
/// Astral's MS1 in the Orbitrap, MS2 in the Astral). Ion trap scans (a Stellar's, a Tribrid's linear ion trap)
/// are not searched: their resolution cannot separate the isotopes of multiply charged ions.
/// </summary>
public sealed class AnalyzerEnvelopeFinder : IEnvelopeFinder
{
    private readonly IEnvelopeModel _model;
    private readonly ConcurrentDictionary<MassAnalyzer, HardklorFinder> _finders = new();

    public AnalyzerEnvelopeFinder(IEnvelopeModel model) => _model = model;

    /// <summary>
    /// Hardklör's settings for <paramref name="analyzer"/>: Skyline's (Orbitrap model, 60,000 at m/z 400) for an
    /// Orbitrap or an analyzer Glimpse does not know; the Astral at a constant 80,000 (time-of-flight model);
    /// none for an ion trap.
    /// </summary>
    public static HardklorSettings? SettingsFor(MassAnalyzer analyzer) => analyzer switch
    {
        MassAnalyzer.Astral => HardklorSettings.Default with { Instrument = HardklorInstrument.Tof, Resolution = 80000 },
        MassAnalyzer.IonTrap => null,
        _ => HardklorSettings.Default,
    };

    public bool CanSearch(MassAnalyzer analyzer) => SettingsFor(analyzer) is not null;

    public IReadOnlyList<IsotopeEnvelope> Find(double[] mz, double[] intensity, MassAnalyzer analyzer) =>
        CanSearch(analyzer)
            ? _finders.GetOrAdd(Key(analyzer), a => new HardklorFinder(SettingsFor(a)!, _model)).Find(mz, intensity)
            : Array.Empty<IsotopeEnvelope>();

    /// <summary>Analyzers with the same settings share one finder (and its model library).</summary>
    private static MassAnalyzer Key(MassAnalyzer analyzer) => analyzer == MassAnalyzer.Astral ? analyzer : MassAnalyzer.Orbitrap;
}
