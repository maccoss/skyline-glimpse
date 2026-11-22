using System.Globalization;
using System.Text.RegularExpressions;
using Glimpse.Core.Chemistry;
using Glimpse.Core.Document;

namespace Glimpse.Core.Assignment;

/// <summary>The text drawn over an annotated peak.</summary>
public static class PeakLabels
{
    /// <summary>
    /// A fragment's own name in MS2 ("y7", "b4++"). In MS1 a precursor isotope says whose it is: "LMSPTGGR 2+"
    /// for the monoisotopic peak, "LMSPTGGR 2+ M+1" for the next, with the label type when it is not light
    /// ("ACK heavy 2+") - several peptides overlap in an MS1 spectrum and "M+1" alone says nothing.
    /// </summary>
    public static string For(PeakMatch match, int msLevel) =>
        msLevel <= 1 && match.Ion.Type == IonType.Precursor
            ? Precursor(match.Owner.Precursor, match.Ion.Label)
            : match.Ion.Label;

    public static string Precursor(DocPrecursor precursor, string isotope)
    {
        var label = string.Equals(precursor.LabelType, "light", StringComparison.OrdinalIgnoreCase) ? "" : " " + precursor.LabelType;
        var iso = isotope is "" or "M" ? "" : " " + isotope;
        return string.Create(CultureInfo.InvariantCulture,
            $"{CompactSequence(precursor.Peptide.ModifiedSequence)}{label} {precursor.Charge}+{iso}");
    }

    private static readonly Regex MassDelta = new(@"\[([+-])(\d+(?:\.\d+)?)\]");

    /// <summary>
    /// A modified sequence with its modification masses to the nearest dalton ("VYAMC[+57]QIK"), as
    /// Skyline's graphs show them: six decimal places make a label three times as wide for nothing.
    /// </summary>
    public static string CompactSequence(string modifiedSequence) =>
        MassDelta.Replace(modifiedSequence, m =>
            "[" + m.Groups[1].Value
            + Math.Round(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)
            + "]");
}
