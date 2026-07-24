using System;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using System.Collections.Generic;
using TTLSim.UI.Commands;
using TTLSim.UI.Model;
using TTLSim.UI.View;
using TTLSim.UI.Sim;
using TTLSim.UI.Logging;

using Microsoft.Extensions.Logging;

namespace TTLSim.UI;

public sealed class MainForm : Form
{
    private readonly SchematicCanvas canvas;
    private readonly LibraryPanel library;
    private readonly EnterAwarePropertyGrid propertyGrid;
    private readonly LayersPanel layersPanel;
    private readonly NetLabelsPanel netLabelsPanel;
    private readonly StatusStrip statusStrip;
    private readonly ToolStripStatusLabel coordsLabel;
    private readonly ToolStripStatusLabel zoomLabel;
    private readonly ToolStripStatusLabel selectionLabel;
    private readonly OutputPanel outputPanel;

    private readonly SimulationController simController;
    private ToolStripButton buildButton = null!;
    private ToolStripButton runButton = null!;
    private ToolStripButton pauseButton = null!;
    private ToolStripButton stopButton = null!;
    private ToolStripLabel simTimeLabel = null!;
    private ToolStripLabel speedLabel = null!;

    // Edit-menu items whose enabled state depends on selection / clipboard.
    // Held as fields so the SelectionChanged handler and the Edit menu's
    // DropDownOpening handler can refresh them.
    private ToolStripMenuItem cutItem = null!;
    private ToolStripMenuItem copyItem = null!;
    private ToolStripMenuItem pasteItem = null!;
    private ToolStripMenuItem importHexItem = null!;
    private ToolStripMenuItem exportHexItem = null!;
    private ToolStripMenuItem importJedecItem = null!;

    private ICommand? savedAtCommand;

    public MainForm()
    {
        RecentFolders.Load();

        Text = "TTL Schematic Editor";

        // Restore size & position if we have one that still lands on a visible screen,
        // otherwise fall back to centred 1400x900.
        var restored = TryRestoreWindowBounds();
        if (!restored)
        {
            Width = 1400;
            Height = 900;
            StartPosition = FormStartPosition.CenterScreen;
        }

        Width = 1400;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;

        Log.Initialize();
        var log = Log.For<MainForm>();
        log.LogInformation("TTLSim starting");

        canvas = new SchematicCanvas { Dock = DockStyle.Fill };

        simController = new SimulationController(canvas.Schematic);
        simController.StateChanged += OnSimStateChanged;
        simController.Ticked += (_, _) =>
        {
            canvas.Invalidate();
            UpdateSimTimeLabel();
        };

        outputPanel = new OutputPanel();
        outputPanel.Visible = false;

        propertyGrid = new EnterAwarePropertyGrid
        {
            Dock = DockStyle.Fill,
            ToolbarVisible = false,
            PropertySort = PropertySort.Categorized
        };
        propertyGrid.EnterPressed += (_, _) => canvas.Focus();

        var menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("&File");
        var newItem = new ToolStripMenuItem("&New", null, OnNew)
        { ShortcutKeys = Keys.Control | Keys.N };
        var openItem = new ToolStripMenuItem("&Open...", null, OnOpen)
        { ShortcutKeys = Keys.Control | Keys.O };
        var saveItem = new ToolStripMenuItem("&Save", null, OnSave)
        { ShortcutKeys = Keys.Control | Keys.S };
        var saveAsItem = new ToolStripMenuItem("Save &As...", null, OnSaveAs)
        { ShortcutKeys = Keys.Control | Keys.Shift | Keys.S };
        var exportEasyEDAItem = new ToolStripMenuItem("&Export EasyEDA...", null, OnExportEasyEDA);
        var exportLabelsItem = new ToolStripMenuItem("Export Chi&p Labels...", null, OnExportChipLabels);
        importHexItem = new ToolStripMenuItem("&Import HEX into EEPROM...", null, OnImportHex);
        exportHexItem = new ToolStripMenuItem("E&xport EEPROM HEX...", null, OnExportHex);
        importJedecItem = new ToolStripMenuItem("Import &JEDEC into GAL...", null, OnImportJedec);
        var exitItem = new ToolStripMenuItem("E&xit", null, (_, _) => Close());
        fileMenu.DropDownItems.Add(newItem);
        fileMenu.DropDownItems.Add(openItem);
        fileMenu.DropDownItems.Add(saveItem);
        fileMenu.DropDownItems.Add(saveAsItem);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(exportEasyEDAItem);
        fileMenu.DropDownItems.Add(exportLabelsItem);
        fileMenu.DropDownItems.Add(importHexItem);
        fileMenu.DropDownItems.Add(exportHexItem);
        fileMenu.DropDownItems.Add(importJedecItem);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(exitItem);
        fileMenu.DropDownOpening += (_, _) => { UpdateHexMenuItems(); UpdateGalMenuItems(); };
        menu.Items.Add(fileMenu);

        var viewMenu = new ToolStripMenuItem("&View");
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("&Reset View (Home)", null, (_, _) => canvas!.ResetView()));
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("&Fit", null, (_, _) => canvas!.FitView())
        {
            ShortcutKeys = Keys.Control | Keys.Back
        });
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        var outputMenuItem = new ToolStripMenuItem("&Output Panel", null,
            (_, _) => outputPanel.Visible = !outputPanel.Visible)
        {
            CheckOnClick = true,
            Checked = true,
            ShortcutKeys = Keys.Control | Keys.W
        };
        viewMenu.DropDownItems.Add(outputMenuItem);
        menu.Items.Add(viewMenu);

        outputPanel.VisibleChanged += (_, _) => outputMenuItem.Checked = outputPanel.Visible;

        var editMenu = new ToolStripMenuItem("&Edit");
        var undoItem = new ToolStripMenuItem("&Undo", null, (_, _) => canvas!.UndoStack.Undo())
        { ShortcutKeys = Keys.Control | Keys.Z };
        var redoItem = new ToolStripMenuItem("&Redo", null, (_, _) => canvas!.UndoStack.Redo())
        { ShortcutKeys = Keys.Control | Keys.Y };
        editMenu.DropDownItems.Add(undoItem);
        editMenu.DropDownItems.Add(redoItem);
        editMenu.DropDownItems.Add(new ToolStripSeparator());

        // Cut / Copy / Paste. The canvas owns the actual clipboard logic and
        // the same operations are also reachable by Ctrl+X/C/V handled in the
        // canvas's OnKeyDown -- these menu items just route to the same
        // public methods. ShortcutKeyDisplayString (rather than ShortcutKeys)
        // shows the accelerator text without registering a second handler
        // that would compete with the canvas's keyboard handling.
        cutItem = new ToolStripMenuItem("Cu&t", null, (_, _) => canvas!.Cut())
        { ShortcutKeyDisplayString = "Ctrl+X" };
        copyItem = new ToolStripMenuItem("&Copy", null, (_, _) => canvas!.Copy())
        { ShortcutKeyDisplayString = "Ctrl+C" };
        pasteItem = new ToolStripMenuItem("&Paste", null, (_, _) => canvas!.Paste())
        { ShortcutKeyDisplayString = "Ctrl+V" };
        editMenu.DropDownItems.Add(cutItem);
        editMenu.DropDownItems.Add(copyItem);
        editMenu.DropDownItems.Add(pasteItem);

        // Select All. Same pattern as Cut/Copy/Paste: Ctrl+A is handled by
        // the canvas's OnKeyDown, so the shortcut only fires when the canvas
        // has focus (a text box or the property grid keeps its own Ctrl+A);
        // the menu item just shows the accelerator text and routes to the
        // same public method.
        var selectAllItem = new ToolStripMenuItem("Select &All", null,
            (_, _) => canvas!.SelectAll())
        { ShortcutKeyDisplayString = "Ctrl+A" };
        editMenu.DropDownItems.Add(selectAllItem);
        editMenu.DropDownItems.Add(new ToolStripSeparator());

        editMenu.DropDownItems.Add(new ToolStripMenuItem("Place &Wire", null,
            (_, _) => canvas!.BeginWirePlacement()));
        editMenu.DropDownItems.Add(new ToolStripMenuItem("Place Header &Link", null,
            (_, _) => canvas!.BeginHeaderLinkPlacement()));

        // Rotate items. Space / Shift+Space are handled by the canvas's
        // OnKeyDown directly; the menu items just display the shortcut text
        // and route to the same RotateSelection method.
        var rotateCwItem = new ToolStripMenuItem("Rotate &Clockwise", null,
            (_, _) => canvas!.RotateSelection(clockwise: true))
        { ShortcutKeyDisplayString = "Space" };
        var rotateCcwItem = new ToolStripMenuItem("Rotate Counter-Clock&wise", null,
            (_, _) => canvas!.RotateSelection(clockwise: false))
        { ShortcutKeyDisplayString = "Shift+Space" };
        editMenu.DropDownItems.Add(rotateCwItem);
        editMenu.DropDownItems.Add(rotateCcwItem);

        // Refresh Paste's enabled state each time the Edit menu opens: the
        // clipboard can change from outside this app, so CanPaste can't be
        // kept current by our own events alone. Cut/Copy depend only on the
        // selection, which our SelectionChanged handler already tracks, but
        // we refresh them here too so the menu is always self-consistent.
        editMenu.DropDownOpening += (_, _) => UpdateEditMenuItems();

        menu.Items.Add(editMenu);

        var buildMenu = new ToolStripMenuItem("&Build");
        var buildItem = new ToolStripMenuItem("&Build", null, OnBuild)
        { ShortcutKeys = Keys.F6 };
        buildMenu.DropDownItems.Add(buildItem);
        menu.Items.Add(buildMenu);

        var helpMenu = new ToolStripMenuItem("&Help");

        var verboseItem = new ToolStripMenuItem("&Verbose Logging")
        {
            CheckOnClick = true,
            Checked = Log.Verbose,
            ToolTipText = "Log debug-level detail. Takes effect immediately."
        };
        verboseItem.CheckedChanged += (_, _) => Log.Verbose = verboseItem.Checked;

        helpMenu.DropDownItems.Add(verboseItem);
        helpMenu.DropDownItems.Add(new ToolStripSeparator());
        helpMenu.DropDownItems.Add(new ToolStripMenuItem("Open &Log Folder", null,
            (_, _) => System.Diagnostics.Process.Start("explorer.exe", Log.LogFolder)));

        menu.Items.Add(helpMenu);


        var simToolbar = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System
        };
        buildButton = new ToolStripButton("Build") { ToolTipText = "Compile the schematic (F6)" };
        runButton = new ToolStripButton("▶ Run") { ToolTipText = "Run simulation", Enabled = false };
        pauseButton = new ToolStripButton("⏸ Pause") { ToolTipText = "Pause", Enabled = false };
        stopButton = new ToolStripButton("⏹ Stop") { ToolTipText = "Stop and reset", Enabled = false };

        var slowerButton = new ToolStripButton("⏪") { ToolTipText = "Slower" };
        var fasterButton = new ToolStripButton("⏩") { ToolTipText = "Faster" };
        speedLabel = new ToolStripLabel("1×");
        speedLabel.AutoSize = false;
        speedLabel.Width = 50;
        speedLabel.TextAlign = ContentAlignment.MiddleCenter;

        simTimeLabel = new ToolStripLabel("Sim time: --");

        buildButton.Click += OnBuild;
        runButton.Click += (_, _) => simController.Run();
        pauseButton.Click += (_, _) => simController.Pause();
        stopButton.Click += (_, _) => simController.Stop();
        slowerButton.Click += (_, _) => StepSpeed(-1);
        fasterButton.Click += (_, _) => StepSpeed(+1);

        simToolbar.Items.Add(buildButton);
        simToolbar.Items.Add(new ToolStripSeparator());
        simToolbar.Items.Add(runButton);
        simToolbar.Items.Add(pauseButton);
        simToolbar.Items.Add(stopButton);
        simToolbar.Items.Add(new ToolStripSeparator());
        simToolbar.Items.Add(slowerButton);
        simToolbar.Items.Add(speedLabel);
        simToolbar.Items.Add(fasterButton);
        simToolbar.Items.Add(new ToolStripSeparator());
        simToolbar.Items.Add(simTimeLabel);

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5 && runButton.Enabled)
            {
                simController.Run();
                e.Handled = true;
            }
            if (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add)
            {
                StepSpeed(+1);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract)
            {
                StepSpeed(-1);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                if (simController.State == SimState.Running)
                {
                    simController.Pause();
                    e.Handled = true;
                }
                else if (simController.State == SimState.Paused || simController.State == SimState.Built)
                {
                    simController.ClearBuild();
                    e.Handled = true;
                }
                // In Edit state, let SchematicCanvas's own KeyDown handle Esc
                // (it uses it for wire-placement and marquee cancellation).
            }
        };




        canvas!.UndoStack.Changed += (_, _) =>
        {
            undoItem.Enabled = canvas.UndoStack.CanUndo;
            redoItem.Enabled = canvas.UndoStack.CanRedo;

            undoItem.Text = canvas.UndoStack.CanUndo
                ? $"&Undo {canvas.UndoStack.UndoDescription}" : "&Undo";
            redoItem.Text = canvas.UndoStack.CanRedo
                ? $"&Redo {canvas.UndoStack.RedoDescription}" : "&Redo";
            propertyGrid.Refresh();
            propertyGrid.ExpandAllGridItems();

            dirty = canvas.UndoStack.UndoTop != savedAtCommand;
            UpdateTitle();
            simController.Invalidate();
        };
        undoItem.Enabled = false;
        redoItem.Enabled = false;
        rotateCwItem.Enabled = false;
        rotateCcwItem.Enabled = false;





        library = new LibraryPanel { Width = 275 };
        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 275 };
        leftPanel.Controls.Add(library);
        var leftHeader = new Label
        {
            Text = "Components",
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            BackColor = SystemColors.ControlLight,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        leftPanel.Controls.Add(leftHeader);

        // Net Labels panel pinned to the bottom of the left (Components) column,
        // with a drag splitter above it so the library/labels split is
        // adjustable. WinForms docks in reverse add order, so the splitter is
        // ADDED FIRST and the panel second: the panel claims the bottom edge,
        // the splitter lands directly above it, and library (Fill) takes the
        // rest -- the same add-order trick as the output panel's splitter.
        var netLabelsSplitter = new Splitter
        {
            Dock = DockStyle.Bottom,
            Height = 4,
            MinExtra = 80,   // never squash the library below this
            MinSize = 60     // never squash the labels panel below this
        };
        leftPanel.Controls.Add(netLabelsSplitter);
        netLabelsPanel = new NetLabelsPanel(canvas)
        {
            Dock = DockStyle.Bottom,
            Height = 260
        };
        leftPanel.Controls.Add(netLabelsPanel);

        // A row click in the Net Labels panel puts its own summary -- including
        // the full error description for a red row -- in the status bar. This
        // fires AFTER the canvas selection is applied, so it overwrites the
        // generic "Selected: ..." text with the richer one.
        netLabelsPanel.StatusRequested += (_, msg) => selectionLabel.Text = msg;



        propertyGrid.PropertyValueChanged += (_, e) =>
        {
            // Always invalidate at the end so canvas reflects whatever the
            // PropertyGrid wrote (it has already called the setter by now).
            // We may also record an undo command if we can resolve the
            // changed property and its owning object.
            if (e.ChangedItem?.PropertyDescriptor is { } pd)
            {
                var targets = propertyGrid.SelectedObjects ?? new[] { propertyGrid.SelectedObject };
                if (targets.Length > 0)
                {
                    var propInfo = targets[0].GetType().GetProperty(pd.Name);
                    if (propInfo != null && propInfo.CanWrite)
                    {
                        if (targets.Length == 1)
                        {
                            var cmd = new SetPropertyCommand(targets[0]!, propInfo,
                                e.OldValue, propInfo.GetValue(targets[0]));
                            canvas.UndoStack.Record(cmd);
                        }
                        else
                        {
                            var newValue = propInfo.GetValue(targets[0]);
                            canvas.UndoStack.BeginComposite($"Change {pd.Name}");
                            try
                            {
                                foreach (var t in targets)
                                {
                                    if (t == null) continue;
                                    canvas.UndoStack.Record(
                                        new SetPropertyCommand(t, propInfo, e.OldValue, newValue));
                                }
                            }
                            finally { canvas.UndoStack.EndComposite(); }
                        }
                    }
                }
            }
            canvas.Invalidate();
        };


        var rightPanel = new Panel { Dock = DockStyle.Right, Width = 280 };
        rightPanel.Controls.Add(propertyGrid);
        var rightHeader = new Label
        {
            Text = "Properties",
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            BackColor = SystemColors.ControlLight,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        rightPanel.Controls.Add(rightHeader);

        // Layers panel pinned to the bottom of the right (Properties) column.
        // propertyGrid (Fill) was added first so it claims the space between
        // the header (Top) and this panel (Bottom).
        layersPanel = new LayersPanel(canvas)
        {
            Dock = DockStyle.Bottom,
            Height = 230
        };
        rightPanel.Controls.Add(layersPanel);

        var leftSplitter = new Splitter { Dock = DockStyle.Left, Width = 4 };
        var rightSplitter = new Splitter { Dock = DockStyle.Right, Width = 4 };


        outputPanel.LocateRequested += OnLocateDiagnostic;
        var outputSplitter = new Splitter { Dock = DockStyle.Bottom, Height = 4 };

        statusStrip = new StatusStrip();
        coordsLabel = new ToolStripStatusLabel("X: 0  Y: 0") { AutoSize = false, Width = 140 };
        zoomLabel = new ToolStripStatusLabel("Zoom: 400%") { AutoSize = false, Width = 120 };
        selectionLabel = new ToolStripStatusLabel("Nothing selected")
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        statusStrip.Items.Add(coordsLabel);
        statusStrip.Items.Add(zoomLabel);
        statusStrip.Items.Add(selectionLabel);

        // Add canvas FIRST so it ends up at the bottom of the Controls
        // Z-order; the edge-docked panels then claim their space and Fill
        // honours what's left. Reverse order is the common WinForms gotcha
        // that leaves Fill claiming the full client area.
        Controls.Add(canvas);
        Controls.Add(outputSplitter);
        Controls.Add(outputPanel);
        Controls.Add(leftPanel);
        Controls.Add(leftSplitter);
        Controls.Add(rightPanel);
        Controls.Add(rightSplitter);
        Controls.Add(statusStrip);
        Controls.Add(simToolbar);
        MainMenuStrip = menu;
        Controls.Add(menu);

        canvas.MouseMove += (_, e) =>
        {
            var grid = canvas.ScreenToGrid(e.Location);
            coordsLabel.Text = $"X: {grid.X}  Y: {grid.Y}";
        };
        canvas.ViewChanged += (_, _) =>
        {
            zoomLabel.Text = $"Zoom: {canvas.Zoom * 100f:0}%";
        };
        canvas.SelectionChanged += (_, _) =>
        {
            var items = canvas.Schematic.Selected.ToArray();
            var connections = canvas.Schematic.SelectedConnections.ToArray();
            var links = canvas.Schematic.SelectedLinks.ToArray();
            object[] all = items.Cast<object>().Concat(connections).Concat(links).ToArray();
            propertyGrid.SelectedObjects = all;
            propertyGrid.ExpandAllGridItems();

            int total = items.Length + connections.Length + links.Length;
            selectionLabel.Text = total switch
            {
                0 => "Nothing selected",
                1 => items.Length == 1
                    ? DescribeSingleItem(items[0])
                    : connections.Length == 1 ? "Selected: Connection" : "Selected: Header Link",
                _ => $"Selected: {total} items"
            };

            // Rotate menu items are enabled only when exactly one item is selected.
            rotateCwItem.Enabled = canvas.CanRotateSelection;
            rotateCcwItem.Enabled = canvas.CanRotateSelection;

            // Cut/Copy depend on whether anything is selected; Paste depends
            // on the clipboard. Refresh all three so the Edit menu is correct
            // even before it is next opened (DropDownOpening also refreshes,
            // which covers external clipboard changes).
            UpdateEditMenuItems();
            UpdateHexMenuItems();
            UpdateGalMenuItems();
            layersPanel.OnSelectionChanged();
            netLabelsPanel.OnSelectionChanged();
        };

        // Initial enabled state for the clipboard items, before any
        // selection change or menu open has occurred.
        UpdateEditMenuItems();
        UpdateHexMenuItems();
    }

    /// <summary>
    /// Status-bar text for a single selected item. A net label additionally
    /// reports how many labels in the schematic carry the same name, and -- when
    /// its name group has an error (ties nothing, lone bits, or a name
    /// collision like "D0" vs "D" bit 0) -- the full error description, the same
    /// text the Net Labels panel shows for its red rows.
    /// </summary>
    private string DescribeSingleItem(SchematicItem item)
    {
        if (item is not Components.NetLabelItem label)
            return $"Selected: {item.GetType().Name}";

        // The panel and the status bar must agree on what counts as an error,
        // so both read the same index. Build() is a linear scan of Items --
        // cheap at selection-change frequency.
        var groups = NetLabelIndex.Build(canvas.Schematic);
        string name = string.IsNullOrWhiteSpace(label.Label) ? "" : label.Label;
        NetLabelGroup? group = groups.FirstOrDefault(
            g => string.Equals(g.Name, name, StringComparison.Ordinal));

        string shown = label.Width == 1
            ? label.BitName(1)
            : $"{(name.Length == 0 ? "?" : name)}[{label.StartBit}..{label.StartBit + label.Width - 1}]";

        if (group?.Diagnostic is { } diag)
            return $"Selected: Net Label {shown} — ERROR — {diag}";

        int count = group?.Count ?? 1;
        return $"Selected: Net Label {shown} — {count} label{(count == 1 ? "" : "s")} named \"{name}\"";
    }

    /// <summary>
    /// Refresh the enabled state of the Cut / Copy / Paste menu items.
    /// Cut and Copy require a non-empty item selection; Paste requires
    /// something on the clipboard. Called from the SelectionChanged handler
    /// and from the Edit menu's DropDownOpening (the latter catches clipboard
    /// changes made by other applications).
    /// </summary>
    private void UpdateEditMenuItems()
    {
        bool hasItemSelection = canvas.Schematic.Selected.Any();
        cutItem.Enabled = hasItemSelection;
        copyItem.Enabled = hasItemSelection;
        pasteItem.Enabled = canvas.CanPaste;
    }

    private bool TryRestoreWindowBounds()
    {
        if (RecentFolders.WindowW is not int w || RecentFolders.WindowH is not int h)
            return false;
        if (RecentFolders.WindowX is not int x || RecentFolders.WindowY is not int y)
            return false;

        // Reject if the window wouldn't be visible on any current screen
        // (monitor was unplugged, resolution changed, etc.).
        var rect = new Rectangle(x, y, w, h);
        bool visible = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect));
        if (!visible) return false;

        StartPosition = FormStartPosition.Manual;
        Bounds = rect;

        if (RecentFolders.WindowState == "Maximized")
            WindowState = FormWindowState.Maximized;

        return true;
    }

    private void SaveWindowBounds()
    {
        // Save the *restored* (non-maximized) bounds, so next launch can restore
        // a sensible size even if currently maximized. RestoreBounds is exactly that.
        var r = (WindowState == FormWindowState.Normal) ? Bounds : RestoreBounds;
        RecentFolders.WindowX = r.X;
        RecentFolders.WindowY = r.Y;
        RecentFolders.WindowW = r.Width;
        RecentFolders.WindowH = r.Height;
        RecentFolders.WindowState =
            (WindowState == FormWindowState.Maximized) ? "Maximized" : "Normal";
        RecentFolders.Save();
    }

    private static readonly double[] SpeedPresets = { 0.1, 0.25, 0.5, 1.0, 2.0, 5.0, 10.0, 50.0, 100.0, 1000.0 };
    private int speedIndex = 3;  // index of 1.0 in SpeedPresets

    private void StepSpeed(int direction)
    {
        speedIndex = Math.Clamp(speedIndex + direction, 0, SpeedPresets.Length - 1);
        double factor = SpeedPresets[speedIndex];
        simController.SpeedFactor = factor;
        speedLabel.Text = FormatSpeed(factor);
    }

    private static string FormatSpeed(double factor) => factor switch
    {
        < 1.0 => $"{factor:0.##}×",
        _ => $"{factor:0}×"
    };

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !ConfirmDiscardChanges())
            e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SaveWindowBounds();
        Log.Shutdown();
        base.OnFormClosed(e);
    }

    private string? currentFilePath;
    private bool dirty;

    private const string FileFilter = "TTL Project (*.ttlproj)|*.ttlproj|All files (*.*)|*.*";
    private const string DefaultExt = "ttlproj";
    private const string HexFilter = "Intel HEX (*.hex;*.ihex)|*.hex;*.ihex|All files (*.*)|*.*";
    private const string JedecFilter = "JEDEC fuse map (*.jed;*.jedec)|*.jed;*.jedec|All files (*.*)|*.*";

    private void OnNew(object? sender, EventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;
        savedAtCommand = null;
        canvas.Schematic.Clear();
        canvas.UndoStack.Clear();
        currentFilePath = null;
        dirty = false;
        canvas.ResetView();
        canvas.Invalidate();
        SelectionChangedAfterModelReplace();
        UpdateTitle();
    }

    private void OnOpen(object? sender, EventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;
        using var dlg = new OpenFileDialog
        {
            Filter = FileFilter,
            DefaultExt = DefaultExt,
            InitialDirectory = !string.IsNullOrEmpty(RecentFolders.ProjectFolder)
                ? RecentFolders.ProjectFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        RecentFolders.RememberProjectFromFile(dlg.FileName);

        try
        {
            var loaded = Persistence.SchematicSerializer.Load(dlg.FileName);
            ReplaceSchematic(loaded);
            currentFilePath = dlg.FileName;
            dirty = false;
            UpdateTitle();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load file:\n{ex.Message}",
                "Open failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnSaveAs(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Filter = FileFilter,
            DefaultExt = DefaultExt,
            FileName = currentFilePath ?? "Untitled.ttlproj",
            InitialDirectory = currentFilePath != null
                ? Path.GetDirectoryName(currentFilePath)!
                : (!string.IsNullOrEmpty(RecentFolders.ProjectFolder)
                    ? RecentFolders.ProjectFolder
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        RecentFolders.RememberProjectFromFile(dlg.FileName);
        SaveTo(dlg.FileName);
    }

    private void OnExportEasyEDA(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "EasyEDA Pro (*.epro)|*.epro",
            DefaultExt = "epro",
            FileName = (currentFilePath != null
                ? Path.GetFileNameWithoutExtension(currentFilePath)
                : "Untitled") + ".epro",
            InitialDirectory = !string.IsNullOrEmpty(RecentFolders.ExportFolder)
                ? RecentFolders.ExportFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        RecentFolders.RememberExportFromFile(dlg.FileName);

        try
        {
            // Fresh log per export, matching OnBuild's pattern. Must run
            // before anything that logs.
            Log.Reset();

            var result = Persistence.EasyEDA.EasyEDAExporter.Export(
                canvas.Schematic, dlg.FileName);

            // Non-fatal diagnostics (wire-colour mismatch within a net,
            // router/exporter pin mismatch, ...) flow to the output panel
            // so the user can see and locate them, mirroring OnBuild's UX.
            outputPanel.Show("Export EasyEDA", result.Diagnostics);
            outputPanel.Visible = result.Diagnostics.Count > 0;
        }
        catch (Persistence.EasyEDA.ExportValueException ex)
        {
            MessageBox.Show(this,
            "Some parts have missing or invalid values:\n\n"
             + ex.Message,
            "Export EasyEDA", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (NotImplementedException ex)
        {
            MessageBox.Show(this,
            "Some parts in this schematic don't have an EasyEDA export mapping yet:\n\n"
             + ex.Message,
            "Export EasyEDA", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not export file:\n{ex.Message}",
            "Export EasyEDA", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnExportChipLabels(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "PDF label sheet (*.pdf)|*.pdf",
            DefaultExt = "pdf",
            FileName = (currentFilePath != null
                ? Path.GetFileNameWithoutExtension(currentFilePath)
                : "Untitled") + "-labels.pdf",
            InitialDirectory = !string.IsNullOrEmpty(RecentFolders.LabelFolder)
                ? RecentFolders.LabelFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        RecentFolders.RememberLabelFromFile(dlg.FileName);

        try
        {
            int count = Export.ChipLabelSheetExporter.Export(canvas.Schematic, dlg.FileName);
            if (count == 0)
                MessageBox.Show(this,
                    "The schematic has no labelable chips (DIP-packaged ICs), " +
                    "so no label sheet was written.",
                    "Export Chip Labels", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not export the label sheet:\n{ex.Message}",
                "Export Chip Labels", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- Intel HEX program import/export (EEPROM parts) -------------------

    private static bool IsEepromPart(string partNumber) =>
        partNumber is "28C256" or "28C128" or "28C64" or "28C16";

    /// <summary>The single selected EEPROM device, or null if the selection
    /// isn't exactly one EEPROM. Side-effect free, so it can drive both the
    /// actions and the menu-item enabled state.</summary>
    private Device? SingleSelectedEeprom()
    {
        var eeproms = canvas.Schematic.Selected
            .OfType<Unit>()
            .Select(u => u.Device)
            .Distinct()
            .Where(d => d.Definition is TTLSim.UI.Components.ChipPartDefinition cp
                        && IsEepromPart(cp.PartNumber))
            .ToList();
        return eeproms.Count == 1 ? eeproms[0] : null;
    }

    private static bool IsGalPart(string partNumber) =>
        partNumber is "GAL16V8" or "GAL20V8" or "GAL22V10";

    /// <summary>The single selected GAL device, or null if the selection isn't
    /// exactly one GAL. Side-effect free; drives both the action and the
    /// menu-item enabled state, mirroring SingleSelectedEeprom.</summary>
    private Device? SingleSelectedGal()
    {
        var gals = canvas.Schematic.Selected
            .OfType<Unit>()
            .Select(u => u.Device)
            .Distinct()
            .Where(d => d.Definition is TTLSim.UI.Components.ChipPartDefinition cp
                        && IsGalPart(cp.PartNumber))
            .ToList();
        return gals.Count == 1 ? gals[0] : null;
    }

    private void UpdateGalMenuItems()
    {
        importJedecItem.Enabled = SingleSelectedGal() is not null;
    }

    private void OnImportJedec(object? sender, EventArgs e)
    {
        Device? dev = SingleSelectedGal();
        if (dev is null) return;

        using var dlg = new OpenFileDialog
        {
            Filter = JedecFilter,
            DefaultExt = "jed",
            InitialDirectory = HexInitialDirectory()
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        RecentFolders.RememberHexFromFile(dlg.FileName);

        try
        {
            string text = System.IO.File.ReadAllText(dlg.FileName);
            TTLSim.Core.JedecData jedec = TTLSim.Core.JedecFuseMap.Parse(text);   // validate; throws on malformed

            // Reject a fuse map compiled for a different device. The fuse count
            // identifies the part (GAL16V8 = 2194, GAL20V8 = 2706, GAL22V10 =
            // 5828 or 5892 -- the latter carries the unused UES region); a
            // mismatch would put the config bits and the array at the wrong
            // addresses.
            string partNumber = dev.Definition is TTLSim.UI.Components.ChipPartDefinition cp
                ? cp.PartNumber : "";
            int[] expectedCounts = partNumber switch
            {
                "GAL16V8" => new[] { 2194 },
                "GAL20V8" => new[] { 2706 },
                "GAL22V10" => new[] { 5828, 5892 },
                _ => System.Array.Empty<int>()
            };
            if (expectedCounts.Length > 0 &&
                System.Array.IndexOf(expectedCounts, jedec.FuseCount) < 0)
            {
                string looksLike = jedec.FuseCount switch
                {
                    2194 => "GAL16V8",
                    2706 => "GAL20V8",
                    5828 or 5892 => "GAL22V10",
                    _ => $"{jedec.FuseCount}-fuse"
                };
                MessageBox.Show(this,
                    $"That looks like a {looksLike} fuse map, but {dev.Designator} is a {partNumber}.\n\n" +
                    $"Load a fuse map compiled for the {partNumber}.",
                    "Wrong device", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            dev.Program = text;

            // Populate the unit's user label from the design name in the
            // JEDEC header (generic PLD/GAL prefix stripped, underscores to
            // spaces: "PLD1_ALU" -> "1 ALU") so the canvas identifies the
            // programmed part at a glance. Import is authoritative: a header
            // that carries a name replaces any existing label; a header
            // without one leaves the label untouched.
            string? displayName = TTLSim.Chips.Pld.GalJedecHeader.TryParseDisplayName(text);
            if (displayName is not null)
            {
                foreach (var unit in dev.Units)
                {
                    if (unit is TTLSim.UI.Components.ChipUnit)
                    {
                        unit.Label = displayName;
                        break;
                    }
                }
            }

            dirty = true;
            UpdateTitle();
            propertyGrid.Refresh();
            UpdateGalMenuItems();
            canvas.Invalidate();   // pins follow the new fuse map -- redraw the symbol
            MessageBox.Show(this,
                $"Imported {System.IO.Path.GetFileName(dlg.FileName)} into {dev.Designator}.",
                "Import JEDEC", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not import JEDEC fuse map:\n{ex.Message}",
                "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Import is available when exactly one EEPROM is selected; Export
    /// additionally requires it to hold a program.</summary>
    private void UpdateHexMenuItems()
    {
        Device? dev = SingleSelectedEeprom();
        importHexItem.Enabled = dev is not null;
        exportHexItem.Enabled = dev is not null && !string.IsNullOrWhiteSpace(dev.Program);
    }

    private static string HexInitialDirectory() =>
        !string.IsNullOrEmpty(RecentFolders.HexFolder)
            ? RecentFolders.HexFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private void OnImportHex(object? sender, EventArgs e)
    {
        Device? dev = SingleSelectedEeprom();
        if (dev is null) return;

        using var dlg = new OpenFileDialog
        {
            Filter = HexFilter,
            DefaultExt = "hex",
            InitialDirectory = HexInitialDirectory()
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        RecentFolders.RememberHexFromFile(dlg.FileName);

        try
        {
            string text = System.IO.File.ReadAllText(dlg.FileName);
            TTLSim.Core.IntelHex.Parse(text);   // validate; throws on malformed
            dev.Program = text;
            dirty = true;
            UpdateTitle();
            propertyGrid.Refresh();
            UpdateHexMenuItems();               // Export becomes available now
            MessageBox.Show(this,
                $"Imported {System.IO.Path.GetFileName(dlg.FileName)} into {dev.Designator}.",
                "Import HEX", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not import Intel HEX:\n{ex.Message}",
                "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnExportHex(object? sender, EventArgs e)
    {
        Device? dev = SingleSelectedEeprom();
        if (dev is null || string.IsNullOrWhiteSpace(dev.Program)) return;

        using var dlg = new SaveFileDialog
        {
            Filter = HexFilter,
            DefaultExt = "hex",
            FileName = dev.Designator + ".hex",
            InitialDirectory = HexInitialDirectory()
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        RecentFolders.RememberHexFromFile(dlg.FileName);

        try
        {
            System.IO.File.WriteAllText(dlg.FileName, dev.Program);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not export Intel HEX:\n{ex.Message}",
                "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnSave(object? sender, EventArgs e)
    {
        if (currentFilePath == null) { OnSaveAs(sender, e); return; }
        SaveTo(currentFilePath);
    }



    private void SaveTo(string path)
    {
        try
        {
            Persistence.SchematicSerializer.Save(path,
                canvas.Schematic, canvas.Zoom, canvas.PanOffset);
            currentFilePath = path;
            savedAtCommand = canvas.UndoStack.UndoTop;
            dirty = false;
            UpdateTitle();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save file:\n{ex.Message}",
                "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnBuild(object? sender, EventArgs e)
    {
        // Fresh log for every build, so a build's output is never buried
        // under previous runs. Must run before anything that logs.
        Log.Reset();

        var result = simController.Build();
        outputPanel.Show(result);
        // Any diagnostic warrants the pane -- including Info-only builds
        // (e.g. TTL022 hidden-layer exclusions), which would otherwise be
        // buried in the log. Matches the export path's test.
        outputPanel.Visible = result.Diagnostics.Count > 0;
    }

    private void OnSimStateChanged(object? sender, EventArgs e)
    {
        bool built = simController.State == SimState.Built;
        bool running = simController.State == SimState.Running;
        bool paused = simController.State == SimState.Paused;

        buildButton.Enabled = !running && !paused;
        runButton.Enabled = built || paused;
        pauseButton.Enabled = running;
        stopButton.Enabled = running || paused;

        if (simController.State == SimState.Edit)
        {
            simTimeLabel.Text = "Sim time: --";
            canvas.SignalProvider = null;
            canvas.DisplayBindings = null;
            canvas.ButtonPressHandler = null;
            canvas.SwitchToggleHandler = null;
            canvas.SpdtToggleHandler = null;
            canvas.DipSwitchToggleHandler = null;
            canvas.ProbeProvider = null;
            canvas.PinSignalProvider = null;
        }
        else
        {
            canvas.SignalProvider = c => simController.GetSignal(c);
            canvas.DisplayBindings = simController.DisplayBindings;
            canvas.PinSignalProvider = (item, pin) => simController.GetPinSignal(item, pin);
            canvas.ButtonPressHandler = (btn, pressed) =>
            {
                if (simController.ButtonBindings.TryGetValue(btn, out var chip))
                    simController.SetButtonPressed(chip, pressed);
                canvas.Invalidate();
            };
            canvas.SwitchToggleHandler = (sw, closed) =>
            {
                if (simController.SwitchBindings.TryGetValue(sw, out var chip))
                    simController.SetSwitchClosed(chip, closed);
                canvas.Invalidate();
            };
            canvas.SpdtToggleHandler = (sw, throwB) =>
            {
                if (simController.SpdtBindings.TryGetValue(sw, out var chip))
                    simController.SetSpdtPosition(chip, throwB);
                canvas.Invalidate();
            };
            canvas.DipSwitchToggleHandler = (sw, position, closed) =>
            {
                if (simController.DipSwitchBindings.TryGetValue((sw, position), out var chip))
                    simController.SetSwitchClosed(chip, closed);
                canvas.Invalidate();
            };
            canvas.ProbeProvider = c => simController.GetProbeText(c);
        }

        canvas.Invalidate();
    }

    private void UpdateSimTimeLabel()
    {
        long ps = simController.CurrentTick;
        // Format as h:mm:ss.fff while running.
        double seconds = ps / 1.0e12;
        int h = (int)(seconds / 3600);
        int m = (int)((seconds % 3600) / 60);
        double s = seconds % 60;
        simTimeLabel.Text = $"Sim time: {h:D2}:{m:D2}:{s:00.000}";
    }

    private void OnLocateDiagnostic(object? sender, DiagnosticLocateEventArgs e)
    {
        // Collect every locator into one set of items + connections, then
        // select them all and centre the canvas on the bounding box of the
        // union. Single-target (ItemId) and multi-target (ItemIds /
        // ConnectionIds) fields are additive.
        var items = new List<SchematicItem>();
        var connections = new List<Connection>();

        if (e.Diagnostic.ItemId is { } singleItemId)
        {
            var it = canvas.FindItemById(singleItemId);
            if (it is not null) items.Add(it);
        }
        if (e.Diagnostic.ItemIds is { } itemIds)
        {
            foreach (var id in itemIds)
            {
                var it = canvas.FindItemById(id);
                if (it is not null) items.Add(it);
            }
        }
        if (e.Diagnostic.ConnectionIds is { } connIds)
        {
            foreach (var id in connIds)
            {
                var c = canvas.FindConnectionById(id);
                if (c is not null) connections.Add(c);
            }
        }

        // NetId locator: select every wire on that built net. Used by the
        // short-circuit errors (VCC/GND short, output-to-rail short) so a
        // double-click highlights the offending net's wires rather than a
        // single unit. Resolved against the build that produced these
        // diagnostics; if the schematic was edited since (build invalidated)
        // this no-ops.
        if (e.Diagnostic.NetId is { } netId
            && simController.LastBuild?.NetTable is { } netTable)
        {
            var net = netTable.Nets.FirstOrDefault(n => n.Id == netId);
            if (net is not null)
            {
                var pinSet = new HashSet<TTLSim.Core.PinRef>(net.Pins);
                foreach (var c in canvas.Schematic.ActiveConnections)
                {
                    if (c.A.Owner is not { } a || c.B.Owner is not { } b) continue;
                    if (pinSet.Contains(new TTLSim.Core.PinRef(a.Id, c.A.Number))
                        || pinSet.Contains(new TTLSim.Core.PinRef(b.Id, c.B.Number)))
                        connections.Add(c);
                }
            }
        }

        if (items.Count == 0 && connections.Count == 0 && e.Diagnostic.GridPoint is null) return;

        canvas.Schematic.ClearSelection();
        foreach (var it in items) it.Selected = true;
        foreach (var c in connections) c.Selected = true;

        // If the diagnostic carries an explicit grid point, that *is* the
        // location of interest — centre on it directly. The selected items
        // and connections still highlight via the selection pass above so
        // the user can see which wires are involved.
        if (e.Diagnostic.GridPoint is { } gp)
        {
            canvas.CenterOn(new Point(gp.X, gp.Y));
            canvas.Invalidate();
            return;
        }

        // Compute the union bounding box in grid units, considering items'
        // rotated Bounds and connections' two pin world positions plus their
        // owner items (so the centre lands sensibly even when a connection's
        // two pins are far apart).
        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;
        void Include(int x, int y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }
        foreach (var it in items)
        {
            var b = it.Bounds;
            Include(b.Left, b.Top);
            Include(b.Right, b.Bottom);
        }
        foreach (var c in connections)
        {
            var a = c.A.WorldPosition;
            var b = c.B.WorldPosition;
            Include(a.X, a.Y);
            Include(b.X, b.Y);
            if (c.A.Owner is { } oa) { var ob = oa.Bounds; Include(ob.Left, ob.Top); Include(ob.Right, ob.Bottom); }
            if (c.B.Owner is { } ob2) { var ob = ob2.Bounds; Include(ob.Left, ob.Top); Include(ob.Right, ob.Bottom); }
        }

        var centre = new Point((minX + maxX) / 2, (minY + maxY) / 2);
        canvas.CenterOn(centre);
        canvas.Invalidate();
    }

    private bool ConfirmDiscardChanges()
    {
        if (!dirty) return true;
        var result = MessageBox.Show(this,
            "There are unsaved changes. Save before continuing?",
            "Unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (result == DialogResult.Cancel) return false;
        if (result == DialogResult.Yes)
        {
            OnSave(this, EventArgs.Empty);
            return !dirty;
        }
        return true;
    }

    private void ReplaceSchematic(Persistence.SchematicSerializer.LoadResult loaded)
    {
        savedAtCommand = null;
        canvas.Schematic.CopyFrom(loaded.Schematic);
        canvas.UndoStack.Clear();
        if (loaded.Zoom is { } z && loaded.Pan is { } pan)
            canvas.SetView(z, pan);
        canvas.Invalidate();
        SelectionChangedAfterModelReplace();
    }

    private void SelectionChangedAfterModelReplace()
    {
        propertyGrid.SelectedObjects = Array.Empty<object>();
        outputPanel.Clear();
        outputPanel.Visible = false;
        simController.Invalidate();
        canvas.CurrentLayerId = 0;
        layersPanel.RefreshLayers();
        netLabelsPanel.RefreshLabels();
    }

    private void UpdateTitle()
    {
        string name = currentFilePath != null
            ? System.IO.Path.GetFileName(currentFilePath)
            : "Untitled";
        Text = $"TTL Schematic Editor — {name}{(dirty ? "*" : "")}";
    }
}

internal static class RecentFolders
{
    private static string StateFile =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TTLSim",
            "recent-folders.txt");

    public static string? ProjectFolder { get; set; }
    public static string? ExportFolder { get; set; }
    public static string? HexFolder { get; set; }   // shared by HEX import + export
    public static string? LabelFolder { get; set; } // chip label sheet export
    public static int? WindowX { get; set; }
    public static int? WindowY { get; set; }
    public static int? WindowW { get; set; }
    public static int? WindowH { get; set; }
    public static string? WindowState { get; set; }   // "Normal" or "Maximized"

    public static void Load()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            foreach (var line in File.ReadAllLines(StateFile))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1).Trim();
                if (key == "project" && Directory.Exists(val)) ProjectFolder = val;
                else if (key == "export" && Directory.Exists(val)) ExportFolder = val;
                else if (key == "hex" && Directory.Exists(val)) HexFolder = val;
                else if (key == "labels" && Directory.Exists(val)) LabelFolder = val;
                else if (key == "x" && int.TryParse(val, out var x)) WindowX = x;
                else if (key == "y" && int.TryParse(val, out var y)) WindowY = y;
                else if (key == "w" && int.TryParse(val, out var w)) WindowW = w;
                else if (key == "h" && int.TryParse(val, out var h)) WindowH = h;
                else if (key == "state") WindowState = val;
            }
        }
        catch { /* not fatal */ }
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(StateFile)!;
            Directory.CreateDirectory(dir);
            File.WriteAllLines(StateFile, new[]
            {
                $"project={ProjectFolder ?? ""}",
                $"export={ExportFolder ?? ""}",
                $"hex={HexFolder ?? ""}",
                $"labels={LabelFolder ?? ""}",
                $"x={WindowX?.ToString() ?? ""}",
                $"y={WindowY?.ToString() ?? ""}",
                $"w={WindowW?.ToString() ?? ""}",
                $"h={WindowH?.ToString() ?? ""}",
                $"state={WindowState ?? ""}"
            });
        }
        catch { /* not fatal */ }
    }

    public static void RememberProjectFromFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) { ProjectFolder = dir; Save(); }
    }

    public static void RememberExportFromFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) { ExportFolder = dir; Save(); }
    }

    public static void RememberHexFromFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) { HexFolder = dir; Save(); }
    }

    public static void RememberLabelFromFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) { LabelFolder = dir; Save(); }
    }
}