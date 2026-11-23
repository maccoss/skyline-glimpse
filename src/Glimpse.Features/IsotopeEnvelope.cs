namespace Glimpse.Features;

/// <summary>
/// One isotope envelope found in one spectrum: a set of peaks spaced by 1.00335/z that match the isotope
/// distribution of an averagine peptide of <see cref="MonoMass"/> and <see cref="Charge"/>.
/// </summary>
/// <param name="MonoMass">Neutral monoisotopic mass (Da).</param>
/// <param name="Charge">Charge state (1 or more).</param>
/// <param name="Intensity">The envelope's intensity as Hardklör reports it.</param>
/// <param name="BasePeakMz">m/z of the envelope's most intense isotope peak.</param>
/// <param name="Correlation">How well the observed peaks match the model distribution (0 to 1).</param>
/// <param name="LowMz">Lowest m/z of the envelope.</param>
/// <param name="HighMz">Highest m/z of the envelope.</param>
/// <param name="PeakIndices">Indices into the spectrum's m/z and intensity arrays of the peaks the envelope
/// matched, ascending. Peaks can belong to more than one envelope.</param>
public sealed record IsotopeEnvelope(
    double MonoMass,
    int Charge,
    float Intensity,
    double BasePeakMz,
    double Correlation,
    double LowMz,
    double HighMz,
    int[] PeakIndices);

/// <summary>An elemental composition: the atoms an averagine model of a peptide is made of.</summary>
public readonly record struct ElementCounts(int C, int H, int N, int O, int S);

/// <summary>One isotope peak of a distribution: its mass and its abundance.</summary>
public readonly record struct IsotopePeak(double Mass, double Abundance);

/// <summary>
/// Isotope distributions of elemental compositions. Glimpse.Pwiz implements it with pwiz's isotope
/// calculator, so this project needs no pwiz reference.
/// </summary>
public interface IEnvelopeModel
{
    /// <summary>
    /// The isotope peaks of <paramref name="formula"/> (neutral masses, ascending; abundances relative to the
    /// most abundant peak = 1), dropping peaks below <paramref name="abundanceCutoff"/> of the most abundant.
    /// </summary>
    IReadOnlyList<IsotopePeak> Distribution(ElementCounts formula, double abundanceCutoff);
}
