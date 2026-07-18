using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using TTLSim.Chips.Displays;
using TTLSim.Core;
using TTLSim.UI.Commands;
using TTLSim.UI.Components;
using TTLSim.UI.Logging;
using TTLSim.UI.Model;
using TTLSim.UI.Persistence;

namespace TTLSim.UI.View;


/// <summary>
/// The schematic editing surface. Coordinates internally are logical grid units;
/// rendering applies (zoom * grid pitch) to convert to screen pixels.
///
/// Interaction:
///   - Mouse wheel: zoom (centred on cursor)
///   - Middle drag: pan
///   - Left click: select / start moving an item
///   - Ctrl + left drag on a selected item: duplicate the selection and drag
///     the copy (one undo step)
///   - Drop target: accepts PartDefinitions and standalone-symbol factories from
///     the library panel.
///   - Space / Shift+Space: rotate the selected unit 90 degrees clockwise /
///     counter-clockwise (only when exactly one item is selected).
///   - Ctrl+C / Ctrl+X / Ctrl+V: copy / cut / paste the selection
/// </summary>
public sealed partial class SchematicCanvas : Control
{
    public Schematic Schematic { get; } = new();
    public UndoStack UndoStack { get; }

    /// <summary>
    /// Layer that newly dropped items are placed on. Set by the Layers panel
    /// when the user picks a current layer; reset to 0 (Default) when the
    /// schematic is replaced. An out-of-range value is harmless -- the active
    /// rule clamps it to Default.
    /// </summary>
    public int CurrentLayerId { get; set; }

    /// <summary>
    /// When non-null, the canvas is in "sim mode": wires colour by the
    /// signal returned from this callback, and editor wire colours are
    /// suppressed. Null in Edit state.
    /// </summary>
    public Func<Connection, Signal?>? SignalProvider { get; set; }

    /// <summary>
    /// In sim mode: called when the user presses or releases a button symbol.
    /// The bool is true for press, false for release. Null outside sim mode.
    /// </summary>
    public Action<ButtonUnit, bool>? ButtonPressHandler { get; set; }

    /// <summary>Sim-mode: invoked with the new IsClosed state when a switch is clicked.</summary>
    public Action<SwitchUnit, bool>? SwitchToggleHandler { get; set; }

    /// <summary>Sim-mode: invoked with the new ThrowB state when an SPDT switch is clicked.</summary>
    public Action<SpdtSwitchUnit, bool>? SpdtToggleHandler { get; set; }

    /// <summary>Sim-mode: invoked with the 0-based position index and its new
    /// closed state when one position of a DIP switch is clicked.</summary>
    public Action<DipSwitchUnit, int, bool>? DipSwitchToggleHandler { get; set; }

    private ButtonUnit? heldButton;

    private readonly ToolTip probeTooltip = new() { ShowAlways = true, InitialDelay = 200, ReshowDelay = 100 };
    private Connection? hoveredConnection;

    /// <summary>
    /// In sim mode: supplies a probe description for a connection's net
    /// (name, current value, last-change tick). Null outside sim mode.
    /// </summary>
    public Func<Connection, string?>? ProbeProvider { get; set; }

    /// <summary>
    /// When non-null, the canvas consults this map to render 7-segment
    /// display segments as lit/unlit. Keyed by the display unit on the canvas.
    /// </summary>
    public IReadOnlyDictionary<SchematicItem, SevenSegCa>? DisplayBindings { get; set; }

    /// <summary>
    /// When non-null, the canvas uses this to resolve the signal on a given
    /// item pin. Used by LEDs (lit/unlit) and header output pins (per-pin
    /// state dot). Null in Edit state.
    /// </summary>
    public Func<SchematicItem, int, Signal?>? PinSignalProvider { get; set; }

    /// <summary>
    /// The router that decides how connections are drawn. Swap implementations
    /// freely; the model is unaffected. Setting a new router invalidates the
    /// route cache and triggers a repaint.
    /// </summary>
    public Routing.IConnectionRouter Router
    {
        get => router;
        set
        {
            router = value ?? throw new ArgumentNullException(nameof(value));
            routeCache = null;
            coincidentCornersCache = null;
            Invalidate();
        }
    }
    private Routing.IConnectionRouter router = new Routing.WireRouter();

    // Cache of router output (polylines + junctions). Built lazily by the
    // Routes accessor; nulled by anything that may have invalidated pin
    // positions (committed model edits via UndoStack.Changed, and live
    // mid-drag pin moves in OnMouseMove).
    private Routing.RouteResult? routeCache;

    // Cached coincident-corner detection over the current routeCache. Same
    // lifetime as routeCache — both are nulled together.
    private IReadOnlyList<Routing.CoincidentCorner>? coincidentCornersCache;

    private Routing.RouteResult Routes
    {
        get
        {
            if (routeCache is not null) return routeCache;

            // Routing can be slow on large schematics (Dijkstra over a grid
            // the size of the canvas, per connection group). Show the wait
            // cursor so the user understands the brief pause. We restore the
            // previous cursor in finally rather than hard-coding Cursors.Default
            // so we don't trample anything the canvas set on its own (e.g. the
            // panning cursor) -- although in practice routing only runs when
            // the cache was just invalidated by a model edit, which won't
            // collide with the mid-pan state.
            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                routeCache = router.RouteAll(Schematic);
            }
            finally
            {
                Cursor = previous;
            }
            return routeCache;
        }
    }

    /// <summary>
    /// Cells where one wire's vertex (endpoint or bend) coincides with a
    /// different-net wire. These render as red dots and surface as EDA003
    /// errors on EasyEDA export. Computed lazily over <see cref="Routes"/>
    /// and cached until the model changes.
    /// </summary>
    private IReadOnlyList<Routing.CoincidentCorner> CoincidentCorners
    {
        get
        {
            if (coincidentCornersCache is not null) return coincidentCornersCache;

            // Net id per connection via union-find over shared pins. Local
            // and short — the writer has its own copy for EDA003 emission
            // because it also needs the per-net connection groups for net
            // descriptions; the canvas just needs the ids.
            //
            // Built over ACTIVE connections only, to match the router: a hidden
            // wire produces no polyline and is electrically absent, so it must
            // not bridge two visible nets into one id (which would hide a real
            // cross-net corner between them). Net-label ties are included as
            // pseudo-connections (indices past the real ones) so two clusters
            // joined only by same-named labels share one net id and their
            // coincidences never surface as false EDA003 errors.
            var activeConns = Schematic.ActiveConnections.ToList();
            var ties = Schematic.NetLabelTiePairs().ToList();
            var parent = new int[activeConns.Count + ties.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            var pinToConn = new Dictionary<Pin, int>();
            void Touch(Pin pin, int index)
            {
                if (pinToConn.TryGetValue(pin, out int j))
                { int ra = Find(index), rb = Find(j); if (ra != rb) parent[ra] = rb; }
                else pinToConn[pin] = index;
            }
            for (int i = 0; i < activeConns.Count; i++)
            {
                var c = activeConns[i];
                Touch(c.A, i);
                Touch(c.B, i);
            }
            for (int t = 0; t < ties.Count; t++)
            {
                Touch(ties[t].A, activeConns.Count + t);
                Touch(ties[t].B, activeConns.Count + t);
            }
            var netIdOf = new Dictionary<Connection, int>(activeConns.Count);
            for (int i = 0; i < activeConns.Count; i++)
                netIdOf[activeConns[i]] = Find(i);

            coincidentCornersCache = Routing.CoincidentCornerDetector.Detect(
                activeConns, Routes, c => netIdOf[c]);

            // Verbose-only diagnostic: turn each red dot into a log line
            // naming the colliding pins and nets. LogDebug is filtered out
            // unless verbose logging is on (Help -> Verbose Logging), and the
            // Verbose guard also keeps us off the logger when it isn't up.
            if (coincidentCornersCache.Count > 0 && Log.Verbose)
            {
                var log = Log.For<SchematicCanvas>();
                log.LogDebug("Routing produced {Count} coincident-corner error(s):",
                    coincidentCornersCache.Count);
                foreach (var line in Routing.CoincidentCornerDetector.Describe(
                             coincidentCornersCache, c => netIdOf[c]))
                    log.LogDebug("  {Detail}", line);
            }

            return coincidentCornersCache;
        }
    }


    public int GridPitch { get; set; } = 5;          // logical units per grid cell
    public float Zoom { get; private set; } = 4f;    // start zoomed in so 5px feels usable
    public PointF PanOffset { get; private set; } = new(40, 40);

    private bool panning;
    private Point panAnchor;
    private PointF panStart;

    private bool dragging;
    private Point dragGridAnchor;
    private List<(SchematicItem Item, Point Start)> dragItems = new();

    // Set when the in-progress drag began as a Ctrl-drag duplicate. In that
    // case OnMouseDown has already opened a composite ("Duplicate") and
    // pasted the clones into it; OnMouseUp must close that same composite
    // after recording the moves, instead of running the normal move-commit
    // path (which would open its own separate composite).
    private bool copyDragInProgress;

    // Marquee selection state. Active when left-mouse-down hit empty grid.
    private bool marqueeing;
    private Point marqueeStart;   // grid units
    private Point marqueeEnd;     // grid units
    private bool marqueeCtrl;     // Ctrl held when marquee started -- additive mode

    // Connection placement state. Null when not placing.
    private Pin? wireStartPin;
    // Live cursor grid point while placing, for the rubber-band preview.
    private Point wirePreviewEnd;

    // Header-link placement state. Null when not placing. The first click
    // picks the start header; the second click on a different, equal-pin-count
    // header creates the link.
    private HeaderOutputUnit? headerLinkStart;
    // Live cursor grid point while placing, for the rubber-band preview.
    private Point headerLinkPreviewEnd;
    private bool awaitingHeaderLinkStart;

    // Last known cursor position in grid units, updated on every mouse move.
    // Used by keyboard/menu paste to decide between "paste under the cursor"
    // (cursor over the canvas) and "paste at a cascade offset" (cursor
    // elsewhere, e.g. the menu bar).
    private Point lastMouseGrid;
    private bool isMouseOverCanvas;

    // Arrow-key nudge: which arrow (if any) is currently held down. Used to
    // suppress auto-repeat so one press = one one-cell nudge, even on a long
    // hold. Reset on KeyUp.
    private Keys nudgeKeyHeld = Keys.None;

    // Cascade counter for keyboard/menu paste. Each such paste lands one step
    // further up-and-right than the last, so repeated Ctrl+V doesn't stack
    // everything on one spot.
    //
    // Reset to 0 when the working context changes:
    //   - Copy / Cut          -- a new payload; the old cascade is meaningless
    //   - a mouse-positioned paste (PasteAt) -- defines a fresh anchor
    //
    // Note: a plain selection change (clicking elsewhere between pastes) does
    // NOT reset the counter. The accepted consequence is that
    // paste-paste-click-elsewhere-paste lands the last paste stepped off the
    // original anchor rather than near the new click. Resetting on Copy/Cut
    // and on mouse-positioned paste was chosen as the simpler rule.
    private int pasteCascadeCount;

    // Up-and-right offset, in grid units, between successive cascade pastes.
    private static readonly Size CascadeStep = new(3, -3);

    public event EventHandler? SelectionChanged;
    public event EventHandler? ViewChanged;

    public bool IsPlacingWire => wireStartPin != null || awaitingWireStart;

    public bool IsPlacingHeaderLink => headerLinkStart != null || awaitingHeaderLinkStart;

    private bool awaitingWireStart;

    /// <summary>
    /// Raise <see cref="SelectionChanged"/>. A single funnel for the event so
    /// every selection-changing path goes through one place; kept as a method
    /// (rather than scattering <c>SelectionChanged?.Invoke</c> at each call
    /// site) purely for that tidiness.
    /// </summary>
    private void OnSelectionChanged()
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Enter wire-placement mode. First click picks the start pin, second click the end.</summary>
    public void BeginWirePlacement()
    {
        awaitingWireStart = true;
        wireStartPin = null;
        Cursor = Cursors.Cross;
        Invalidate();
    }

    /// <summary>Cancel wire placement, discarding any in-progress wire.</summary>
    public void CancelWirePlacement()
    {
        awaitingWireStart = false;
        wireStartPin = null;
        Cursor = Cursors.Default;
        Invalidate();
    }

    /// <summary>
    /// Enter header-link placement mode. First click picks the start header,
    /// second click the end header. The two must have the same pin count.
    /// </summary>
    public void BeginHeaderLinkPlacement()
    {
        awaitingHeaderLinkStart = true;
        headerLinkStart = null;
        Cursor = Cursors.Cross;
        Invalidate();
    }

    /// <summary>Cancel header-link placement, discarding any in-progress link.</summary>
    public void CancelHeaderLinkPlacement()
    {
        awaitingHeaderLinkStart = false;
        headerLinkStart = null;
        Cursor = Cursors.Default;
        Invalidate();
    }

    public SchematicCanvas()
    {
        UndoStack = new UndoStack(Schematic);

        UndoStack.Changed += (_, _) =>
        {
            routeCache = null;
            coincidentCornersCache = null;
            Invalidate();
            OnSelectionChanged();
        };

        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.White;
        AllowDrop = true;

        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    /// <summary>
    /// Tolerance (in grid units) for connection hit-testing. Roughly half a
    /// grid pitch in screen pixels stays comfortable across zoom levels.
    /// </summary>
    private float ConnectionHitTolerance => 0.5f / Zoom * 1.5f;
}