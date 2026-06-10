using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace BlinkyJed;

/// <summary>The parsed contents of a .pld source file.</summary>
internal sealed class PldDocument
{
    public string DeviceName = "";          // from the PLD header, e.g. "g16v8"
    public string Title = "";               // the Name property; used as the .jed note
    public string PartNo = "";              // the PartNo property; used as the signature
    public List<PinDeclaration> Pins = new();
    public List<Equation> Equations = new();
}

/// <summary>One PIN declaration: pin number, signal name, polarity.</summary>
internal sealed class PinDeclaration
{
    public int Number;
    public string Name = "";
    public bool ActiveLow;                  // leading ! in CUPL
}

/// <summary>
/// One output equation: a sum (OR) of product terms (AND of literals).
/// Registered vs combinational and output polarity drive OLMC config fuses.
/// </summary>
internal sealed class Equation
{
    public string Target = "";              // output signal name
    public bool Registered;                 // .D / .R (registered) vs combinational
    public bool ActiveLow;                  // complemented output (leading !)
    public List<ProductTerm> Terms = new(); // OR of these
}

/// <summary>A single AND term: each literal asserted (true) or negated (false).</summary>
internal sealed class ProductTerm
{
    public Dictionary<string, bool> Literals = new();
}

/// <summary>
/// Stage 1: parse CUPL/PLD source into a <see cref="PldDocument"/>.
///
/// Scope (matches the surface of the Mini Blinky decode .pld files):
///   - header properties (Name / PartNo / ... / Device);
///   - PIN declarations, with optional leading '!' for active-low;
///   - combinational equations: a sum '#' of products '&' of literals '[!]name',
///     optional parentheses around a product term;
///   - comments: '//' to end of line, and '/* ... */' NON-NESTING (CUPL rule:
///     the first '*/' closes the block).
///
/// Not yet handled (deferred, each will register an error if encountered):
///   - output extensions (.D / .R / .OE / ...);
///   - set/range/index notation, function calls, $-directives.
/// </summary>
internal static class PldParser
{
    private static readonly HashSet<string> HeaderKeywords =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "NAME", "PARTNO", "DATE", "REVISION", "DESIGNER",
        "COMPANY", "ASSEMBLY", "LOCATION", "DEVICE",
    };

    private static readonly Regex PinRx =
        new(@"^PIN\s+(\d+)\s*=\s*(!?)\s*([A-Za-z_]\w*)$", RegexOptions.IgnoreCase);

    private static readonly Regex NameRx = new(@"^[A-Za-z_]\w*$");

    public static PldDocument Parse(string source, List<string> errors)
    {
        var doc = new PldDocument();
        string code = StripComments(source);

        foreach (string segment in code.Split(';'))
        {
            string st = CollapseWhitespace(segment);
            if (st.Length == 0) continue;

            int space = st.IndexOf(' ');
            string first = space < 0 ? st : st.Substring(0, space);

            if (HeaderKeywords.Contains(first))
            {
                string value = space < 0 ? "" : st.Substring(space + 1).Trim();
                ApplyHeader(doc, first, value);
            }
            else if (first.Equals("PIN", StringComparison.OrdinalIgnoreCase))
            {
                ParsePin(doc, st, errors);
            }
            else if (st.Contains('='))
            {
                ParseEquation(doc, st, errors);
            }
            else
            {
                errors.Add($"Unrecognised statement: \"{Shorten(st)}\".");
            }
        }

        if (doc.DeviceName.Length == 0)
            errors.Add("No Device specified in the .pld header.");
        if (doc.Equations.Count == 0)
            errors.Add("No output equations found.");

        return doc;
    }

    private static void ApplyHeader(PldDocument doc, string keyword, string value)
    {
        if (keyword.Equals("DEVICE", StringComparison.OrdinalIgnoreCase))
            doc.DeviceName = value;
        else if (keyword.Equals("NAME", StringComparison.OrdinalIgnoreCase))
            doc.Title = value;
        else if (keyword.Equals("PARTNO", StringComparison.OrdinalIgnoreCase))
            doc.PartNo = value;
    }

    private static void ParsePin(PldDocument doc, string st, List<string> errors)
    {
        Match m = PinRx.Match(st);
        if (!m.Success)
        {
            errors.Add($"Malformed PIN declaration: \"{Shorten(st)}\".");
            return;
        }
        doc.Pins.Add(new PinDeclaration
        {
            Number = int.Parse(m.Groups[1].Value),
            ActiveLow = m.Groups[2].Value == "!",
            Name = m.Groups[3].Value,
        });
    }

    private static void ParseEquation(PldDocument doc, string st, List<string> errors)
    {
        int eq = st.IndexOf('=');
        string lhs = st.Substring(0, eq).Trim();
        string rhs = st.Substring(eq + 1).Trim();

        if (lhs.Contains('.'))
        {
            errors.Add($"Output extensions (e.g. .D/.OE) not supported yet: \"{Shorten(st)}\".");
            return;
        }

        bool activeLow = lhs.StartsWith("!");
        string target = (activeLow ? lhs.Substring(1) : lhs).Trim();
        if (!IsName(target))
        {
            errors.Add($"Bad equation target: \"{Shorten(lhs)}\".");
            return;
        }

        var equation = new Equation { Target = target, ActiveLow = activeLow, Registered = false };

        foreach (string termText in rhs.Split('#'))
        {
            string t = termText.Trim();
            if (t.StartsWith("(") && t.EndsWith(")"))
                t = t.Substring(1, t.Length - 2).Trim();
            if (t.Length == 0)
            {
                errors.Add($"Empty product term in \"{Shorten(st)}\".");
                continue;
            }

            var term = new ProductTerm();
            foreach (string litText in t.Split('&'))
            {
                string lit = litText.Trim();
                bool negated = lit.StartsWith("!");
                string name = (negated ? lit.Substring(1) : lit).Trim();
                if (!IsName(name))
                {
                    errors.Add($"Bad literal \"{Shorten(lit)}\" in \"{Shorten(st)}\".");
                    continue;
                }
                term.Literals[name] = !negated;   // true = asserted, false = negated
            }
            equation.Terms.Add(term);
        }

        doc.Equations.Add(equation);
    }

    // ---- CUPL comments: '//' to EOL, '/* */' NON-NESTING (first '*/' closes) ----
    private static string StripComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                i += 2;                 // skip the closing */ (or run off the end)
                sb.Append(' ');
                continue;
            }
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            sb.Append(s[i]);
            i++;
        }
        return sb.ToString();
    }

    private static string CollapseWhitespace(string s) =>
        Regex.Replace(s, @"\s+", " ").Trim();

    private static bool IsName(string s) => NameRx.IsMatch(s);

    private static string Shorten(string s) =>
        s.Length > 44 ? s.Substring(0, 44) + "..." : s;
}