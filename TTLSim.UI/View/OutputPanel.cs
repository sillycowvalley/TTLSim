using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using TTLSim.Core;

namespace TTLSim.UI.View;

/// <summary>
/// Docked panel that lists build diagnostics. Selecting a row that has
/// a locator (ItemId) raises LocateRequested so the host form can pan
/// the canvas and select the offending item.
///
/// The header carries a Copy button that puts every displayed diagnostic
/// on the clipboard as tab-separated text (severity, code, message,
/// location), preceded by the header summary line -- pasteable into a
/// bug report, a chat, or a spreadsheet. Ctrl+C in the list copies the
/// selected row, or everything when nothing is selected.
/// </summary>
public sealed class OutputPanel : Panel
{
    private readonly ListView list;
    private readonly Label header;
    private readonly ToolTip toolTip = new();

    /// <summary>Raised when the user activates a diagnostic that has a locator.</summary>
    public event EventHandler<DiagnosticLocateEventArgs>? LocateRequested;

    public OutputPanel()
    {
        Dock = DockStyle.Bottom;
        Height = 180;

        header = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Text = "Output",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            BackColor = SystemColors.ControlLight,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };

        var closeButton = new Button
        {
            Text = "×",
            Dock = DockStyle.Right,
            Width = 22,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            TabStop = false,
            BackColor = SystemColors.ControlLight
        };
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.Click += (_, _) => Visible = false;
        header.Controls.Add(closeButton);

        // Added after the close button, so it docks to its left.
        var copyButton = new Button
        {
            Text = "Copy",
            Dock = DockStyle.Right,
            Width = 46,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8f),
            TabStop = false,
            BackColor = SystemColors.ControlLight
        };
        copyButton.FlatAppearance.BorderSize = 0;
        copyButton.Click += (_, _) => CopyToClipboard(selectedOnly: false);
        toolTip.SetToolTip(copyButton, "Copy all diagnostics to the clipboard as text");
        header.Controls.Add(copyButton);

        list = new ListView
        {
            Dock = DockStyle.Fill,
            View = System.Windows.Forms.View.Details,
            FullRowSelect = true,
            GridLines = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            MultiSelect = false,
            Font = new Font("Segoe UI", 9f)
        };
        list.Columns.Add("", 24);              // severity glyph
        list.Columns.Add("Code", 60);
        list.Columns.Add("Message", 700);
        list.Columns.Add("Location", 140);

        list.MouseDoubleClick += (_, _) => RaiseLocate();
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                RaiseLocate();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                // Selected row when there is one; the whole pane otherwise.
                CopyToClipboard(selectedOnly: list.SelectedItems.Count > 0);
                e.Handled = true;
            }
        };

        Controls.Add(list);
        Controls.Add(header);
    }

    /// <summary>Replace the displayed diagnostics with a fresh build result.</summary>
    public void Show(BuildResult result)
        => Show("Output", result.Diagnostics);

    /// <summary>
    /// Replace the displayed diagnostics with an arbitrary list. The header
    /// label is shown to the left of the error/warning count; use it to
    /// identify which operation produced the diagnostics (e.g.
    /// "Export EasyEDA"). Used by callers that don't have a BuildResult.
    /// </summary>
    public void Show(string headerLabel, IReadOnlyList<Diagnostic> diagnostics)
    {
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (Diagnostic d in diagnostics)
            {
                ListViewItem row = new(SeverityGlyph(d.Severity));
                row.UseItemStyleForSubItems = false;
                row.SubItems.Add(d.Code);
                row.SubItems.Add(d.Message);
                row.SubItems.Add(LocationText(d));

                row.ForeColor = d.Severity switch
                {
                    DiagnosticSeverity.Error => Color.FromArgb(0xB0, 0x10, 0x10),
                    DiagnosticSeverity.Warning => Color.FromArgb(0xA0, 0x70, 0x00),
                    _ => SystemColors.WindowText
                };

                row.Tag = d;
                list.Items.Add(row);
            }

            int errors = 0, warnings = 0, infos = 0;
            foreach (var d in diagnostics)
            {
                if (d.Severity == DiagnosticSeverity.Error) errors++;
                else if (d.Severity == DiagnosticSeverity.Warning) warnings++;
                else infos++;
            }
            header.Text = infos > 0
                ? $"{headerLabel}  —  {errors} error(s), {warnings} warning(s), {infos} info(s)"
                : $"{headerLabel}  —  {errors} error(s), {warnings} warning(s)";
        }
        finally
        {
            list.EndUpdate();
        }
    }

    /// <summary>Clear all diagnostics.</summary>
    public void Clear()
    {
        list.Items.Clear();
        header.Text = "Output";
    }

    // ------------------------------------------------------------- copy

    /// <summary>
    /// Put the pane's diagnostics on the clipboard as plain text: the
    /// header summary line, then one tab-separated line per diagnostic
    /// (severity, code, message, location). Tab separation pastes cleanly
    /// into spreadsheets and stays readable as text.
    /// </summary>
    private void CopyToClipboard(bool selectedOnly)
    {
        IEnumerable<ListViewItem> rows = selectedOnly
            ? SelectedRows()
            : AllRows();

        var sb = new StringBuilder();
        sb.AppendLine(header.Text);
        int count = 0;
        foreach (ListViewItem row in rows)
        {
            if (row.Tag is not Diagnostic d) continue;
            sb.Append(SeverityWord(d.Severity)).Append('\t')
              .Append(d.Code).Append('\t')
              .Append(d.Message).Append('\t')
              .AppendLine(LocationText(d));
            count++;
        }
        if (count == 0) return;   // nothing to export; leave the clipboard alone

        try
        {
            Clipboard.SetText(sb.ToString());
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process holds the clipboard open. Rare, transient,
            // and not worth a dialog -- the user can just click again.
        }
    }

    private IEnumerable<ListViewItem> AllRows()
    {
        foreach (ListViewItem row in list.Items) yield return row;
    }

    private IEnumerable<ListViewItem> SelectedRows()
    {
        foreach (ListViewItem row in list.SelectedItems) yield return row;
    }

    private static string SeverityWord(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        _ => "info"
    };

    // ------------------------------------------------------------- locate

    private void RaiseLocate()
    {
        if (list.SelectedItems.Count == 0) return;
        if (list.SelectedItems[0].Tag is not Diagnostic d) return;
        // Fire if ANY locator is present -- single-target (ItemId) or
        // multi-target (ItemIds / ConnectionIds). The handler decides
        // how to combine them.
        bool hasLocator = d.ItemId is not null
            || (d.ItemIds is { Count: > 0 })
            || (d.ConnectionIds is { Count: > 0 })
            || d.NetId is not null
            || d.GridPoint is not null;
        if (!hasLocator) return;
        LocateRequested?.Invoke(this, new DiagnosticLocateEventArgs(d));
    }

    private static string SeverityGlyph(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Error => "⊘",
        DiagnosticSeverity.Warning => "△",
        _ => "·"
    };

    private static string LocationText(Diagnostic d)
    {
        if (d.PinNumber is int p && d.ItemId is not null) return $"pin {p}";
        if (d.NetId is int n) return $"net {n}";
        if (d.GridPoint is { } g) return $"({g.X},{g.Y})";
        return "";
    }
}

public sealed class DiagnosticLocateEventArgs : EventArgs
{
    public Diagnostic Diagnostic { get; }
    public DiagnosticLocateEventArgs(Diagnostic d) { Diagnostic = d; }
}