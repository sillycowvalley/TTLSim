namespace TTLSim.Core;

/// <summary>
/// A parsed testbench stimulus program: a set of named columns and a list of
/// rows, each row giving one <see cref="Signal"/> per column.
///
/// <para>The text form is a minimal CSV:</para>
/// <code>
/// CLK,/RESET,D0,D1
/// L,L,L,L
/// H,L,H,L
/// L,H,Z,Z
/// </code>
///
/// <list type="bullet">
///   <item>Line 1 is the column names. Each name becomes a pin on the
///         testbench symbol, so names must be non-empty and unique.</item>
///   <item>Every later line is one row of cell values, one per column.
///         A row with the wrong number of cells is an error, not a
///         silently padded row.</item>
///   <item>Cells are <c>H</c>, <c>L</c> or <c>Z</c>, case-insensitive and
///         surrounded by any amount of whitespace. <c>Z</c> releases the
///         pin (high-Z) so a testbench column can share a net with
///         something the circuit itself drives.</item>
///   <item>Blank lines are ignored anywhere, so a trailing newline is fine.</item>
/// </list>
///
/// <para>Name comparison is ORDINAL, the same rule net labels use: case and
/// punctuation are significant, so <c>/WE</c>, <c>WE</c> and <c>we</c> are
/// three different columns.</para>
///
/// <para>Quoting is NOT supported -- the format has no use for it (cells are
/// single letters) and supporting it would only let a comma hide inside a
/// column name. A name containing a comma is therefore impossible, which is
/// exactly the intent.</para>
///
/// <para>RESERVED SYNTAX: a column name beginning with '?' is reserved for a
/// later "expected value" feature, where the simulator checks a value instead
/// of driving it. Such a name is REJECTED today rather than treated as an
/// ordinary column, so that no file written now can quietly change meaning
/// when the feature lands.</para>
///
/// <para>Lives in TTLSim.Core, next to <see cref="JedecFuseMap"/> and
/// <see cref="IntelHex"/>, so the GUI's import validation and the simulator's
/// execution run through exactly the same parser -- a file that imports
/// cleanly cannot then fail at build time.</para>
/// </summary>
public sealed class TestbenchProgram
{
    /// <summary>Upper bound on columns. Generous next to the net label's 16
    /// (a testbench plausibly drives a whole 32-bit bus plus control), but
    /// bounded so a mis-selected file cannot try to build thousands of pins
    /// on a symbol.</summary>
    public const int MaxColumns = 64;

    // Row-major: cells[row * ColumnCount + column].
    private readonly Signal[] cells;

    private TestbenchProgram(string[] columnNames, int rowCount, Signal[] cells)
    {
        ColumnNames = columnNames;
        RowCount = rowCount;
        this.cells = cells;
    }

    /// <summary>Column names, in file order. Column order is the order the
    /// pins are laid out down the symbol.</summary>
    public IReadOnlyList<string> ColumnNames { get; }

    public int ColumnCount => ColumnNames.Count;

    public int RowCount { get; }

    /// <summary>Value driven onto <paramref name="column"/> during
    /// <paramref name="row"/>.</summary>
    public Signal this[int row, int column] => cells[row * ColumnCount + column];

    /// <summary>
    /// Parse the CSV text. Throws <see cref="FormatException"/> with a message
    /// naming the offending line (1-based, counting blank lines, so it matches
    /// what a text editor shows) on any malformed input.
    /// </summary>
    public static TestbenchProgram Parse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            throw new FormatException("The testbench program is empty.");

        string[] lines = csv.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // ---------------------------------------------------------- header
        int headerLine = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            headerLine = i;
            break;
        }
        if (headerLine < 0)
            throw new FormatException("The testbench program is empty.");

        string[] names = SplitAndTrim(lines[headerLine]);

        if (names.Length == 0)
            throw new FormatException($"Line {headerLine + 1}: the header row has no columns.");
        if (names.Length > MaxColumns)
            throw new FormatException(
                $"Line {headerLine + 1}: {names.Length} columns, but the maximum is {MaxColumns}.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int c = 0; c < names.Length; c++)
        {
            string name = names[c];

            if (name.Length == 0)
                throw new FormatException(
                    $"Line {headerLine + 1}: column {c + 1} has no name. Every column needs a name; "
                    + "it becomes the pin name on the testbench symbol.");

            if (name[0] == '?')
                throw new FormatException(
                    $"Line {headerLine + 1}: column \u201c{name}\u201d starts with '?', which is "
                    + "reserved for expected-value columns. That feature is not implemented yet -- "
                    + "rename the column to drive it instead.");

            if (!seen.Add(name))
                throw new FormatException(
                    $"Line {headerLine + 1}: column name \u201c{name}\u201d appears more than once. "
                    + "Column names must be unique -- each one becomes a separate pin. "
                    + "(Comparison is case-sensitive.)");
        }

        // ------------------------------------------------------------ rows
        var values = new List<Signal>();
        int rowCount = 0;

        for (int i = headerLine + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;

            string[] fields = SplitAndTrim(lines[i]);
            if (fields.Length != names.Length)
                throw new FormatException(
                    $"Line {i + 1}: {fields.Length} value(s) but {names.Length} column(s). "
                    + "Every row must have exactly one value per column.");

            for (int c = 0; c < fields.Length; c++)
                values.Add(ParseCell(fields[c], i + 1, names[c]));

            rowCount++;
        }

        if (rowCount == 0)
            throw new FormatException(
                "The testbench program has column names but no rows of values.");

        return new TestbenchProgram(names, rowCount, values.ToArray());
    }

    private static Signal ParseCell(string text, int lineNumber, string columnName)
    {
        if (text.Length == 1)
        {
            switch (text[0])
            {
                case 'H': case 'h': return Signal.High;
                case 'L': case 'l': return Signal.Low;
                case 'Z': case 'z': return Signal.HighZ;
            }
        }

        throw new FormatException(
            $"Line {lineNumber}, column \u201c{columnName}\u201d: \u201c{text}\u201d is not a valid "
            + "value. Use H (drive high), L (drive low) or Z (release the pin).");
    }

    private static string[] SplitAndTrim(string line)
    {
        string[] parts = line.Split(',');
        for (int i = 0; i < parts.Length; i++)
            parts[i] = parts[i].Trim();
        return parts;
    }
}
