namespace Glimpse.Features.Hardklor;

/// <summary>
/// The mass analyzer whose peak width Hardklor assumes (its <c>instrument</c> key). Only the peak-width
/// formula depends on it; see <see cref="HardklorSettings.PeakWidth"/>.
/// </summary>
public enum HardklorInstrument
{
    /// <summary>FWHM = m/z^1.5 / (20 * resolution): resolution falls with the square root of m/z.</summary>
    Orbitrap,

    /// <summary>FWHM = m/z / resolution: constant resolution.</summary>
    Tof,

    /// <summary>FWHM = resolution / 5000: constant peak width.</summary>
    Qit,

    /// <summary>FWHM = m/z^2 / (400 * resolution): resolution falls with m/z.</summary>
    Fticr,
}

/// <summary>
/// Settings of <see cref="HardklorFinder"/>. The defaults are the configuration Skyline's feature detection
/// writes for Hardklor (pwiz_tools/Skyline/Model/DdaSearch/HardklorSearchEngine.cs): algorithm Version2,
/// charge_algorithm Quick, charge 1 to 7, correlation from Skyline's default minimum idotp of 0.9, depth 2,
/// averagine_mod 0, molecule_max_mz 5000, distribution_area 1, centroided input.
/// </summary>
/// <remarks>
/// Skyline's other keys have no effect on the Version2 algorithm and have no setting here:
/// signal_to_noise, sn_window, static_sn, sensitivity, mz_window and max_features are read only by the
/// Version1 algorithm; smooth, boxcar averaging and mz_min/mz_max are 0 (off); centroided input means no
/// centroiding step.
/// </remarks>
public sealed record HardklorSettings
{
    /// <summary>Skyline's default minimum idotp (normalized contrast angle) for feature detection.</summary>
    public const double SkylineDefaultIdotP = 0.9;

    /// <summary>The settings Skyline uses by default, for an Orbitrap at 60,000 resolution.</summary>
    public static HardklorSettings Default { get; } = new();

    /// <summary>The analyzer, which sets the peak width (Hardklor's <c>instrument</c>).</summary>
    public HardklorInstrument Instrument { get; init; } = HardklorInstrument.Orbitrap;

    /// <summary>
    /// Resolution at m/z 400 (Hardklor's <c>resolution</c>). For <see cref="HardklorInstrument.Qit"/> this is
    /// the value Hardklor divides by 5000 to get the peak width.
    /// </summary>
    public double Resolution { get; init; } = 60_000;

    /// <summary>Lowest charge considered (<c>charge_min</c>).</summary>
    public int MinCharge { get; init; } = 1;

    /// <summary>Highest charge considered (<c>charge_max</c>; Skyline's HardklorSearchEngine.MaxCharge).</summary>
    public int MaxCharge { get; init; } = 7;

    /// <summary>
    /// Cosine correlation an envelope must exceed (<c>correlation</c>). Skyline converts its minimum idotp
    /// with <see cref="CorrelationFromIdotP"/>: 0.9 becomes 0.98769.
    /// </summary>
    public double Correlation { get; init; } = CorrelationFromIdotP(SkylineDefaultIdotP);

    /// <summary>Most envelopes kept with base peaks within 0.5 m/z of each other (<c>depth</c>).</summary>
    public int Depth { get; init; } = 2;

    /// <summary>Highest m/z of a model envelope (<c>molecule_max_mz</c>); peaks above it are not modelled.</summary>
    public double MaxMoleculeMz { get; init; } = 5000;

    /// <summary>
    /// When true (<c>distribution_area = 1</c>, Skyline's choice) an envelope's intensity is its base peak
    /// times the model's summed relative abundance; when false, the base peak's intensity alone.
    /// </summary>
    public bool DistributionArea { get; init; } = true;

    /// <summary>
    /// Skyline's conversion from a normalized contrast angle (its idotp) to Hardklor's cosine correlation,
    /// cos((1 - idotp) * pi / 2) (ImportPeptideSearch.HardklorSettings.CosineAngleFromNormalizedContrastAngle).
    /// </summary>
    public static double CorrelationFromIdotP(double normalizedContrastAngle) =>
        Math.Cos((1 - Math.Min(1.0, normalizedContrastAngle)) * Math.PI / 2);

    /// <summary>
    /// Expected full width at half maximum of a peak at <paramref name="mz"/> (CHardklor2::CalcFWHM).
    /// </summary>
    public double PeakWidth(double mz) => Instrument switch
    {
        HardklorInstrument.Orbitrap => mz * Math.Sqrt(mz) / (20 * Resolution),
        HardklorInstrument.Tof => mz / Resolution,
        HardklorInstrument.Qit => Resolution / 5000.0,
        _ => mz * mz / (400 * Resolution),
    };

    internal void Validate()
    {
        if (MinCharge < 1 || MaxCharge < MinCharge || MaxCharge > 100)
            throw new ArgumentException($"Charge range {MinCharge}..{MaxCharge} must lie within 1..100.");
        if (!(Resolution > 0))
            throw new ArgumentException($"Resolution {Resolution} must be positive.");
        if (Depth < 1)
            throw new ArgumentException($"Depth {Depth} must be at least 1.");
        if (!(MaxMoleculeMz >= 5))
            throw new ArgumentException($"MaxMoleculeMz {MaxMoleculeMz} must be at least 5.");
    }
}
