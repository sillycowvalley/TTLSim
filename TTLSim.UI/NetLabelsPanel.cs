using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using TTLSim.UI.Components;
using TTLSim.UI.Model;

namespace TTLSim.UI.View;

/// <summary>
/// Docks at the bottom of the left (Components) panel. Lists every net-label
/// NAME in the schematic with the number of labels carrying it, expandable to
/// the individual labels.
///
/// <list type="bullet">
///   <item>Click a NAME row -- selects every label carrying that name (the view
///   does not move; the labels may be scattered across the sheet).</item>
///   <item>Click a LABEL row -- selects that one label and centres the view on
///   it. This is the "step through the instances" navigation, done by picking
///   rather than by cycling.</item>
/// </list>
///
/// <para>Errors render RED, and selecting a red row puts the full description in
/// the status bar (via <see cref="StatusRequested"/>). Three defect classes fall
/// straight out of the data: a name that TIES NOTHING (no bit tapped twice --
/// includes unnamed labels), a name with LONE BITS (some bits tapped only once
/// -- a bus port whose width or start bit doesn't line up with its partner), and
/// a NAME COLLISION ("D0" vs "D" bit 0: visually identical renderings that are
/// different nets). A label row is itself red when any bit IT carries is a lone
/// tap, so inside a red group the broken instance is the red child.</para>
///
/// <para>The list is schematic-wide and ignores layer visibility, matching
/// <see cref="NetLabelIndex"/> and the label's own naming probes: hiding a layer
/// must not silently change what a name means. Labels on a hidden layer are
/// greyed and cannot be selected, matching the canvas's activity rule.</para>
/// </summary>
public sealed class NetLabelsPanel : UserControl
{
    private readonly SchematicCanvas canvas;
    private readonly TreeView tree;
    private readonly Label header;
    private readonly Font regularFont;
    private readonly Font boldFont;

    private static readonly Color ErrorColor = Color.FromArgb(180, 30, 30);
    private static readonly Color HiddenColor = SystemColors.GrayText;

    /// <summary>
    /// Raised when the panel wants a message shown in the status bar: the
    /// selected row's summary, including the full error description for a red
    /// row. Fired AFTER the canvas selection is applied, so it lands on top of
    /// the generic "Selected: N items" text the SelectionChanged handler wrote.
    /// </summary>
    public event EventHandler<string>? StatusRequested;

    // Guards the tree's AfterSelect while we rebuild or sync rows, so a
    // programmatic selection never bounces back into the canvas.
    private bool syncing;

    // Structural signature of the last build. The panel is refreshed on every
    // selection change (which is also every model edit, since UndoStack.Changed
    // funnels through SelectionChanged), so a cheap signature check keeps the
    // tree from being torn down and rebuilt on mere clicks.
    private string signature = "\u0000";

    public NetLabelsPanel(SchematicCanvas canvas)
    {
        this.canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));

        regularFont = new Font("Segoe UI", 9f);
        boldFont = new Font("Segoe UI", 9f, FontStyle.Bold);

        tree = new TreeView
        {
            Dock = DockStyle.Fill,
            HideSelection = false,
            ShowLines = true,
            ShowPlusMinus = true,
            ShowRootLines = true,
            ItemHeight = 20,
            Font = regularFont
        };
        tree.AfterSelect += OnAfterSelect;

        header = new Label
        {
            Text = "Net Labels",
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            BackColor = SystemColors.ControlLight,
            Font = boldFont
        };

        Controls.Add(tree);
        Controls.Add(header);

        RefreshLabels();
    }

    /// <summary>
    /// Rebuild the tree from the schematic if its net labels have changed since
    /// the last build; otherwise just re-sync the highlighted row. Cheap enough
    /// to call on every selection change.
    /// </summary>
    public void OnSelectionChanged()
    {
        var groups = NetLabelIndex.Build(canvas.Schematic);
        string sig = Signature(groups);
        if (sig != signature)
            Rebuild(groups, sig);

        SyncSelectionFromCanvas();
    }

    /// <summary>
    /// Force a full rebuild -- used after the model is replaced wholesale
    /// (File-New / Open), where the signature could coincidentally match.
    /// </summary>
    public void RefreshLabels()
    {
        var groups = NetLabelIndex.Build(canvas.Schematic);
        Rebuild(groups, Signature(groups));
        SyncSelectionFromCanvas();
    }

    // ------------------------------------------------------------------ build

    private void Rebuild(List<NetLabelGroup> groups, string sig)
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        foreach (TreeNode node in tree.Nodes)
            if (node.IsExpanded && node.Tag is NetLabelGroup g)
                expanded.Add(g.Name);

        syncing = true;
        tree.BeginUpdate();
        try
        {
            tree.Nodes.Clear();

            int errorCount = 0;
            foreach (var group in groups)
            {
                var groupNode = tree.Nodes.Add($"{group.DisplayName}  \u00d7{group.Count}");
                groupNode.Tag = group;

                if (group.HasError)
                {
                    groupNode.ForeColor = ErrorColor;
                    errorCount++;
                }

                foreach (var label in group.Labels)
                {
                    var labelNode = groupNode.Nodes.Add(LabelText(group, label));
                    labelNode.Tag = label;

                    if (!canvas.Schematic.IsItemActive(label))
                        labelNode.ForeColor = HiddenColor;
                    else if (LabelHasLoneBit(group, label))
                        labelNode.ForeColor = ErrorColor;
                }

                // Errors auto-expand so the broken instance is visible without
                // hunting; healthy groups keep whatever the user had.
                if (group.HasError || expanded.Contains(group.Name))
                    groupNode.Expand();
            }

            header.Text = groups.Count == 0
                ? "Net Labels"
                : errorCount == 0
                    ? $"Net Labels  ({groups.Count} name{(groups.Count == 1 ? "" : "s")})"
                    : $"Net Labels  ({groups.Count} name{(groups.Count == 1 ? "" : "s")}, {errorCount} with errors)";
            header.ForeColor = errorCount == 0 ? SystemColors.ControlText : ErrorColor;
        }
        finally
        {
            tree.EndUpdate();
            syncing = false;
        }

        signature = sig;
    }

    /// <summary>True when any bit THIS label's pins carry is tapped only once
    /// in the whole schematic -- the per-instance version of the group's
    /// lone-bit error, so the red child inside a red group is the broken one.</summary>
    private static bool LabelHasLoneBit(NetLabelGroup group, NetLabelItem label)
    {
        if (group.IsUnnamed) return true;   // unnamed always ties nothing
        foreach (var pin in label.Pins)
        {
            int bit = label.BitOfPin(pin.Number);
            if (group.PinsPerBit.TryGetValue(bit, out int n) && n < 2)
                return true;
        }
        return false;
    }

    /// <summary>
    /// One label's row: its displayed name (exactly as the canvas draws it for a
    /// width-1 label, or the bracketed range for a bus port), its grid position,
    /// and its layer when that is not the Default.
    /// </summary>
    private string LabelText(NetLabelGroup group, NetLabelItem label)
    {
        string name = group.IsUnnamed ? "?" : label.Label;
        int lo = label.StartBit;
        int hi = label.StartBit + label.Width - 1;

        string text = label.Width == 1
            ? label.BitName(1)
            : $"{name}[{lo}..{hi}]";

        var sb = new StringBuilder(text);
        sb.Append("   (").Append(label.Position.X).Append(", ").Append(label.Position.Y).Append(')');

        int layerId = label.LayerId;
        if (layerId > 0 && layerId < canvas.Schematic.Layers.Count)
            sb.Append("   \u00b7 ").Append(canvas.Schematic.Layers[layerId].Name);

        return sb.ToString();
    }

    /// <summary>
    /// Structural fingerprint of the labels: rebuild only when one of these
    /// changes. Position is included so a moved label's row keeps its
    /// coordinates honest.
    /// </summary>
    private static string Signature(List<NetLabelGroup> groups)
    {
        var sb = new StringBuilder();
        foreach (var group in groups)
        {
            sb.Append(group.Name).Append('|');
            foreach (var label in group.Labels)
            {
                sb.Append(label.Id).Append(':')
                  .Append(label.StartBit).Append(':')
                  .Append(label.Width).Append(':')
                  .Append(label.LayerId).Append(':')
                  .Append(label.Position.X).Append(',').Append(label.Position.Y)
                  .Append(';');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ selection

    private void OnAfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (syncing) return;
        if (e.Node is null) return;

        switch (e.Node.Tag)
        {
            case NetLabelItem label:
                {
                    // One label: select it and bring it into view. This is the
                    // step-to-instance navigation. The group (the parent node's
                    // tag) supplies the diagnostic for the status bar.
                    canvas.SelectItems(new[] { (SchematicItem)label }, center: true);

                    var group = e.Node.Parent?.Tag as NetLabelGroup;
                    StatusRequested?.Invoke(this, LabelStatus(group, label));
                    break;
                }

            case NetLabelGroup group:
                {
                    // Every label carrying this name. No centring -- they can be
                    // anywhere on the sheet, and a centroid of scattered labels
                    // would land on empty grid.
                    canvas.SelectItems(group.Labels.Cast<SchematicItem>(), center: false);

                    string status = $"{group.DisplayName}: {group.Count} label{(group.Count == 1 ? "" : "s")}";
                    if (group.Diagnostic is { } diag)
                        status = $"ERROR — {diag}";
                    StatusRequested?.Invoke(this, status);
                    break;
                }
        }
    }

    /// <summary>Status-bar text for one selected label row: what it is, plus
    /// its group's full error description when there is one.</summary>
    private static string LabelStatus(NetLabelGroup? group, NetLabelItem label)
    {
        string shown = label.Width == 1
            ? label.BitName(1)
            : $"{(string.IsNullOrWhiteSpace(label.Label) ? "?" : label.Label)}"
              + $"[{label.StartBit}..{label.StartBit + label.Width - 1}]";

        if (group?.Diagnostic is { } diag)
            return $"Net Label {shown} — ERROR — {diag}";

        return group is null
            ? $"Net Label {shown}"
            : $"Net Label {shown} — {group.Count} label{(group.Count == 1 ? "" : "s")} named \"{group.Name}\"";
    }

    /// <summary>
    /// Mirror the canvas selection into the tree: when exactly one net label is
    /// selected, highlight its row (expanding its name). Any other selection
    /// leaves the tree alone rather than clearing it -- the highlight is a
    /// convenience, not a second source of truth.
    /// </summary>
    private void SyncSelectionFromCanvas()
    {
        var selected = canvas.Schematic.Selected.OfType<NetLabelItem>().ToList();
        if (selected.Count != 1) return;

        NetLabelItem target = selected[0];
        foreach (TreeNode groupNode in tree.Nodes)
        {
            foreach (TreeNode labelNode in groupNode.Nodes)
            {
                if (!ReferenceEquals(labelNode.Tag, target)) continue;
                if (ReferenceEquals(tree.SelectedNode, labelNode)) return;

                syncing = true;
                try
                {
                    groupNode.Expand();
                    tree.SelectedNode = labelNode;
                    labelNode.EnsureVisible();
                }
                finally { syncing = false; }
                return;
            }
        }
    }
}