using System.Globalization;
using System.Text.RegularExpressions;

namespace Glimpse.Core.Document;

/// <summary>
/// Skyline element locators ("Molecule:/sp|P1|X_HUMAN/PEPTIDEK"), built the way Skyline builds them.
/// </summary>
/// <remarks>
/// <para>Why build them rather than ask for them: Skyline's report columns ProteinLocator, PeptideLocator
/// and PrecursorLocator cost it about half a millisecond a row. On a 69,000 precursor document asking for
/// them took a report from 0.5 s to 36 s, and the transition report from 3.5 s to 41 s. The names they
/// are made of are ordinary columns.</para>
/// <para>Ported from pwiz_tools/Skyline/Model/ElementLocators: <c>ElementLocator.QuoteIfSpecial</c> and
/// the names of <c>MoleculeGroupRef</c> (protein name), <c>MoleculeRef</c> (modified sequence with
/// monoisotopic masses, the PeptideModifiedSequenceMonoisotopicMasses column) and <c>PrecursorRef</c>
/// (label type and charge indicator, <c>Transition.GetChargeIndicator</c> in the invariant culture).
/// Skyline adds an index when one protein holds two peptides of the same name; such duplicates are not
/// distinguished here.</para>
/// </remarks>
public static class SkylineLocators
{
    private static readonly Regex SpecialChars = new("[/?&=\"]");

    /// <summary>A name as it appears in a locator: quoted (quotes doubled) when it holds / ? &amp; = or ".</summary>
    public static string Quote(string name) =>
        SpecialChars.IsMatch(name) ? "\"" + name.Replace("\"", "\"\"") + "\"" : name;

    public static string Protein(string proteinName) => "MoleculeGroup:/" + Quote(proteinName);

    public static string Peptide(string proteinName, string modifiedSequence) =>
        "Molecule:/" + Quote(proteinName) + "/" + Quote(modifiedSequence);

    public static string Precursor(string proteinName, string modifiedSequence, string labelType, int charge) =>
        "Precursor:/" + Quote(proteinName) + "/" + Quote(modifiedSequence) + "/" + Quote(labelType + ChargeIndicator(charge));

    /// <summary>"+", "++" ... "++++", then ", +5" (the invariant list separator); minus signs likewise.</summary>
    public static string ChargeIndicator(int charge)
    {
        if (charge >= 0)
            return charge <= 4 ? new string('+', charge) : string.Format(CultureInfo.InvariantCulture, ", +{0}", charge);
        return -charge <= 2 ? new string('-', -charge) : string.Format(CultureInfo.InvariantCulture, ", -{0}", -charge);
    }
}
