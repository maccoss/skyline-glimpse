using System.Globalization;
using System.Text;

namespace Glimpse.Core.Chemistry;

/// <summary>Monoisotopic masses used throughout (the values Skyline and pwiz use).</summary>
public static class Mass
{
    public const double Proton = 1.00727646688;
    public const double Hydrogen = 1.00782503207;
    public const double Water = 18.0105646863;
    public const double Ammonia = 17.02654910101;
    public const double CarbonMonoxide = 27.99491461956;

    /// <summary>Spacing between isotope peaks of a peptide (the 13C-12C difference).</summary>
    public const double IsotopeSpacing = 1.00335483778;

    private static readonly double[] Residues = BuildResidues();

    /// <summary>Monoisotopic residue mass of an amino acid letter, or NaN for an unknown letter.</summary>
    public static double Residue(char aa) => aa is >= 'A' and <= 'Z' ? Residues[aa - 'A'] : double.NaN;

    private static double[] BuildResidues()
    {
        var r = Enumerable.Repeat(double.NaN, 26).ToArray();
        void Set(char c, double m) => r[c - 'A'] = m;
        Set('G', 57.02146372); Set('A', 71.03711378); Set('S', 87.03202840); Set('P', 97.05276384);
        Set('V', 99.06841390); Set('T', 101.04767846); Set('C', 103.00918447); Set('L', 113.08406396);
        Set('I', 113.08406396); Set('N', 114.04292744); Set('D', 115.02694302); Set('Q', 128.05857750);
        Set('K', 128.09496301); Set('E', 129.04259308); Set('M', 131.04048508); Set('H', 137.05891186);
        Set('F', 147.06841390); Set('R', 156.10111102); Set('Y', 163.06332852); Set('W', 186.07931294);
        Set('U', 150.95363555); Set('O', 237.14772677);
        return r;
    }
}

/// <summary>
/// A peptide parsed from a mass-annotated modified sequence such as <c>[+42.010565]PEPC[+57.021464]TIDEK</c>
/// (Skyline's "Modified Sequence Monoisotopic Masses", and the BiblioSpec <c>peptideModSeq</c> form).
/// </summary>
public sealed class ModifiedSequence
{
    private ModifiedSequence(string sequence, double[] residueMasses, double[] modDeltas, double nTermDelta)
    {
        Sequence = sequence;
        ResidueMasses = residueMasses;
        ModificationDeltas = modDeltas;
        NTerminalDelta = nTermDelta;
    }

    /// <summary>Unmodified sequence.</summary>
    public string Sequence { get; }

    /// <summary>Mass of each residue including its modifications (an N-terminal modification is folded into the first).</summary>
    public double[] ResidueMasses { get; }

    /// <summary>Modification mass on each residue (0 when unmodified); an N-terminal modification counts on residue 0.</summary>
    public double[] ModificationDeltas { get; }

    public double NTerminalDelta { get; }

    public int Length => Sequence.Length;

    /// <summary>Neutral monoisotopic mass of the peptide.</summary>
    public double MonoisotopicMass => ResidueMasses.Sum() + Mass.Water;

    public double PrecursorMz(int charge) => (MonoisotopicMass + charge * Mass.Proton) / charge;

    /// <summary>
    /// A key that is the same for the same peptide however many decimals its modification masses were
    /// written with: sequence plus each modified position's delta rounded to 0.01 Da.
    /// </summary>
    public string MatchKey
    {
        get
        {
            var sb = new StringBuilder(Sequence);
            for (var i = 0; i < ModificationDeltas.Length; i++)
            {
                if (Math.Abs(ModificationDeltas[i]) > 1e-6)
                    sb.Append(CultureInfo.InvariantCulture, $"|{i}:{Math.Round(ModificationDeltas[i], 2):0.00}");
            }
            return sb.ToString();
        }
    }

    /// <summary>Parse a mass-annotated modified sequence; null when it contains anything unresolvable.</summary>
    public static ModifiedSequence? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var seq = new StringBuilder();
        var residues = new List<double>();
        var deltas = new List<double>();
        double pendingNTerm = 0;
        var i = 0;
        text = text.Trim();
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '[' || ch == '(' || ch == '{')
            {
                var close = text.IndexOf(ch == '[' ? ']' : ch == '(' ? ')' : '}', i + 1);
                if (close < 0)
                    return null;
                var inner = text.Substring(i + 1, close - i - 1);
                if (!double.TryParse(inner, NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
                    return null; // a named or Unimod-id modification carries no mass here
                if (residues.Count == 0)
                {
                    pendingNTerm += delta;
                }
                else
                {
                    residues[^1] += delta;
                    deltas[^1] += delta;
                }
                i = close + 1;
                continue;
            }
            if (ch == '-' || ch == '.' || ch == '_')
            {
                i++;
                continue;
            }
            var m = Mass.Residue(char.ToUpperInvariant(ch));
            if (double.IsNaN(m))
                return null;
            seq.Append(char.ToUpperInvariant(ch));
            residues.Add(m);
            deltas.Add(0);
            i++;
        }
        if (residues.Count == 0)
            return null;
        residues[0] += pendingNTerm;
        deltas[0] += pendingNTerm;
        return new ModifiedSequence(seq.ToString(), residues.ToArray(), deltas.ToArray(), pendingNTerm);
    }
}
