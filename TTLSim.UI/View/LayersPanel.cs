using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using TTLSim.UI.Model;

namespace TTLSim.UI.View;

/// <summary>
/// Docks at the bottom of the right Properties panel. Lists the schematic's
/// layers and lets the user:
///   - toggle a layer's visibility (the checkbox; Default is pinned visible),
///   - pick the current layer (select a row -- new drops land there),
///   - move the current selection onto the current layer (undoable),
///   - add / rename / delete layers.
///
/// Visibility and table edits are view state (no undo), matching the model.
/// Only "Move Sel" is undoable, routed through the canvas (SetLayerCommand).
/// Names are always read from <see cref="Schematic.Layers"/>; the row text only
/// decorates them with a current-layer marker, so a rename never picks up the
/// marker glyph.
/// </summary>
public sealed class LayersPanel : UserControl
{
    private readonly SchematicCanvas canvas;
    private readonly ListView listView;
    private readonly Button addButton;
    private readonly Button renameButton;
    private readonly Button deleteButton;
    private readonly Button moveHereButton;
    private readonly Font regularFont;
    private readonly Font boldFont;
    private readonly ToolTip toolTip = new();

    // Guards the ListView event handlers while we rebuild rows, so programmatic
    // Checked / Selected changes during a refresh don't fire model mutations.
    private bool populating;

    public LayersPanel(SchematicCanvas canvas)
    {
        this.canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));

        var header = new Label
        {
            Text = "Layers",
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            BackColor = SystemColors.ControlLight,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4, 2, 4, 2)
        };
        addButton = MakeButton("Add", OnAdd);
        renameButton = MakeButton("Rename", OnRename);
        deleteButton = MakeButton("Delete", OnDelete);
        moveHereButton = MakeButton("Move", OnMoveSelectionHere);
        toolTip.SetToolTip(moveHereButton, "Move selection to current layer");
        toolbar.Controls.Add(addButton);
        toolbar.Controls.Add(renameButton);
        toolbar.Controls.Add(deleteButton);
        toolbar.Controls.Add(moveHereButton);

        listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = System.Windows.Forms.View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.None,
            MultiSelect = false,
            HideSelection = false,
            LabelEdit = false
        };
        listView.Columns.Add("Layer");
        listView.ItemCheck += OnItemCheck;
        listView.ItemChecked += OnItemChecked;
        listView.SelectedIndexChanged += OnSelectedLayerChanged;
        listView.Resize += (_, _) => FitColumn();

        regularFont = new Font(listView.Font, FontStyle.Regular);
        boldFont = new Font(listView.Font, FontStyle.Bold);

        // Fill first (lowest z-order), then the docked edges, so the list claims
        // what's left between the header and toolbar.
        Controls.Add(listView);
        Controls.Add(toolbar);
        Controls.Add(header);

        RefreshLayers();
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = false,
            Width = 58,
            Height = 24,
            Margin = new Padding(2, 0, 2, 0),
            FlatStyle = FlatStyle.System
        };
        b.Click += onClick;
        return b;
    }

    private void FitColumn()
    {
        if (listView.Columns.Count > 0)
            listView.Columns[0].Width = listView.ClientSize.Width - 4;
    }

    /// <summary>
    /// Rebuild the row list from the schematic's current layers. Call after the
    /// schematic is replaced (File-New / Open) or after any layer-table change.
    /// </summary>
    public void RefreshLayers()
    {
        populating = true;
        listView.BeginUpdate();
        listView.Items.Clear();

        var layers = canvas.Schematic.Layers;

        // Keep the current-layer index in range (a delete may have shrunk the
        // table); fall back to Default.
        if (canvas.CurrentLayerId < 0 || canvas.CurrentLayerId >= layers.Count)
            canvas.CurrentLayerId = 0;

        for (int i = 0; i < layers.Count; i++)
            listView.Items.Add(new ListViewItem(layers[i].Name) { Checked = layers[i].Visible });

        if (listView.Items.Count > 0)
            listView.Items[canvas.CurrentLayerId].Selected = true;

        listView.EndUpdate();
        populating = false;

        FitColumn();
        UpdateCurrentMarkersAndButtons();
    }

    /// <summary>
    /// Refresh row decoration (current-layer marker / bold) and button enable
    /// state. Also called by the host when the canvas selection changes, so the
    /// "Move Sel" button tracks whether anything is selected.
    /// </summary>
    public void OnSelectionChanged() => UpdateCurrentMarkersAndButtons();

    private void UpdateCurrentMarkersAndButtons()
    {
        int current = canvas.CurrentLayerId;
        var layers = canvas.Schematic.Layers;

        for (int i = 0; i < listView.Items.Count && i < layers.Count; i++)
        {
            bool isCurrent = i == current;
            var it = listView.Items[i];
            it.Text = (isCurrent ? "\u25B6 " : "    ") + layers[i].Name;
            it.Font = isCurrent ? boldFont : regularFont;
        }

        bool currentIsDefault = current == 0;
        renameButton.Enabled = !currentIsDefault;
        deleteButton.Enabled = !currentIsDefault && layers.Count > 1;
        moveHereButton.Enabled = canvas.Schematic.Selected.Any();
    }

    // -------------------------------------------------------- list events

    private void OnItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (populating) return;
        // Default (index 0) is pinned visible -- veto any attempt to uncheck it.
        if (e.Index == 0 && e.NewValue == CheckState.Unchecked)
            e.NewValue = CheckState.Checked;
    }

    private void OnItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (populating) return;
        canvas.SetLayerVisible(e.Item.Index, e.Item.Checked);
    }

    private void OnSelectedLayerChanged(object? sender, EventArgs e)
    {
        if (populating) return;
        if (listView.SelectedIndices.Count == 0) return;
        canvas.CurrentLayerId = listView.SelectedIndices[0];
        UpdateCurrentMarkersAndButtons();
    }

    // -------------------------------------------------------- toolbar actions

    private void OnAdd(object? sender, EventArgs e)
    {
        string? name = Prompt("New layer name:", canvas.Schematic.UniqueLayerName("Layer"));
        if (name is null) return;
        int index = canvas.Schematic.AddLayer(name);
        canvas.CurrentLayerId = index;   // new items land on the layer just added
        RefreshLayers();
    }

    private void OnRename(object? sender, EventArgs e)
    {
        int index = canvas.CurrentLayerId;
        if (index == 0) return;          // Default not renamable
        string? name = Prompt("Rename layer:", canvas.Schematic.Layers[index].Name);
        if (name is null) return;
        canvas.Schematic.RenameLayer(index, name);
        RefreshLayers();
    }

    private void OnDelete(object? sender, EventArgs e)
    {
        int index = canvas.CurrentLayerId;
        if (index == 0) return;          // Default not deletable
        string layerName = canvas.Schematic.Layers[index].Name;

        var answer = MessageBox.Show(
            $"Delete layer \u201c{layerName}\u201d? Items on it move to Default. " +
            "This cannot be undone.",
            "Delete Layer", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (answer != DialogResult.OK) return;

        canvas.Schematic.DeleteLayer(index);
        canvas.CurrentLayerId = 0;       // the deleted layer was current; fall back to Default
        canvas.Invalidate();             // orphaned items reassigned to Default are visible again
        RefreshLayers();
    }

    private void OnMoveSelectionHere(object? sender, EventArgs e)
    {
        canvas.MoveSelectionToLayer(canvas.CurrentLayerId);
        RefreshLayers();
    }

    /// <summary>
    /// Minimal modal text prompt. Returns the trimmed text, or null if the user
    /// cancelled or left it blank.
    /// </summary>
    private string? Prompt(string label, string initial)
    {
        using var dlg = new Form
        {
            Text = "Layer",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(284, 100),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };
        var promptLabel = new Label { Text = label, Left = 10, Top = 12, Width = 264, AutoSize = false };
        var box = new TextBox { Text = initial, Left = 10, Top = 34, Width = 264 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 118, Top = 64, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 199, Top = 64, Width = 75 };

        dlg.Controls.Add(promptLabel);
        dlg.Controls.Add(box);
        dlg.Controls.Add(ok);
        dlg.Controls.Add(cancel);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        box.SelectAll();
        bool accepted = dlg.ShowDialog(FindForm()) == DialogResult.OK;
        return accepted && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            regularFont?.Dispose();
            boldFont?.Dispose();
            toolTip?.Dispose();
        }
        base.Dispose(disposing);
    }
}