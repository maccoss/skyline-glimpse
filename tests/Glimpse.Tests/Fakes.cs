using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Glimpse.Core.Library;

namespace Glimpse.Tests;

/// <summary>An in-memory run: spectra built in the test, served through the reader seam.</summary>
internal sealed class FakeSource : IMsDataSource
{
    private readonly List<SpectrumData> _spectra = new();

    public string FilePath { get; init; } = @"C:\data\run1.raw";

    public int SpectrumCount => _spectra.Count;

    public RunInfo Run { get; } = new() { FileName = "run1.raw" };

    public bool CanVendorCentroid => false;

    public int Reads { get; private set; }

    public FakeSource Ms1(double rt, double[] mz, double[] intensity, float injectionMs = 10f)
    {
        _spectra.Add(Spectrum(_spectra.Count, 1, rt, mz, intensity, injectionMs, double.NaN, double.NaN));
        return this;
    }

    public FakeSource Ms2(double rt, double lower, double upper, double[] mz, double[] intensity, float injectionMs = 10f)
    {
        _spectra.Add(Spectrum(_spectra.Count, 2, rt, mz, intensity, injectionMs, lower, upper));
        return this;
    }

    public SpectrumData ReadSpectrum(int index, bool centroid)
    {
        Reads++;
        return _spectra[index];
    }

    public double ReadRetentionTime(int index) => _spectra[index].Header.RetentionTime;

    public void Dispose()
    {
    }

    public static SpectrumData Spectrum(int index, int level, double rt, double[] mz, double[] intensity, float injectionMs, double lower, double upper)
    {
        var tic = intensity.Sum();
        var bp = intensity.Length == 0 ? -1 : Array.IndexOf(intensity, intensity.Max());
        var header = new ScanHeader(index, (byte)level, rt, injectionMs, (lower + upper) / 2, lower, upper, tic,
            bp < 0 ? double.NaN : mz[bp], bp < 0 ? 0 : (float)intensity[bp], mz.Length, Polarity.Positive, true);
        return new SpectrumData(header, mz, intensity, new SpectrumDetails { NativeId = $"scan={index + 1}", ScanNumber = index + 1 });
    }
}

/// <summary>Builds small documents for tests.</summary>
internal static class Docs
{
    public static DocPrecursor Precursor(DocProtein protein, string sequence, int charge, double mz, string file, double start, double end,
        params (string Ion, int Charge, double Mz)[] transitions)
    {
        var peptide = protein.Peptides.FirstOrDefault(p => p.ModifiedSequence == sequence);
        if (peptide is null)
        {
            peptide = new DocPeptide(protein, sequence, sequence, $"Molecule:/{protein.Name}/{sequence}");
            protein.Peptides.Add(peptide);
        }
        var precursor = new DocPrecursor(peptide, sequence, charge, mz, "light", $"Precursor:/{protein.Name}/{sequence}/{charge}");
        foreach (var (ion, z, m) in transitions)
            precursor.Transitions.Add(new DocTransition(ion, z, m));
        precursor.Peaks[file] = new PeakBounds((start + end) / 2, start, end, 0.001, null, null);
        peptide.Precursors.Add(precursor);
        return precursor;
    }

    public static DocumentModel Model(params DocProtein[] proteins) =>
        new("doc.sky", proteins, new[] { new ResultFileInfo("run1.raw", @"C:\data\run1.raw", "run1") });
}

/// <summary>A library holding exactly the spectra given.</summary>
internal sealed class FakeLibrary : ISpectralLibrary
{
    private readonly Dictionary<(string, int), LibrarySpectrum> _entries = new();

    public string Name => "fake.blib";

    public int Count => _entries.Count;

    public FakeLibrary Add(string modifiedSequence, int charge, double[] mz, double[] intensity)
    {
        _entries[(modifiedSequence, charge)] = new LibrarySpectrum { LibraryName = Name, ModifiedSequence = modifiedSequence, Charge = charge, Mz = mz, Intensity = intensity };
        return this;
    }

    public LibrarySpectrum? Find(string modifiedSequence, int charge) =>
        _entries.TryGetValue((modifiedSequence, charge), out var e) ? e : null;
}
