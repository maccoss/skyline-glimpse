namespace Glimpse.Core.Library;

/// <summary>A spectral library Glimpse can look precursors up in.</summary>
public interface ISpectralLibrary
{
    /// <summary>Library name (file name) for display.</summary>
    string Name { get; }

    /// <summary>Number of library spectra.</summary>
    int Count { get; }

    /// <summary>
    /// The library entry for a peptide (mass-annotated modified sequence) at a charge, or null.
    /// Peaks are read on demand and cached.
    /// </summary>
    LibrarySpectrum? Find(string modifiedSequence, int charge);
}

/// <summary>One library spectrum and the metadata Skyline's library properties show.</summary>
public sealed record LibrarySpectrum
{
    public string LibraryName { get; init; } = "";

    public string ModifiedSequence { get; init; } = "";

    public int Charge { get; init; }

    public double PrecursorMz { get; init; }

    public double? RetentionTime { get; init; }

    /// <summary>Data file the identification came from (SpectrumSourceFiles.fileName).</summary>
    public string? SourceFile { get; init; }

    /// <summary>Search result file the identification was read from (SpectrumSourceFiles.idFileName).</summary>
    public string? IdFile { get; init; }

    public double? Score { get; init; }

    public string? ScoreType { get; init; }

    /// <summary>Number of spectra the entry was built from.</summary>
    public int? Copies { get; init; }

    public string? SpectrumIdInFile { get; init; }

    /// <summary>Peak m/z, ascending.</summary>
    public double[] Mz { get; init; } = Array.Empty<double>();

    /// <summary>Peak intensities, normalized to the most intense = 1.</summary>
    public double[] Intensity { get; init; } = Array.Empty<double>();
}
