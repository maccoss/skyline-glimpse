namespace Glimpse.Core.Data;

/// <summary>How intensities are presented.</summary>
public enum IntensityUnits
{
    /// <summary>As stored in the file. For Orbitrap and Astral data this is an ion arrival rate (ions/s).</summary>
    AsRecorded,

    /// <summary>
    /// Ion counts: intensity times the injection time in seconds (Skyline's <c>LcPeakIonMetrics</c>:
    /// <c>intensity * InjectionTime / 1000</c> with the time in ms), divided by the analyzer's α when the
    /// calibration table knows it (<see cref="IonCalibration"/>).
    /// </summary>
    Ions,
}

public static class IntensityConversion
{
    /// <summary>
    /// Convert one intensity to <paramref name="units"/> using the scan's injection time. Returns NaN
    /// when ions are requested and the scan has no injection time, so the point drops out of a plot
    /// instead of being drawn with an invented weight.
    /// </summary>
    public static double Convert(double intensity, float injectionTimeMs, IntensityUnits units)
    {
        if (units == IntensityUnits.AsRecorded)
            return intensity;
        if (float.IsNaN(injectionTimeMs) || injectionTimeMs <= 0)
            return double.NaN;
        return intensity * injectionTimeMs / 1000.0;
    }

    /// <summary>
    /// <see cref="Convert(double, float, IntensityUnits)"/> for one scan, with its analyzer's α applied to
    /// ions when <paramref name="calibration"/> knows it.
    /// </summary>
    public static double Convert(double intensity, in ScanHeader scan, IntensityUnits units, IonCalibration? calibration)
    {
        var value = Convert(intensity, scan.InjectionTimeMs, units);
        return units == IntensityUnits.Ions && calibration is not null ? value / calibration.Divisor(scan.Analyzer) : value;
    }

    public static string Label(IntensityUnits units) => units switch
    {
        IntensityUnits.Ions => "Ions",
        _ => "Intensity",
    };
}
