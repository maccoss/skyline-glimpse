using System.Text.Json;
using System.Text.Json.Serialization;
using Glimpse.Core.Data;

namespace Glimpse.Plotting;

/// <summary>
/// The sizes Glimpse draws plots with, in points or device-independent pixels at the reference plot size
/// (larger plots scale them up, see <see cref="FigureStyle.SizeScale"/>). The defaults are chosen so a
/// screenshot can go into a figure: legible text and heavy lines that survive being shrunk to a column.
/// </summary>
public sealed record FigureSettings
{
    public static FigureSettings Default { get; } = new();

    /// <summary>Smallest and largest font size a setting may take.</summary>
    public const float MinFontSize = 6, MaxFontSize = 48;

    /// <summary>Smallest and largest line width or tick length a setting may take.</summary>
    public const float MinLine = 0.5f, MaxLine = 20;

    public float AxisLabelFontSize { get; init; } = 18;
    public float TickLabelFontSize { get; init; } = 16;
    public float LegendFontSize { get; init; } = 15;
    public float PeakLabelFontSize { get; init; } = 14;

    /// <summary>The axis frame.</summary>
    public float FrameWidth { get; init; } = 2;
    public float MajorTickLength { get; init; } = 8;
    public float MajorTickWidth { get; init; } = 2;
    public float MinorTickLength { get; init; } = 4;
    public float MinorTickWidth { get; init; } = 1.5f;

    /// <summary>TIC, base peak and assigned-signal traces.</summary>
    public float TraceWidth { get; init; } = 2;

    /// <summary>XIC traces when a few are drawn (thinner for hundreds, see <see cref="FigureStyle.XicWidth"/>).</summary>
    public float XicWidth { get; init; } = 2.5f;

    /// <summary>Spectrum sticks of unassigned peaks.</summary>
    public float StickWidth { get; init; } = 1.5f;

    /// <summary>Spectrum sticks of peaks assigned to a peptide.</summary>
    public float AssignedStickWidth { get; init; } = 2.5f;

    /// <summary>Grow text and lines with the plot (a maximized window shows the same figure scaled up).</summary>
    public bool ScaleWithPlotSize { get; init; } = true;

    /// <summary>
    /// These settings with every size brought into range, and a missing or unreadable size (NaN, from a
    /// hand-edited file) back to its default.
    /// </summary>
    public FigureSettings Validated()
    {
        var d = Default;
        static float Clamp(float v, float fallback, float min, float max) => float.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
        float Font(float v, float fallback) => Clamp(v, fallback, MinFontSize, MaxFontSize);
        float Line(float v, float fallback) => Clamp(v, fallback, MinLine, MaxLine);
        return this with
        {
            AxisLabelFontSize = Font(AxisLabelFontSize, d.AxisLabelFontSize),
            TickLabelFontSize = Font(TickLabelFontSize, d.TickLabelFontSize),
            LegendFontSize = Font(LegendFontSize, d.LegendFontSize),
            PeakLabelFontSize = Font(PeakLabelFontSize, d.PeakLabelFontSize),
            FrameWidth = Line(FrameWidth, d.FrameWidth),
            MajorTickLength = Line(MajorTickLength, d.MajorTickLength),
            MajorTickWidth = Line(MajorTickWidth, d.MajorTickWidth),
            MinorTickLength = Line(MinorTickLength, d.MinorTickLength),
            MinorTickWidth = Line(MinorTickWidth, d.MinorTickWidth),
            TraceWidth = Line(TraceWidth, d.TraceWidth),
            XicWidth = Line(XicWidth, d.XicWidth),
            StickWidth = Line(StickWidth, d.StickWidth),
            AssignedStickWidth = Line(AssignedStickWidth, d.AssignedStickWidth),
        };
    }
}

/// <summary>
/// Everything Glimpse keeps between runs: the figure sizes and the ion calibration table. Saved as JSON in
/// %LOCALAPPDATA%\SkylineGlimpse\settings.json.
/// </summary>
public sealed record GlimpseSettings
{
    public static GlimpseSettings Default { get; } = new();

    public FigureSettings Figures { get; init; } = FigureSettings.Default;

    /// <summary>α per instrument model and analyzer, starting from Hsu et al. 2025 (Table 1).</summary>
    public IReadOnlyList<AlphaEntry> IonCalibration { get; init; } = IonCalibrationTable.Published;

    /// <summary>
    /// Lowest charge of an MS1 isotope envelope that counts as peptide-like: 1 counts every envelope, 2 only
    /// multiply charged ones (most persistent 1+ envelopes are contaminants). MS2 counts every charge.
    /// </summary>
    public int PeptideLikeMs1MinCharge { get; init; } = 1;

    /// <summary>Decimal places of the m/z in a spectrum's peptide-like envelope labels ("542.29 2+").</summary>
    public int PeptideLikeLabelDecimals { get; init; } = 2;

    /// <summary>The most decimal places an envelope label may show.</summary>
    public const int MaxLabelDecimals = 6;

    /// <summary>Hardklör: the highest charge state an isotope envelope is tried at (Skyline's setting: 7).</summary>
    public int HardklorMaxCharge { get; init; } = 7;

    /// <summary>Kronik: the largest mass difference, in ppm, between envelopes linked across scans.</summary>
    public double KronikPpmTolerance { get; init; } = 10;

    /// <summary>Kronik: scans in a row that may miss an envelope before its feature ends.</summary>
    public int KronikGapTolerance { get; init; } = 1;

    /// <summary>Kronik: the fewest scans an envelope must be found in to be peptide-like.</summary>
    public int KronikMinScans { get; init; } = 3;

    /// <summary>Ranges the search settings are kept in.</summary>
    public const int MaxHardklorCharge = 10, MaxGapTolerance = 5, MaxMinScans = 20;

    public const double MinPpm = 0.1, MaxPpm = 100;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkylineGlimpse", "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The settings saved at <paramref name="path"/>, brought into range; the defaults when there is no
    /// file or it cannot be read. An unreadable file is copied to <c>settings.json.unreadable</c>, so the next
    /// save does not lose it, and <paramref name="onProblem"/> hears why.
    /// </summary>
    public static GlimpseSettings Load(string path, Action<string>? onProblem = null)
    {
        try
        {
            if (!File.Exists(path))
                return Default;
            var loaded = JsonSerializer.Deserialize<GlimpseSettings>(File.ReadAllText(path), Json);
            return loaded is null ? Default : loaded.Validated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            var kept = UnreadablePath(path);
            try
            {
                File.Copy(path, kept, overwrite: true);
            }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException)
            {
                kept = null;
            }
            onProblem?.Invoke($"Could not read {Path.GetFileName(path)} ({ex.Message}), so Glimpse uses the default settings."
                + (kept is null ? "" : $" The file was kept as {Path.GetFileName(kept)}."));
            return Default;
        }
    }

    /// <summary>Where <see cref="Load"/> keeps a copy of a settings file it could not read.</summary>
    public static string UnreadablePath(string path) => path + ".unreadable";

    /// <summary>Write the settings to <paramref name="path"/> (via a temporary file, so a crash never leaves half a file).</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// These settings in range: figure sizes clamped, and calibration rows without an instrument name or a
    /// positive, finite α dropped. A file without a table (null) gets the published one.
    /// </summary>
    public GlimpseSettings Validated() => this with
    {
        Figures = (Figures ?? FigureSettings.Default).Validated(),
        PeptideLikeMs1MinCharge = Math.Clamp(PeptideLikeMs1MinCharge, 1, 2),
        PeptideLikeLabelDecimals = Math.Clamp(PeptideLikeLabelDecimals, 0, MaxLabelDecimals),
        HardklorMaxCharge = Math.Clamp(HardklorMaxCharge, 1, MaxHardklorCharge),
        KronikPpmTolerance = double.IsFinite(KronikPpmTolerance) ? Math.Clamp(KronikPpmTolerance, MinPpm, MaxPpm) : 10,
        KronikGapTolerance = Math.Clamp(KronikGapTolerance, 0, MaxGapTolerance),
        KronikMinScans = Math.Clamp(KronikMinScans, 1, MaxMinScans),
        IonCalibration = IonCalibration is null
            ? IonCalibrationTable.Published
            : IonCalibration.Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Instrument) && double.IsFinite(e.Alpha) && e.Alpha > 0)
                .Select(e => e with { Instrument = e.Instrument.Trim() })
                .ToList(),
    };
}
