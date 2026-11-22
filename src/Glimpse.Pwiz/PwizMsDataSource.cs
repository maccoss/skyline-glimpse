using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Glimpse.Core.Data;
using Pwiz.Data.Common.Cv;
using Pwiz.Data.Common.Params;
using Pwiz.Data.MsData;
using Pwiz.Data.MsData.Readers;
using Pwiz.Data.MsData.Spectra;
using Pwiz.Util.Misc;
using Polarity = Glimpse.Core.Data.Polarity;

namespace Glimpse.Pwiz;

/// <summary>
/// <see cref="IMsDataSource"/> over pwiz-sharp: Thermo .raw, mzML, mzXML and mz5.
/// </summary>
public sealed partial class PwizMsDataSource : IMsDataSource
{
    private readonly MSData _msd;
    private readonly ISpectrumList _spectra;
    private readonly IVendorCentroidingSpectrumList? _centroider;

    // Reused for out-of-order m/z arrays, which some vendor readers deliver.
    private static readonly IntegerSet AllLevels = new(1, int.MaxValue);

    static PwizMsDataSource() => VendorReaders.EnsureRegistered();

    private PwizMsDataSource(string path, MSData msd)
    {
        FilePath = path;
        _msd = msd;
        _spectra = msd.Run.SpectrumList ?? throw new InvalidDataException($"No spectra in {Path.GetFileName(path)}.");
        _centroider = _spectra as IVendorCentroidingSpectrumList;
        Run = ReadRunInfo(path, msd);
    }

    /// <summary>File extensions (and directory suffixes) this build can open.</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } = new[] { ".raw", ".mzml", ".mzxml", ".mz5", ".mzml.gz" };

    /// <summary>Open a data file. Throws with the reader's message when the file cannot be read.</summary>
    public static PwizMsDataSource Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("Data file not found.", path);
        var msd = new MSData();
        try
        {
            // Combine the ion mobility dimension so a TIMS frame is one spectrum, not hundreds.
            ReaderList.Default.Read(path, msd, new ReaderConfig { CombineIonMobilitySpectra = true });
            return new PwizMsDataSource(path, msd);
        }
        catch
        {
            msd.Dispose();
            throw;
        }
    }

    public string FilePath { get; }

    public int SpectrumCount => _spectra.Count;

    public RunInfo Run { get; }

    public bool CanVendorCentroid => _centroider is not null;

    public double ReadRetentionTime(int index)
    {
        var s = _spectra.GetSpectrum(index, DetailLevel.FastMetadata);
        var scan = s.ScanList.Scans.Count > 0 ? s.ScanList.Scans[0] : null;
        return Minutes(scan?.CvParam(CVID.MS_scan_start_time));
    }

    public SpectrumData ReadSpectrum(int index, bool centroid)
    {
        Spectrum s;
        if (centroid && _centroider is not null)
        {
            try
            {
                s = _centroider.GetCentroidSpectrum(index, true, AllLevels);
            }
            catch (Exception)
            {
                // A scan the vendor library cannot centroid (e.g. already centroided in a mixed file)
                // is read as stored rather than lost.
                s = _spectra.GetSpectrum(index, true);
            }
        }
        else
        {
            s = _spectra.GetSpectrum(index, true);
        }

        var (mz, intensity) = PeakArrays(s);
        var header = ReadHeader(index, s, mz, intensity);
        return new SpectrumData(header, mz, intensity, ReadDetails(s));
    }

    public void Dispose() => _msd.Dispose();

    private static (double[] Mz, double[] Intensity) PeakArrays(Spectrum s)
    {
        var mzArray = s.GetMZArray();
        var intArray = s.GetIntensityArray();
        if (mzArray is null || intArray is null || mzArray.Data.Count != intArray.Data.Count)
            return (Array.Empty<double>(), Array.Empty<double>());

        var mz = mzArray.Data.ToArray();
        var intensity = intArray.Data.ToArray();
        // mzML requires ascending m/z, but vendor readers do not always deliver it, and every lookup
        // downstream binary-searches the array.
        for (var i = 1; i < mz.Length; i++)
        {
            if (mz[i] < mz[i - 1])
            {
                Array.Sort(mz, intensity);
                break;
            }
        }
        return (mz, intensity);
    }

    private static ScanHeader ReadHeader(int index, Spectrum s, double[] mz, double[] intensity)
    {
        var level = s.Params.CvParamValueOrDefault(CVID.MS_ms_level, 0);
        var scan = s.ScanList.Scans.Count > 0 ? s.ScanList.Scans[0] : null;
        var rt = Minutes(scan?.CvParam(CVID.MS_scan_start_time));
        var injectionMs = Milliseconds(scan?.CvParam(CVID.MS_ion_injection_time));

        double target = double.NaN, lower = double.NaN, upper = double.NaN;
        if (level >= 2 && s.Precursors.Count > 0)
        {
            var w = s.Precursors[0].IsolationWindow;
            target = w.CvParamValueOrDefault(CVID.MS_isolation_window_target_m_z, double.NaN);
            if (double.IsNaN(target) && s.Precursors[0].SelectedIons.Count > 0)
                target = s.Precursors[0].SelectedIons[0].CvParamValueOrDefault(CVID.MS_selected_ion_m_z, double.NaN);
            var lo = w.CvParamValueOrDefault(CVID.MS_isolation_window_lower_offset, double.NaN);
            var hi = w.CvParamValueOrDefault(CVID.MS_isolation_window_upper_offset, double.NaN);
            if (!double.IsNaN(target) && !double.IsNaN(lo) && !double.IsNaN(hi))
            {
                lower = target - lo;
                upper = target + hi;
            }
        }

        double tic = 0, bpMz = double.NaN, bpI = 0;
        var span = intensity.AsSpan();
        for (var i = 0; i < span.Length; i++)
        {
            tic += span[i];
            if (span[i] > bpI)
            {
                bpI = span[i];
                bpMz = mz[i];
            }
        }

        var polarity = s.HasCVParam(CVID.MS_positive_scan) ? Polarity.Positive
            : s.HasCVParam(CVID.MS_negative_scan) ? Polarity.Negative
            : Polarity.Unknown;

        return new ScanHeader(
            index, (byte)Math.Clamp(level, 0, 255), rt, (float)injectionMs,
            target, lower, upper, tic, bpMz, (float)bpI, mz.Length, polarity,
            s.HasCVParam(CVID.MS_centroid_spectrum));
    }

    private static SpectrumDetails ReadDetails(Spectrum s)
    {
        var scan = s.ScanList.Scans.Count > 0 ? s.ScanList.Scans[0] : null;
        var precursor = s.Precursors.Count > 0 ? s.Precursors[0] : null;
        var selected = precursor is { SelectedIons.Count: > 0 } ? precursor.SelectedIons[0] : null;
        var window = scan is { ScanWindows.Count: > 0 } ? scan.ScanWindows[0] : null;

        string? dissociation = null;
        double? ce = null;
        if (precursor is not null)
        {
            var methods = precursor.Activation.CvParamChildren(CVID.MS_dissociation_method);
            if (methods.Count > 0)
                dissociation = string.Join(" + ", methods.Select(m => Shorten(m.Name)));
            var ceParam = precursor.Activation.CvParam(CVID.MS_collision_energy);
            if (ceParam.Cvid != CVID.CVID_Unknown)
                ce = ceParam.ValueAs<double>();
        }

        string? mobility = null;
        if (scan is not null)
        {
            var im = scan.CvParamChild(CVID.MS_ion_mobility_attribute);
            if (im.Cvid != CVID.CVID_Unknown)
                mobility = $"{im.Value} {im.UnitsName}".Trim() + $" ({im.Name})";
        }

        var other = new List<KeyValuePair<string, string>>();
        if (scan is not null)
        {
            foreach (var p in scan.CVParams)
            {
                if (p.Cvid is CVID.MS_scan_start_time or CVID.MS_ion_injection_time or CVID.MS_filter_string)
                    continue;
                other.Add(new(p.Name, WithUnits(p)));
            }
            foreach (var p in scan.UserParams)
                other.Add(new(p.Name, p.Value));
        }
        foreach (var p in s.Params.UserParams)
            other.Add(new(p.Name, p.Value));

        return new SpectrumDetails
        {
            NativeId = s.Id,
            ScanNumber = ScanNumberFromId(s.Id),
            FilterString = NullIfEmpty(scan?.CvParam(CVID.MS_filter_string).Value),
            Dissociation = dissociation,
            CollisionEnergy = ce,
            SelectedIonMz = Nullable(selected?.CvParamValueOrDefault(CVID.MS_selected_ion_m_z, double.NaN)),
            SelectedIonCharge = selected is null ? null : NullIfZero(selected.CvParamValueOrDefault(CVID.MS_charge_state, 0)),
            ScanWindowLower = Nullable(window?.CvParamValueOrDefault(CVID.MS_scan_window_lower_limit, double.NaN)),
            ScanWindowUpper = Nullable(window?.CvParamValueOrDefault(CVID.MS_scan_window_upper_limit, double.NaN)),
            IonMobility = mobility,
            Other = other,
        };
    }

    private static RunInfo ReadRunInfo(string path, MSData msd)
    {
        string? model = null, serial = null, manufacturer = null;
        var configs = new List<string>();
        foreach (var ic in msd.InstrumentConfigurations)
        {
            var m = ic.CvParamChild(CVID.MS_instrument_model);
            if (m.Cvid != CVID.CVID_Unknown && model is null)
            {
                model = m.Name;
                var vendor = CvLookup.CvTermInfo(m.Cvid).ParentsIsA.FirstOrDefault();
                manufacturer = vendor != default ? Shorten(CvLookup.CvTermInfo(vendor).Name) : null;
            }
            var sn = ic.CvParam(CVID.MS_instrument_serial_number);
            if (sn.Cvid != CVID.CVID_Unknown && serial is null)
                serial = sn.Value;
            var parts = ic.ComponentList.Select(c => string.Join(", ", c.CVParams.Select(p => p.Name))).Where(t => t.Length > 0);
            var text = string.Join(" | ", parts);
            if (text.Length > 0)
                configs.Add(text);
        }

        return new RunInfo
        {
            FileName = Path.GetFileName(path),
            RunId = NullIfEmpty(msd.Run.Id),
            StartTime = DateTime.TryParse(msd.Run.StartTimeStamp, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var t) ? t : null,
            InstrumentModel = model,
            InstrumentSerialNumber = serial,
            InstrumentManufacturer = manufacturer,
            Configurations = configs,
            Software = msd.Software.Select(sw => $"{sw.Id} {sw.Version}".Trim()).ToList(),
        };
    }

    private static string WithUnits(CVParam p) =>
        p.Units == CVID.CVID_Unknown ? p.Value : $"{p.Value} {p.UnitsName}";

    private static string Shorten(string name) => name switch
    {
        "beam-type collision-induced dissociation" => "HCD",
        "collision-induced dissociation" => "CID",
        "electron transfer dissociation" => "ETD",
        "electron capture dissociation" => "ECD",
        "supplemental beam-type collision-induced dissociation" => "supplemental HCD",
        "supplemental collision-induced dissociation" => "supplemental CID",
        "ultraviolet photodissociation" => "UVPD",
        _ => name,
    };

    [GeneratedRegex(@"\bscan=(\d+)")]
    private static partial Regex ScanEquals();

    private static int? ScanNumberFromId(string id)
    {
        var m = ScanEquals().Match(id ?? "");
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static int? NullIfZero(int v) => v == 0 ? null : v;

    private static double? Nullable(double? v) => v is { } d && double.IsFinite(d) ? d : null;

    internal static double Minutes(CVParam? param)
    {
        if (param is null || param.Cvid == CVID.CVID_Unknown)
            return double.NaN;
        double value = param;
        return param.Units switch
        {
            CVID.UO_second => value / 60.0,
            CVID.UO_millisecond => value / 60_000.0,
            _ => value, // UO_minute, and pwiz's convention for vendor data
        };
    }

    internal static double Milliseconds(CVParam? param)
    {
        if (param is null || param.Cvid == CVID.CVID_Unknown)
            return double.NaN;
        double value = param;
        return param.Units switch
        {
            CVID.UO_second => value * 1000.0,
            CVID.UO_microsecond => value / 1000.0,
            CVID.UO_nanosecond => value / 1_000_000.0,
            CVID.UO_minute => value * 60_000.0,
            _ => value, // UO_millisecond, the term's declared default
        };
    }
}
