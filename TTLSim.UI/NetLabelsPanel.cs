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
/// <para>Two defect classes fall straight out of the data and are coloured:
/// a name whose every bit is tapped only once TIES NOTHING (red) -- almost
/// always a typo, or a label placed and then forgotten -- and a name where SOME
/// bits are lone taps (amber) -- typically a bus port whose width or start bit
/// does not line up with its partner. Unnamed labels tie nothing by definition
/// and are grouped first.</para>
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

    // Colours for the two defect classes, plus the greyed hidden-layer state.
    private static readonly Color TiesNothingColor = Color.FromArgb(180, 30, 30);
    private static readonly Color LoneBitsColor = Color.FromArgb(170, 105, 0);
    private static readonly Color HiddenColor = SystemColors.GrayText;

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
            ShowNodeToolTips = true,
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

            foreach (var group in groups)
            {
                var groupNode = tree.Nodes.Add($"{group.DisplayName}  \u00d7{group.Count}");
                groupNode.Tag = group;
                groupNode.ToolTipText = GroupToolTip(group);

                if (group.TiesNothing) groupNode.ForeColor = TiesNothingColor;
                else if (group.HasLoneBits) groupNode.ForeColor = LoneBitsColor;

                foreach (var label in group.Labels)
                {
                    var labelNode = groupNode.Nodes.Add(LabelText(group, label));
                    labelNode.Tag = label;
                    if (!canvas.Schematic.IsItemActive(label))
                    {
                        labelNode.ForeColor = HiddenColor;
                        labelNode.ToolTipText = "On a hidden layer -- shown for naming, not selectable.";
                    }
                }

                if (expanded.Contains(group.Name))
                    groupNode.Expand();
            }

            header.Text = groups.Count == 0
                ? "Net Labels"
                : $"Net Labels  ({groups.Count} name{(groups.Count == 1 ? "" : "s")})";
        }
        finally
        {
            tree.EndUpdate();
            syncing = false;
        }

        signature = sig;
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

    private static string GroupToolTip(NetLabelGroup group)
    {
        if (group.IsUnnamed)
            return "Unnamed labels tie nothing. Give each a name, or delete it.";

        var sb = new StringBuilder();
        sb.Append(group.Count).Append(group.Count == 1 ? " label carries " : " labels carry ")
          .Append('"').Append(group.Name).Append('"').Append('.');

        if (group.TiesNothing)
        {
            sb.AppendLine().Append("Ties nothing: no bit of this name is tapped twice.");
        }
        else
        {
            var lone = group.LoneBits.ToList();
            if (lone.Count > 0)
            {
                sb.AppendLine().Append("Tapped only once: bit")
                  .Append(lone.Count == 1 ? " " : "s ")
                  .Append(string.Join(", ", lone))
                  .Append(" -- these tie nothing.");
            }
        }
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
                // One label: select it and bring it into view. This is the
                // step-to-instance navigation.
                canvas.SelectItems(new[] { (SchematicItem)label }, center: true);
                break;

            case NetLabelGroup group:
                // Every label carrying this name. No centring -- they can be
                // anywhere on the sheet, and a centroid of scattered labels
                // would land on empty grid.
                canvas.SelectItems(group.Labels.Cast<SchematicItem>(), center: false);
                break;
        }
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
