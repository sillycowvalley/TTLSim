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
/// A .oe equation carries the target's output-enable product term instead of
/// its logic; the compiler pairs it with the target's output equation.
/// </summary>
internal sealed class Equation
{
    public string Target = "";              // output signal name
    public bool Registered;                 // .d suffix (registered) vs combinational
    public bool OutputEnable;               // .oe suffix (per-macrocell output enable)
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
/// Scope:
///   - header properties (Name / PartNo / ... / Device);
///   - PIN declarations, with optional leading '!' for active-low;
///   - equations: any expression over NOT '!', AND '&', OR '#',
///     XOR '$' and parentheses, with CUPL precedence (highest to lowest):
///     ! , & , # , $. The expression is flattened to a sum-of-products here
///     (XOR expanded, De Morgan applied to negated products) so the fuse
///     mapper downstream only ever sees an OR of AND-of-literals. Flat SOP
///     inputs compile bit-for-bit as before.
///   - output extensions: '.d' marks a registered output, '.oe' gives an
///     output its per-macrocell output-enable term. Which GAL mode these
///     select, and their constraints, are the compiler's business; the parser
///     just records them. Any other extension is rejected.
///   - comments: '//' to end of line, and '/* ... */' NON-NESTING (CUPL rule:
///     the first '*/' closes the block).
///
/// Not yet handled (deferred, each will register an error if encountered):
///   - output extensions other than .d / .oe;
///   - set/range/index notation, function calls;
///   - '$' PREPROCESSOR directives ($DEFINE / $REPEAT / $MACRO ...). Note this
///     is distinct from the '$' XOR operator, which IS supported above.
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

        // Optional output extension: '.d' (registered output) or '.oe'
        // (per-macrocell output enable). Anything else is still rejected.
        bool registered = false;
        bool outputEnable = false;
        int dot = lhs.IndexOf('.');
        if (dot >= 0)
        {
            string suffix = lhs.Substring(dot + 1).Trim();
            lhs = lhs.Substring(0, dot).Trim();
            if (suffix.Equals("d", StringComparison.OrdinalIgnoreCase))
                registered = true;
            else if (suffix.Equals("oe", StringComparison.OrdinalIgnoreCase))
                outputEnable = true;
            else
            {
                errors.Add($"Output extension '.{suffix}' not supported (only .d and .oe): \"{Shorten(st)}\".");
                return;
            }
        }

        bool activeLow = lhs.StartsWith("!");
        string target = (activeLow ? lhs.Substring(1) : lhs).Trim();
        if (!IsName(target))
        {
            errors.Add($"Bad equation target: \"{Shorten(lhs)}\".");
            return;
        }
        if (outputEnable && activeLow)
        {
            errors.Add($"Output '{target}': '!' is not valid on a .oe equation.");
            return;
        }

        // Flatten the whole right-hand side (with !, &, #, $ and parentheses)
        // into a sum-of-products. Each product term is a name->polarity map.
        List<Dictionary<string, bool>>? sop =
            ExprParser.ParseToSop(rhs, errors, Shorten(st));
        if (sop == null) return;

        // Degenerate results have no fuse-array representation as a plain SOP:
        // an empty sum is constant 0; a term with no literals is constant 1.
        if (sop.Count == 0)
        {
            errors.Add($"Expression reduces to constant 0 (no product terms): \"{Shorten(st)}\".");
            return;
        }
        foreach (var termLits in sop)
        {
            if (termLits.Count == 0)
            {
                errors.Add($"Expression reduces to a constant (always true): \"{Shorten(st)}\".");
                return;
            }
        }

        var equation = new Equation
        {
            Target = target,
            ActiveLow = activeLow,
            Registered = registered,
            OutputEnable = outputEnable,
        };
        foreach (var termLits in sop)
        {
            var term = new ProductTerm();
            foreach (var kv in termLits)
                term.Literals[kv.Key] = kv.Value;   // true = asserted, false = negated
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

    /// <summary>
    /// Recursive-descent parser for a CUPL right-hand-side expression, producing
    /// a flattened sum-of-products. Grammar (loosest to tightest binding, which
    /// is CUPL precedence highest-to-lowest reversed): XOR '$', OR '#', AND '&',
    /// unary NOT '!', then a NAME or a parenthesised sub-expression.
    ///
    /// A product term is a Dictionary&lt;name,bool&gt; (true = asserted literal,
    /// false = negated). A sum-of-products is a List of those (an OR of ANDs).
    ///   - OR  concatenates the two lists (source order preserved, so existing
    ///         flat-SOP files keep their exact term ordering and fuse output).
    ///   - AND is a cartesian merge of terms; a term that would contain both a
    ///         literal and its complement (x &amp; !x) is dropped as constant 0.
    ///   - NOT is De Morgan: !(t1 # t2 # ...) = !t1 &amp; !t2 &amp; ...
    ///   - XOR(a,b) = (a &amp; !b) # (!a &amp; b).
    /// </summary>
    private sealed class ExprParser
    {
        private const string Ops = "!&#$()";

        private readonly List<string> toks;
        private readonly List<string> errors;
        private readonly string ctx;
        private int pos;
        private bool failed;

        private ExprParser(List<string> toks, List<string> errors, string ctx)
        {
            this.toks = toks;
            this.errors = errors;
            this.ctx = ctx;
        }

        public static List<Dictionary<string, bool>>? ParseToSop(
            string rhs, List<string> errors, string ctx)
        {
            List<string>? toks = Tokenize(rhs, errors, ctx);
            if (toks == null) return null;

            var p = new ExprParser(toks, errors, ctx);
            var sop = p.ParseXor();
            if (p.failed) return null;
            if (p.pos != toks.Count)
            {
                errors.Add($"Unexpected '{toks[p.pos]}' in \"{ctx}\".");
                return null;
            }
            return sop;
        }

        // ---- tokenizer: names and the single-char operators ! & # $ ( ) ----
        private static List<string>? Tokenize(string s, List<string> errors, string ctx)
        {
            var toks = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (Ops.IndexOf(c) >= 0) { toks.Add(c.ToString()); i++; continue; }
                if (c == '_' || char.IsLetter(c))
                {
                    int start = i++;
                    while (i < s.Length && (s[i] == '_' || char.IsLetterOrDigit(s[i]))) i++;
                    toks.Add(s.Substring(start, i - start));
                    continue;
                }
                errors.Add($"Bad character '{c}' in \"{ctx}\".");
                return null;
            }
            if (toks.Count == 0)
            {
                errors.Add($"Empty expression in \"{ctx}\".");
                return null;
            }
            return toks;
        }

        private string? Peek() => pos < toks.Count ? toks[pos] : null;

        // A token is a name (operand) if its first char is not an operator glyph.
        private static bool IsNameTok(string t) => Ops.IndexOf(t[0]) < 0;

        // xor := or ( '$' or )*
        private List<Dictionary<string, bool>> ParseXor()
        {
            var left = ParseOr();
            while (Peek() == "$") { pos++; left = Xor(left, ParseOr()); }
            return left;
        }

        // or := and ( '#' and )*
        private List<Dictionary<string, bool>> ParseOr()
        {
            var left = ParseAnd();
            while (Peek() == "#") { pos++; left = Or(left, ParseAnd()); }
            return left;
        }

        // and := not ( '&' not )*
        private List<Dictionary<string, bool>> ParseAnd()
        {
            var left = ParseNot();
            while (Peek() == "&") { pos++; left = And(left, ParseNot()); }
            return left;
        }

        // not := '!' not | primary
        private List<Dictionary<string, bool>> ParseNot()
        {
            if (Peek() == "!") { pos++; return Not(ParseNot()); }
            return ParsePrimary();
        }

        // primary := NAME | '(' xor ')'
        private List<Dictionary<string, bool>> ParsePrimary()
        {
            string? t = Peek();
            if (t == null) { Fail("expected an operand"); return Zero(); }
            if (t == "(")
            {
                pos++;
                var inner = ParseXor();
                if (Peek() != ")") { Fail("missing ')'"); return Zero(); }
                pos++;
                return inner;
            }
            if (IsNameTok(t)) { pos++; return Lit(t, true); }
            Fail($"unexpected '{t}'");
            return Zero();
        }

        private void Fail(string why)
        {
            if (!failed) errors.Add($"Malformed expression ({why}) in \"{ctx}\".");
            failed = true;
            pos = toks.Count;   // halt the recursion
        }

        // ---- sum-of-products primitives ----
        private static List<Dictionary<string, bool>> Zero() => new();      // empty OR = const 0
        private static List<Dictionary<string, bool>> One() =>              // one empty AND = const 1
            new() { new Dictionary<string, bool>() };
        private static List<Dictionary<string, bool>> Lit(string name, bool asserted) =>
            new() { new Dictionary<string, bool> { [name] = asserted } };

        private static List<Dictionary<string, bool>> Or(
            List<Dictionary<string, bool>> a, List<Dictionary<string, bool>> b)
        {
            var r = new List<Dictionary<string, bool>>(a);
            r.AddRange(b);
            return r;
        }

        private static List<Dictionary<string, bool>> And(
            List<Dictionary<string, bool>> a, List<Dictionary<string, bool>> b)
        {
            var r = new List<Dictionary<string, bool>>();
            foreach (var ta in a)
            {
                foreach (var tb in b)
                {
                    var merged = new Dictionary<string, bool>(ta);
                    bool contradiction = false;
                    foreach (var kv in tb)
                    {
                        if (merged.TryGetValue(kv.Key, out bool existing))
                        {
                            if (existing != kv.Value) { contradiction = true; break; }
                        }
                        else merged[kv.Key] = kv.Value;
                    }
                    if (!contradiction) r.Add(merged);
                }
            }
            return r;
        }

        private static List<Dictionary<string, bool>> Not(List<Dictionary<string, bool>> a)
        {
            // !(t1 # t2 # ...) = !t1 & !t2 & ...  where !term = OR of negated literals.
            var acc = One();
            foreach (var term in a)
            {
                if (term.Count == 0) return Zero();   // !(constant 1) = constant 0
                var negTerm = new List<Dictionary<string, bool>>();
                foreach (var kv in term)
                    negTerm.Add(new Dictionary<string, bool> { [kv.Key] = !kv.Value });
                acc = And(acc, negTerm);
            }
            return acc;
        }

        private static List<Dictionary<string, bool>> Xor(
            List<Dictionary<string, bool>> a, List<Dictionary<string, bool>> b)
        {
            // a $ b = (a & !b) # (!a & b)
            return Or(And(a, Not(b)), And(Not(a), b));
        }
    }
}