using System.Collections.Concurrent;
using Glimpse.Core.Data;
using Glimpse.Features.Hardklor;

namespace Glimpse.Features;

/// <summary>
/// Hardklör with the peak width of the analyzer that recorded each scan: one run can mix them (an Orbitrap
/// Astral's MS1 in the Orbitrap, MS2 in the Astral; a Tribrid's MS2 in the ion trap).
/// </summary>
public sealed class AnalyzerEnvelopeFinder : IEnvelopeFinder
{
    private readonly IEnvelopeModel _model;
    private readonly ConcurrentDictionary<MassAnalyzer, HardklorFinder> _finders = new();

    public AnalyzerEnvelopeFinder(IEnvelopeModel model) => _model = model;

    /// <summary>
    /// Hardklör's settings for <paramref name="analyzer"/>: Skyline's (Orbitrap model, 60,000 at m/z 400) for an
    /// Orbitrap or an analyzer Glimpse does not know; the Astral at a constant 80,000 (time-of-flight model);
    /// an ion trap with 0.5 m/z peaks.
    /// </summary>
    public static HardklorSettings SettingsFor(MassAnalyzer analyzer) => analyzer switch
    {
        MassAnalyzer.Astral => HardklorSettings.Default with { Instrument = HardklorInstrument.Tof, Resolution = 80000 },
        MassAnalyzer.IonTrap => HardklorSettings.Default with { Instrument = HardklorInstrument.Qit, Resolution = 2500 },
        _ => HardklorSettings.Default,
    };

    public IReadOnlyList<IsotopeEnvelope> Find(double[] mz, double[] intensity, MassAnalyzer analyzer) =>
        _finders.GetOrAdd(Key(analyzer), a => new HardklorFinder(SettingsFor(a), _model)).Find(mz, intensity);

    /// <summary>Analyzers with the same settings share one finder (and its model library).</summary>
    private static MassAnalyzer Key(MassAnalyzer analyzer) =>
        analyzer is MassAnalyzer.Astral or MassAnalyzer.IonTrap ? analyzer : MassAnalyzer.Orbitrap;
}
