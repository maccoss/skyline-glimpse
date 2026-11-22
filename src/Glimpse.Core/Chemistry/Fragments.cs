using System.Globalization;

namespace Glimpse.Core.Chemistry;

/// <summary>Fragment ion series.</summary>
public enum IonType
{
    A,
    B,
    C,
    X,
    Y,

    /// <summary>Even-electron z (y - NH3); Skyline's "z".</summary>
    Z,

    /// <summary>z-dot, the z+1 radical (y - NH3 + H) that dominates ETD/ECD spectra; Skyline's "z•".</summary>
    ZDot,

    /// <summary>z-prime, z+2 (y - NH3 + 2H); Skyline's "z'".</summary>
    ZPrime,

    /// <summary>Precursor isotope peak (M, M+1, ...).</summary>
    Precursor,

    /// <summary>A document transition Glimpse cannot classify (neutral loss, custom ion): labeled with Skyline's own name.</summary>
    Other,
}

/// <summary>One theoretical ion.</summary>
/// <param name="Mz">Monoisotopic m/z.</param>
/// <param name="Type">Series.</param>
/// <param name="Ordinal">Residue count for fragments; isotope index for precursor ions.</param>
/// <param name="Charge">Charge state.</param>
/// <param name="Label">Display label, e.g. "y7", "b4++", "z•5", "M+1".</param>
public readonly record struct TheoreticalIon(double Mz, IonType Type, int Ordinal, int Charge, string Label);

public static class Fragments
{
    /// <summary>Every ion type that can be labeled, in menu order.</summary>
    public static IReadOnlyList<IonType> FragmentTypes { get; } =
        new[] { IonType.A, IonType.B, IonType.C, IonType.X, IonType.Y, IonType.Z, IonType.ZDot, IonType.ZPrime };

    public static string Name(IonType type) => type switch
    {
        IonType.A => "a",
        IonType.B => "b",
        IonType.C => "c",
        IonType.X => "x",
        IonType.Y => "y",
        IonType.Z => "z",
        IonType.ZDot => "z•",
        IonType.ZPrime => "z'",
        IonType.Precursor => "precursor",
        _ => "?",
    };

    public static bool IsNTerminal(IonType type) => type is IonType.A or IonType.B or IonType.C;

    /// <summary>
    /// The fragments of <paramref name="peptide"/> of the given types and charges. Charges above the
    /// precursor charge are skipped, as Skyline does.
    /// </summary>
    public static List<TheoreticalIon> Enumerate(
        ModifiedSequence peptide, IReadOnlyCollection<IonType> types, IReadOnlyCollection<int> charges, int precursorCharge)
    {
        var n = peptide.Length;
        var ions = new List<TheoreticalIon>();
        if (n < 2)
            return ions;
        var prefix = new double[n + 1];
        for (var i = 0; i < n; i++)
            prefix[i + 1] = prefix[i] + peptide.ResidueMasses[i];
        var total = prefix[n];

        foreach (var z in charges)
        {
            if (z < 1 || (precursorCharge > 0 && z > precursorCharge))
                continue;
            var suffix = z == 1 ? "" : new string('+', z);
            foreach (var type in types)
            {
                for (var k = 1; k < n; k++)
                {
                    // Neutral masses of the singly-cleaved fragments of length k.
                    var nTerm = prefix[k];
                    var cTerm = total - prefix[n - k] + Mass.Water;
                    var neutral = type switch
                    {
                        IonType.A => nTerm - Mass.CarbonMonoxide,
                        IonType.B => nTerm,
                        IonType.C => nTerm + Mass.Ammonia,
                        IonType.X => cTerm + Mass.CarbonMonoxide - 2 * Mass.Hydrogen,
                        IonType.Y => cTerm,
                        IonType.Z => cTerm - Mass.Ammonia,
                        IonType.ZDot => cTerm - Mass.Ammonia + Mass.Hydrogen,
                        IonType.ZPrime => cTerm - Mass.Ammonia + 2 * Mass.Hydrogen,
                        _ => double.NaN,
                    };
                    if (double.IsNaN(neutral))
                        continue;
                    ions.Add(new TheoreticalIon((neutral + z * Mass.Proton) / z, type, k, z, $"{Name(type)}{k}{suffix}"));
                }
            }
        }
        return ions;
    }

    /// <summary>Precursor isotope peaks M, M+1, ... M+<paramref name="isotopes"/>.</summary>
    public static List<TheoreticalIon> PrecursorIsotopes(double precursorMz, int charge, int isotopes = 2)
    {
        var ions = new List<TheoreticalIon>();
        for (var k = 0; k <= isotopes; k++)
        {
            var label = k == 0 ? "M" : $"M+{k}";
            ions.Add(new TheoreticalIon(precursorMz + k * Mass.IsotopeSpacing / Math.Max(1, charge), IonType.Precursor, k, charge, label));
        }
        return ions;
    }

    /// <summary>
    /// Label and type for a Skyline transition from its "Fragment Ion" text ("y7", "b4", "precursor",
    /// "precursor [M+1]", "y7 -98", ...).
    /// </summary>
    public static (IonType Type, int Ordinal, string Label) ParseSkylineFragment(string fragmentIon, int charge)
    {
        var text = fragmentIon.Trim();
        var suffix = charge > 1 ? new string('+', charge) : "";
        if (text.StartsWith("precursor", StringComparison.OrdinalIgnoreCase))
        {
            var open = text.IndexOf('[');
            var iso = open >= 0 ? text[(open + 1)..].TrimEnd(']') : "M";
            var ordinal = iso.StartsWith("M+", StringComparison.Ordinal) && int.TryParse(iso[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) ? k : 0;
            return (IonType.Precursor, ordinal, iso);
        }
        if (text.Length >= 2 && char.IsLetter(text[0]))
        {
            var type = char.ToLowerInvariant(text[0]) switch
            {
                'a' => IonType.A,
                'b' => IonType.B,
                'c' => IonType.C,
                'x' => IonType.X,
                'y' => IonType.Y,
                'z' => IonType.Z,
                _ => IonType.Other,
            };
            // Skyline writes z-dot and z-prime with the mark before the ordinal: "z•5", "z'5".
            var at = 1;
            if (type == IonType.Z && at < text.Length && text[at] is '•' or '.')
            {
                type = IonType.ZDot;
                at++;
            }
            else if (type == IonType.Z && at < text.Length && text[at] == '\'')
            {
                type = IonType.ZPrime;
                at++;
            }
            var digits = new string(text.Skip(at).TakeWhile(char.IsDigit).ToArray());
            if (type != IonType.Other && int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return (type, n, text + suffix);
        }
        return (IonType.Other, 0, text + suffix);
    }
}
