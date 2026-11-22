using System.Globalization;
using System.Xml.Linq;
using Glimpse.Core.Chemistry;

namespace Glimpse.Core.Document;

/// <summary>
/// The document settings Glimpse uses, read from Skyline settings XML (the live document via the tool
/// service's GetDocumentSettings, or a saved .sky file).
/// </summary>
public sealed record DocumentSettings
{
    /// <summary>Fallbacks when a document does not say: Skyline's own defaults for centroided data.</summary>
    public static DocumentSettings Default { get; } = new();

    /// <summary>MS1 extraction window (transition_full_scan precursor settings).</summary>
    public MzTolerance PrecursorTolerance { get; init; } = MzTolerance.Ppm(10);

    /// <summary>MS2 extraction window (transition_full_scan product settings).</summary>
    public MzTolerance ProductTolerance { get; init; } = MzTolerance.Ppm(10);

    /// <summary>Library ion match tolerance (transition_libraries), what Skyline's spectrum graph labels with.</summary>
    public MzTolerance IonMatchTolerance { get; init; } = MzTolerance.Th(0.05);

    /// <summary>Fragment types the transition filter picks, e.g. y, b, p.</summary>
    public IReadOnlyList<string> FragmentTypes { get; init; } = new[] { "y", "b" };

    /// <summary>Product charges the transition filter picks.</summary>
    public IReadOnlyList<int> ProductCharges { get; init; } = new[] { 1, 2 };

    /// <summary>File names (or hints) of the document's BiblioSpec libraries, in settings order.</summary>
    public IReadOnlyList<string> LibraryFiles { get; init; } = Array.Empty<string>();

    public string? AcquisitionMethod { get; init; }

    public static DocumentSettings Parse(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        if (root is null)
            return Default;
        var result = Default;

        var fullScan = root.Descendants("transition_full_scan").FirstOrDefault();
        if (fullScan is not null)
        {
            result = result with
            {
                AcquisitionMethod = (string?)fullScan.Attribute("acquisition_method"),
                PrecursorTolerance = MzTolerance.FromSkyline(
                    (string?)fullScan.Attribute("precursor_mass_analyzer"), (string?)fullScan.Attribute("precursor_res"),
                    (string?)fullScan.Attribute("precursor_res_mz"), (string?)fullScan.Attribute("selective_extraction"))
                    ?? result.PrecursorTolerance,
                ProductTolerance = MzTolerance.FromSkyline(
                    (string?)fullScan.Attribute("product_mass_analyzer"), (string?)fullScan.Attribute("product_res"),
                    (string?)fullScan.Attribute("product_res_mz"), (string?)fullScan.Attribute("selective_extraction"))
                    ?? result.ProductTolerance,
            };
        }

        var libs = root.Descendants("transition_libraries").FirstOrDefault();
        if (libs is not null && double.TryParse((string?)libs.Attribute("ion_match_tolerance"), NumberStyles.Float, CultureInfo.InvariantCulture, out var tol))
        {
            var unit = ((string?)libs.Attribute("ion_match_tolerance_unit") ?? "").ToLowerInvariant();
            result = result with { IonMatchTolerance = unit == "ppm" ? MzTolerance.Ppm(tol) : MzTolerance.Th(tol) };
        }

        var filter = root.Descendants("transition_filter").FirstOrDefault();
        if (filter is not null)
        {
            var types = ((string?)filter.Attribute("fragment_types"))?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var charges = ((string?)filter.Attribute("product_charges"))?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var z) ? z : 0)
                .Where(z => z > 0).ToArray();
            if (types is { Length: > 0 })
                result = result with { FragmentTypes = types };
            if (charges is { Length: > 0 })
                result = result with { ProductCharges = charges };
        }

        var libraryFiles = root.Descendants("peptide_libraries").Elements()
            .Select(e => (string?)e.Attribute("file_name_hint") ?? (string?)e.Attribute("file_path"))
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f!)
            .ToArray();
        return result with { LibraryFiles = libraryFiles };
    }
}
