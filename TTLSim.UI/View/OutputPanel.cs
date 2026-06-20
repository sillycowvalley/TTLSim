using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using TTLSim.Core;

namespace TTLSim.UI.View;

/// <summary>
/// Docked panel that lists build diagnostics. Selecting a row that has
/// a locator (ItemId) raises LocateRequested so the host form can pan
/// the canvas and select the offending item.
/// </summary>
public sealed class OutputPanel : Panel
{
    private readonly ListView list;
    private readonly Label header;

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

            int errors = 0, warnings = 0;
            foreach (var d in diagnostics)
            {
                if (d.Severity == DiagnosticSeverity.Error) errors++;
                else if (d.Severity == DiagnosticSeverity.Warning) warnings++;
            }
            header.Text = $"{headerLabel}  —  {errors} error(s), {warnings} warning(s)";
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