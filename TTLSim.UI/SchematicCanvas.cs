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
public sealed class SchematicCanvas : Control
{
    public Schematic Schematic { get; } = new();
    public UndoStack UndoStack { get; }

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
            var parent = new int[Schematic.Connections.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            var pinToConn = new Dictionary<Pin, int>();
            for (int i = 0; i < Schematic.Connections.Count; i++)
            {
                var c = Schematic.Connections[i];
                if (pinToConn.TryGetValue(c.A, out int j1))
                { int ra = Find(i), rb = Find(j1); if (ra != rb) parent[ra] = rb; }
                else pinToConn[c.A] = i;
                if (pinToConn.TryGetValue(c.B, out int j2))
                { int ra = Find(i), rb = Find(j2); if (ra != rb) parent[ra] = rb; }
                else pinToConn[c.B] = i;
            }
            var netIdOf = new Dictionary<Connection, int>(Schematic.Connections.Count);
            for (int i = 0; i < Schematic.Connections.Count; i++)
                netIdOf[Schematic.Connections[i]] = Find(i);

            coincidentCornersCache = Routing.CoincidentCornerDetector.Detect(
                Schematic.Connections, Routes, c => netIdOf[c]);

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

    // ---------------------------------------------------------------- coordinate math

    /// <summary>Convert a screen-pixel point to a grid-unit point.</summary>
    public Point ScreenToGrid(Point screen)
    {
        float gx = (screen.X - PanOffset.X) / (Zoom * GridPitch);
        float gy = (screen.Y - PanOffset.Y) / (Zoom * GridPitch);
        return new Point((int)Math.Round(gx), (int)Math.Round(gy));
    }

    /// <summary>Set zoom and pan together, e.g. when restoring view state from a file.</summary>
    public void SetView(float zoom, PointF pan)
    {
        Zoom = Math.Clamp(zoom, 0.05f, 100f);
        PanOffset = pan;
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }


    /// <summary>Convert a grid-unit point to a screen-pixel point.</summary>
    public PointF GridToScreen(Point grid) =>
        new(grid.X * Zoom * GridPitch + PanOffset.X,
            grid.Y * Zoom * GridPitch + PanOffset.Y);

    // ---------------------------------------------------------------- painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Sim mode: subtle warm-cream background tint to distinguish from edit mode.
        if (SignalProvider is not null)
        {
            using var bg = new SolidBrush(Color.FromArgb(0xFA, 0xFA, 0xF5));
            g.FillRectangle(bg, 0, 0, ClientSize.Width, ClientSize.Height);
        }

        DrawGrid(g);

        g.TranslateTransform(PanOffset.X, PanOffset.Y);
        g.ScaleTransform(Zoom, Zoom);

        var ctx = new RenderContext
        {
            GridPitch = GridPitch,
            Zoom = Zoom,
            SegmentProvider = DisplayBindings is null ? null : (item =>
            {
                if (DisplayBindings.TryGetValue(item, out var disp))
                    return (disp.Segments.ToArray(), disp.Dp);
                return null;
            }),
            LedStateProvider = PinSignalProvider is null ? null : (item =>
            {
                // Lit when anode (pin 1) is driven high and cathode (pin 2) low.
                return PinSignalProvider(item, 1) == Signal.High
                    && PinSignalProvider(item, 2) == Signal.Low;
            }),
            SignalStateProvider = PinSignalProvider
        };

        // Cosmetic background items (rectangles, text labels) render behind
        // everything else: before wires, links, junctions, and components.
        // They are skipped in the main item loop below so each paints exactly
        // once. They sit on top of the grid (drawn above) but behind all
        // schematic content.
        foreach (var item in Schematic.ActiveItems)
            if (item is IBackgroundItem)
                item.Draw(g, ctx);

#if DEBUG
        // Debug: visualise routing bounds in pale pink.
        using (var routingBrush = new SolidBrush(Color.FromArgb(60, 255, 180, 200)))
        {
            int p = GridPitch;
            foreach (var item in Schematic.ActiveItems)
            {
                var rb = item.RoutingBounds;
                g.FillRectangle(routingBrush,
                    rb.X * p, rb.Y * p, rb.Width * p, rb.Height * p);
            }
        }

        foreach (var connection in Schematic.ActiveConnections)
            DrawConnector(g, connection);
#endif

        foreach (var connection in Schematic.ActiveConnections)
            DrawWire(g, connection);

        foreach (var link in Schematic.ActiveLinks)
            DrawHeaderLink(g, link);

        DrawJunctions(g);

        DrawCoincidentCornerWarnings(g);

        foreach (var item in Schematic.ActiveItems)
        {
            if (item is IBackgroundItem) continue;   // drawn in the background pass above
            item.Draw(g, ctx);
        }

        if (wireStartPin != null)
        {
            int p = GridPitch;
            var from = wireStartPin.WorldPosition;
            using var dashPen = new Pen(Color.FromArgb(180, 80, 80, 200), 1.2f)
            { DashStyle = DashStyle.Dash };
            g.DrawLine(dashPen,
                from.X * p, from.Y * p,
                wirePreviewEnd.X * p, wirePreviewEnd.Y * p);
        }

        if (headerLinkStart != null)
        {
            int p = GridPitch;
            // Anchor the preview at the start header's body centre.
            var bnd = headerLinkStart.Bounds;
            float ax = (bnd.X + bnd.Width / 2f) * p;
            float ay = (bnd.Y + bnd.Height / 2f) * p;
            using var dashPen = new Pen(Color.FromArgb(180, 120, 90, 170), 1.2f)
            { DashStyle = DashStyle.Dash };
            g.DrawLine(dashPen, ax, ay,
                headerLinkPreviewEnd.X * p, headerLinkPreviewEnd.Y * p);
        }

        if (marqueeing)
        {
            int p = GridPitch;
            int minX = Math.Min(marqueeStart.X, marqueeEnd.X) * p;
            int maxX = Math.Max(marqueeStart.X, marqueeEnd.X) * p;
            int minY = Math.Min(marqueeStart.Y, marqueeEnd.Y) * p;
            int maxY = Math.Max(marqueeStart.Y, marqueeEnd.Y) * p;
            int w = maxX - minX;
            int h = maxY - minY;

            bool overlapMode = marqueeEnd.X < marqueeStart.X;
            // Containment (L->R): solid stroke, faint blue fill.
            // Overlap     (R->L): dashed stroke, faint green fill.
            Color stroke = overlapMode
                ? Color.FromArgb(220, 60, 140, 60)
                : Color.FromArgb(220, 60, 100, 200);
            Color fill = overlapMode
                ? Color.FromArgb(40, 60, 140, 60)
                : Color.FromArgb(40, 60, 100, 200);

            using var fillBrush = new SolidBrush(fill);
            using var pen = new Pen(stroke, 1f);
            if (overlapMode) pen.DashStyle = DashStyle.Dash;
            if (w > 0 || h > 0)
            {
                g.FillRectangle(fillBrush, minX, minY, w, h);
                g.DrawRectangle(pen, minX, minY, w, h);
            }
        }
    }

    /// <summary>
    /// Debug overlay: dotted light-blue straight line directly between the
    /// two pins of a connection. Draws regardless of the router; useful for
    /// confirming what the model thinks is connected vs. what the router
    /// produced.
    /// </summary>
#if DEBUG
    private void DrawConnector(Graphics g, Connection connection)
    {
        int p = GridPitch;
        var a = connection.A.WorldPosition;
        var b = connection.B.WorldPosition;

        Color color = connection.Selected
            ? Color.FromArgb(220, 40, 90, 200)
            : Color.FromArgb(200, 130, 170, 230);

        using var pen = new Pen(color, 1.0f) { DashStyle = DashStyle.Dot };
        g.DrawLine(pen, a.X * p, a.Y * p, b.X * p, b.Y * p);
    }
#endif
    /// <summary>
    /// Draw the routed polyline for a single connection in the wire colour
    /// chosen on the connection itself. Selected wires render in a fixed
    /// blue regardless of their assigned colour so the selection stays
    /// visible against any palette choice.
    /// </summary>
    private void DrawWire(Graphics g, Connection connection)
    {
        if (!Routes.Polylines.TryGetValue(connection, out var pts) || pts.Count < 2)
            return;

        int p = GridPitch;
        var screen = new PointF[pts.Count];
        for (int i = 0; i < pts.Count; i++)
            screen[i] = new PointF(pts[i].X * p, pts[i].Y * p);

        Color color;
        float thickness = 1.4f;

        if (SignalProvider is { } provider)
        {
            Signal? state = provider(connection);
            color = state.HasValue ? SignalColors.For(state.Value) : SignalColors.Unknown;
        }
        else
        {
            color = connection.Color.ToColor();
        }

        if (connection.Selected)
            color = RenderContext.DefaultSelected;

        using var pen = new Pen(color, thickness);
        g.DrawLines(pen, screen);
    }

    /// <summary>
    /// Yield the strand endpoints (in grid units) for a header link: pin i of
    /// A to pin i of B, for every pin both headers share. Strands always
    /// terminate on the true pin endpoints, so the drawing is honestly 1-to-1
    /// (it may cross when the headers face each other). The link's Reversed
    /// flag does not affect these endpoints.
    /// </summary>
    private static IEnumerable<(Point A, Point B)> HeaderLinkStrands(HeaderLink link)
    {
        int n = link.PinCount;
        for (int i = 1; i <= n; i++)
        {
            var pa = link.A.Pins.FirstOrDefault(p => p.Number == i);
            var pb = link.B.Pins.FirstOrDefault(p => p.Number == i);
            if (pa is null || pb is null) continue;
            yield return (pa.WorldPosition, pb.WorldPosition);
        }
    }

    /// <summary>
    /// Draw a header link as a bundle of straight strands, one per pin pair.
    /// Not routed -- a link is a fixed bundle, so it bypasses the wire router.
    /// A selected link renders in the selection colour.
    /// </summary>
    private void DrawHeaderLink(Graphics g, HeaderLink link)
    {
        int p = GridPitch;
        Color color = link.Selected
            ? RenderContext.DefaultSelected
            : Color.FromArgb(210, 120, 90, 170);   // muted violet ribbon

        using var pen = new Pen(color, 1.4f);
        foreach (var (a, b) in HeaderLinkStrands(link))
            g.DrawLine(pen, a.X * p, a.Y * p, b.X * p, b.Y * p);
    }

    /// <summary>
    /// Render junction blobs (T-junctions and crossings of same-net wires).
    /// Empty for 2-pin connections; will populate when multi-pin nets are
    /// introduced.
    /// </summary>
    private void DrawJunctions(Graphics g)
    {
        if (Routes.Junctions.Count == 0) return;

        int p = GridPitch;
        using var brush = new SolidBrush(Color.Black);
        float r = 2.0f;
        foreach (var j in Routes.Junctions)
            g.FillEllipse(brush, j.X * p - r, j.Y * p - r, r * 2, r * 2);
    }

    /// <summary>
    /// Render red warning dots at cells where a wire's vertex coincides
    /// with a different-net wire. These are export-blocking — EasyEDA
    /// would infer a junction at the cell and merge the two nets.
    /// </summary>
    private void DrawCoincidentCornerWarnings(Graphics g)
    {
        var corners = CoincidentCorners;
        if (corners.Count == 0) return;

        int p = GridPitch;
        using var fill = new SolidBrush(Color.FromArgb(220, 220, 30, 30));
        using var ring = new Pen(Color.FromArgb(255, 120, 0, 0), 1.0f);
        float r = 3.5f;
        foreach (var corner in corners)
        {
            float cx = corner.Cell.X * p;
            float cy = corner.Cell.Y * p;
            g.FillEllipse(fill, cx - r, cy - r, r * 2, r * 2);
            g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
        }
    }

    /// <summary>
    /// Hit-test against the router's polylines. Iterates from topmost
    /// connection down so overlapping wires resolve consistently.
    /// </summary>
    private Connection? HitTestConnection(Point gridPoint)
    {
        float tol = ConnectionHitTolerance;
        var polylines = Routes.Polylines;

        for (int i = Schematic.Connections.Count - 1; i >= 0; i--)
        {
            var c = Schematic.Connections[i];
            if (!Schematic.IsConnectionActive(c)) continue;
            if (!polylines.TryGetValue(c, out var pts) || pts.Count < 2) continue;

            for (int j = 0; j < pts.Count - 1; j++)
            {
                if (DistancePointToSegment(gridPoint, pts[j], pts[j + 1]) <= tol)
                    return c;
            }
        }
        return null;
    }

    private static float DistancePointToSegment(Point p, Point a, Point b)
    {
        float vx = b.X - a.X;
        float vy = b.Y - a.Y;
        float wx = p.X - a.X;
        float wy = p.Y - a.Y;

        float lenSq = vx * vx + vy * vy;
        if (lenSq <= 0f)
            return MathF.Sqrt(wx * wx + wy * wy);

        float t = Math.Clamp((wx * vx + wy * vy) / lenSq, 0f, 1f);
        float dx = wx - t * vx;
        float dy = wy - t * vy;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Hit-test against header-link strands, topmost link first. Uses the same
    /// tolerance as wire hit-testing.
    /// </summary>
    private HeaderLink? HitTestHeaderLink(Point gridPoint)
    {
        float tol = ConnectionHitTolerance;
        for (int i = Schematic.Links.Count - 1; i >= 0; i--)
        {
            var link = Schematic.Links[i];
            if (!Schematic.IsLinkActive(link)) continue;
            foreach (var (a, b) in HeaderLinkStrands(link))
                if (DistancePointToSegment(gridPoint, a, b) <= tol)
                    return link;
        }
        return null;
    }

    private void DrawGrid(Graphics g)
    {
        float pitchScreen = Zoom * GridPitch;
        if (pitchScreen < 3f) return;

        float startX = PanOffset.X % pitchScreen;
        float startY = PanOffset.Y % pitchScreen;

        using var minorPen = new Pen(Color.FromArgb(40, Color.Gray));
        using var majorPen = new Pen(Color.FromArgb(90, Color.Gray));

        float majorPitch = pitchScreen * 10f;
        float majorStartX = PanOffset.X % majorPitch;
        float majorStartY = PanOffset.Y % majorPitch;

        using var dotBrush = new SolidBrush(Color.FromArgb(70, Color.Gray));
        if (pitchScreen >= 6f)
        {
            if (pitchScreen >= 12f)
            {
                for (float x = startX; x < Width; x += pitchScreen)
                    for (float y = startY; y < Height; y += pitchScreen)
                        g.FillEllipse(dotBrush, x - 1.5f, y - 1.5f, 3, 3);
            }
            else
            {
                for (float x = startX; x < Width; x += pitchScreen)
                    for (float y = startY; y < Height; y += pitchScreen)
                        g.FillRectangle(dotBrush, x - 0.5f, y - 0.5f, 1, 1);
            }
        }

        for (float x = majorStartX; x < Width; x += majorPitch)
            g.DrawLine(majorPen, x, 0, x, Height);
        for (float y = majorStartY; y < Height; y += majorPitch)
            g.DrawLine(majorPen, 0, y, Width, y);
    }

    // ---------------------------------------------------------------- zoom

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        float factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;
        ZoomAt(e.Location, factor);
    }

    public void ZoomAt(Point screenPoint, float factor)
    {
        float newZoom = Math.Clamp(Zoom * factor, 0.05f, 100f);
        if (Math.Abs(newZoom - Zoom) < 0.0001f) return;

        float gx = (screenPoint.X - PanOffset.X) / (Zoom * GridPitch);
        float gy = (screenPoint.Y - PanOffset.Y) / (Zoom * GridPitch);
        Zoom = newZoom;
        PanOffset = new PointF(
            screenPoint.X - gx * Zoom * GridPitch,
            screenPoint.Y - gy * Zoom * GridPitch);

        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetView()
    {
        Zoom = 4f;
        PanOffset = new PointF(40, 40);
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Pan (no zoom change) so the given grid-space point sits at the centre
    /// of the visible canvas. Used by the output panel's click-to-locate.
    /// </summary>
    public void CenterOn(Point gridPoint)
    {
        PanOffset = new PointF(
            ClientSize.Width / 2f - gridPoint.X * Zoom * GridPitch,
            ClientSize.Height / 2f - gridPoint.Y * Zoom * GridPitch);
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Look up an item by its Id. Returns null if no item matches. Power
    /// symbols and clock sources live in Items; Units also live there.
    /// </summary>
    public SchematicItem? FindItemById(string id)
    {
        foreach (var item in Schematic.Items)
            if (item.Id == id) return item;
        return null;
    }

    /// <summary>
    /// Look up a connection by its Id. Returns null if no connection matches.
    /// Used by diagnostic-locate to centre/select wires named in build or
    /// export diagnostics.
    /// </summary>
    public Connection? FindConnectionById(string id)
    {
        foreach (var c in Schematic.Connections)
            if (c.Id == id) return c;
        return null;
    }

    /// <summary>
    /// True for combinational gate units (AND/OR/NAND/NOR/XOR/NOT) whose
    /// every input is connected but whose output drives nothing -- typically
    /// the spare gates of a shared package tied off to GND/VCC. Used by
    /// FitView to ignore tie-off clutter when framing the schematic.
    /// </summary>
    private bool IsUnusedGate(SchematicItem item)
    {
        if (item is not (AndGateUnit or OrGateUnit or XorGateUnit
                         or NandGateUnit or NorGateUnit or NotGateUnit))
            return false;

        // Unrotated LocalDirection: Left == input, Right == output (every
        // gate unit goes through Unit.BuildLeftInputsRightOutput).
        bool anyInput = false, anyOutput = false;
        foreach (var pin in item.Pins)
        {
            if (pin.LocalDirection == PinDirection.Left) anyInput = true;
            if (pin.LocalDirection == PinDirection.Right) anyOutput = true;
        }
        if (!anyInput || !anyOutput) return false;

        // One pass over the connection list, classifying each endpoint that
        // sits on this item. Output with any connection => gate is used.
        // After the pass, every input must have been seen at least once.
        var inputsSeen = new HashSet<Pin>();
        foreach (var c in Schematic.ConnectionsOn(item))
        {
            foreach (var ep in new[] { c.A, c.B })
            {
                if (ep.Owner != item) continue;
                if (ep.LocalDirection == PinDirection.Right) return false;
                if (ep.LocalDirection == PinDirection.Left) inputsSeen.Add(ep);
            }
        }

        foreach (var pin in item.Pins)
        {
            if (pin.LocalDirection == PinDirection.Left && !inputsSeen.Contains(pin))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Zoom and pan so that the entire schematic fits in the visible
    /// canvas area, with a small margin. Falls back to ResetView when
    /// the schematic is empty.
    /// </summary>
    public void FitView()
    {
        if (!Schematic.ActiveItems.Any())
        {
            ResetView();
            return;
        }

        // Bounding box across every item AND every routed wire cell, in
        // grid units. Item.Bounds only covers the symbol body/pins -- wire
        // polylines can swing outside that on detours, so we also walk the
        // router's polylines so the fit shows the entire visible drawing.
        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;

        // Pre-compute the set of unused gates so we can skip them and their
        // tie-off connections when framing the view.
        var unusedGates = new HashSet<SchematicItem>();
        foreach (var item in Schematic.ActiveItems)
            if (IsUnusedGate(item)) unusedGates.Add(item);

        // Power symbols (GND/VCC) whose every connection terminates on an
        // unused gate are themselves just tie-off clutter -- exclude those
        // too. A power symbol with at least one connection to a USED item,
        // or with no connections at all, stays in the frame.
        var hiddenPower = new HashSet<SchematicItem>();
        foreach (var item in Schematic.ActiveItems)
        {
            if (item is not (GndSymbol or VccSymbol)) continue;

            bool hasAny = false;
            bool allToUnusedGates = true;
            foreach (var c in Schematic.ConnectionsOn(item))
            {
                hasAny = true;
                var other = c.A.Owner == item ? c.B.Owner : c.A.Owner;
                if (other is null || !unusedGates.Contains(other))
                {
                    allToUnusedGates = false;
                    break;
                }
            }
            if (hasAny && allToUnusedGates) hiddenPower.Add(item);
        }

        foreach (var item in Schematic.ActiveItems)
        {
            if (unusedGates.Contains(item)) continue;
            if (hiddenPower.Contains(item)) continue;
            var b = item is IBackgroundItem ? item.Bounds : item.RoutingBounds;
            if (b.Left < minX) minX = b.Left;
            if (b.Top < minY) minY = b.Top;
            if (b.Right > maxX) maxX = b.Right;
            if (b.Bottom > maxY) maxY = b.Bottom;
        }

        foreach (var kvp in Routes.Polylines)
        {
            var c = kvp.Key;
            // Skip wires on an invisible layer -- a hidden wire must not drag
            // the viewport. (Routes still routes every connection until the
            // router increment, so the filter lives here for now.)
            if (!Schematic.IsConnectionActive(c)) continue;
            // Skip connections whose only purpose is to tie off an unused gate.
            if ((c.A.Owner is { } oa && (unusedGates.Contains(oa) || hiddenPower.Contains(oa))) ||
                (c.B.Owner is { } ob && (unusedGates.Contains(ob) || hiddenPower.Contains(ob))))
                continue;

            foreach (var pt in kvp.Value)
            {
                if (pt.X < minX) minX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y > maxY) maxY = pt.Y;
            }
        }

        int boxW = maxX - minX;
        int boxH = maxY - minY;
        if (boxW <= 0 || boxH <= 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            ResetView();
            return;
        }

        // 5% margin on each side -> usable area is 90% of the canvas.
        const float marginFrac = 0.02f;
        float usableW = ClientSize.Width * (1f - 2f * marginFrac);
        float usableH = ClientSize.Height * (1f - 2f * marginFrac);

        // Box width/height in screen pixels at zoom 1 is (boxW * GridPitch).
        float zoomFitX = usableW / (boxW * GridPitch);
        float zoomFitY = usableH / (boxH * GridPitch);
        float fitZoom = Math.Min(zoomFitX, zoomFitY);
        // Fit is a deliberate one-shot operation -- allow any zoom level
        // the geometry demands, well outside the interactive 0.5..40 range.
        // A floor above zero just avoids degenerate cases.
        fitZoom = Math.Max(fitZoom, 0.001f);

        // Centre the schematic in the canvas at the chosen zoom.
        float boxCentreXgrid = (minX + maxX) / 2f;
        float boxCentreYgrid = (minY + maxY) / 2f;
        float panX = ClientSize.Width / 2f - boxCentreXgrid * fitZoom * GridPitch;
        float panY = ClientSize.Height / 2f - boxCentreYgrid * fitZoom * GridPitch;

        Zoom = fitZoom;
        PanOffset = new PointF(panX, panY);
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        // Sim mode: pressing a button symbol.
        if (ButtonPressHandler is not null && e.Button == MouseButtons.Left)
        {
            var hit = HitTestButton(e.Location);
            if (hit is not null)
            {
                heldButton = hit;
                hit.IsPressed = true;
                ButtonPressHandler(hit, true);
                Invalidate();
                return;   // don't fall through to edit-mode handling
            }
        }

        // Sim mode: clicking a switch symbol toggles it.
        if (SwitchToggleHandler is not null && e.Button == MouseButtons.Left)
        {
            var hitSw = HitTestSwitch(e.Location);
            if (hitSw is not null)
            {
                hitSw.IsClosed = !hitSw.IsClosed;
                SwitchToggleHandler(hitSw, hitSw.IsClosed);
                Invalidate();
                return;   // don't fall through to edit-mode handling
            }
        }

        // Sim mode: clicking an SPDT switch symbol flips it between throws.
        if (SpdtToggleHandler is not null && e.Button == MouseButtons.Left)
        {
            var hitSpdt = HitTestSpdt(e.Location);
            if (hitSpdt is not null)
            {
                hitSpdt.ThrowB = !hitSpdt.ThrowB;
                SpdtToggleHandler(hitSpdt, hitSpdt.ThrowB);
                Invalidate();
                return;   // don't fall through to edit-mode handling
            }
        }

        if (e.Button == MouseButtons.Right && IsPlacingWire)
        {
            CancelWirePlacement();
            return;
        }

        if (e.Button == MouseButtons.Right && IsPlacingHeaderLink)
        {
            CancelHeaderLinkPlacement();
            return;
        }

        if (e.Button == MouseButtons.Middle)
        {
            panning = true;
            panAnchor = e.Location;
            panStart = PanOffset;
            Cursor = Cursors.SizeAll;
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            var grid = ScreenToGrid(e.Location);

            if (awaitingWireStart)
            {
                var pin = Schematic.PinAt(grid);
                if (pin != null)
                {
                    wireStartPin = pin;
                    awaitingWireStart = false;
                    wirePreviewEnd = grid;
                    Invalidate();
                }
                return;
            }
            if (wireStartPin != null)
            {
                var pin = Schematic.PinAt(grid);
                if (pin != null && pin != wireStartPin)
                {
                    var connection = new Connection(wireStartPin, pin);
                    UndoStack.Do(new AddConnectionCommand(connection));

                    wireStartPin = null;
                    awaitingWireStart = true;
                    Invalidate();
                }
                return;
            }

            if (awaitingHeaderLinkStart)
            {
                if (Schematic.HitTest(grid) is HeaderOutputUnit header)
                {
                    headerLinkStart = header;
                    awaitingHeaderLinkStart = false;
                    headerLinkPreviewEnd = grid;
                    Invalidate();
                }
                return;
            }
            if (headerLinkStart != null)
            {
                if (Schematic.HitTest(grid) is HeaderOutputUnit header
                    && header != headerLinkStart)
                {
                    if (header.Pins.Count() == headerLinkStart.Pins.Count())
                    {
                        var link = new HeaderLink(headerLinkStart, header);
                        UndoStack.Do(new AddHeaderLinkCommand(link));

                        headerLinkStart = null;
                        awaitingHeaderLinkStart = true;
                        Invalidate();
                    }
                    else
                    {
                        // Pin counts differ: reject and stay armed for a valid
                        // second header.
                        System.Media.SystemSounds.Beep.Play();
                    }
                }
                return;
            }

            var hit = Schematic.HitTest(grid);
            Connection? connectionHit = hit == null
                ? HitTestConnection(grid)
                : null;
            HeaderLink? linkHit = (hit == null && connectionHit == null)
                ? HitTestHeaderLink(grid)
                : null;

            bool ctrl = (ModifierKeys & Keys.Control) == Keys.Control;

            if (hit != null)
            {
                // Ctrl + press on an ALREADY-selected item is the duplicate
                // gesture: copy the selection, paste it in place, and drag
                // the pasted copy. Ctrl + press on an UNSELECTED item still
                // toggles selection (handled in the else-if below), so the
                // two Ctrl behaviours don't collide.
                if (ctrl && hit.Selected && BeginCopyDrag(grid))
                {
                    Invalidate();
                    OnSelectionChanged();
                    return;
                }

                if (ctrl)
                {
                    // Ctrl-click toggles membership of the hit item without
                    // disturbing the rest of the selection.
                    hit.Selected = !hit.Selected;
                }
                else if (!hit.Selected)
                {
                    // Clicking an unselected item replaces the selection.
                    Schematic.ClearSelection();
                    hit.Selected = true;
                }
                // else: clicking an already-selected item without Ctrl
                // preserves the existing selection so the whole group drags.

                if (hit.Selected)
                {
                    dragging = true;
                    dragGridAnchor = grid;
                    dragItems = Schematic.Selected
                        .Select(i => (i, i.Position))
                        .ToList();
                }
            }
            else if (connectionHit != null)
            {
                if (ctrl)
                {
                    connectionHit.Selected = !connectionHit.Selected;
                }
                else if (!connectionHit.Selected)
                {
                    Schematic.ClearSelection();
                    connectionHit.Selected = true;
                }
            }
            else if (linkHit != null)
            {
                if (ctrl)
                {
                    linkHit.Selected = !linkHit.Selected;
                }
                else if (!linkHit.Selected)
                {
                    Schematic.ClearSelection();
                    linkHit.Selected = true;
                }
            }
            else
            {
                // Empty grid -- start a marquee. Don't clear selection yet;
                // commit happens on mouse-up so a click without movement
                // still ends up clearing (or preserving if Ctrl) cleanly.
                marqueeing = true;
                marqueeStart = grid;
                marqueeEnd = grid;
                marqueeCtrl = ctrl;
            }

            Invalidate();
            OnSelectionChanged();
        }
    }

    /// <summary>
    /// Start a Ctrl-drag duplicate. Opens a single "Duplicate" composite,
    /// pastes a copy of the current selection into it at zero offset, makes
    /// the pasted copy the selection, and arms a normal drag against those
    /// pasted items anchored at <paramref name="grid"/>.
    ///
    /// <para>
    /// The composite is intentionally left OPEN: OnMouseUp records the
    /// MoveItemCommands into it and closes it, so the whole gesture
    /// (paste + move) is one undo step. <see cref="copyDragInProgress"/>
    /// tells OnMouseUp to take that path.
    /// </para>
    ///
    /// <para>
    /// Returns false (composite not opened, nothing changed) if there is
    /// nothing on the clipboard to paste -- e.g. the very first Ctrl-drag in
    /// a session before any copy. The caller then falls through to normal
    /// Ctrl-click handling.
    /// </para>
    /// </summary>
    private bool BeginCopyDrag(Point grid)
    {
        // Snapshot the current selection and copy it to the clipboard. We
        // copy-then-paste (rather than cloning directly) so duplicate uses
        // exactly the same path as Ctrl+C / Ctrl+V -- one code path, no
        // second cloning implementation to keep in sync.
        var (devices, items, connections, links) = GatherSelectionForClipboard();
        if (items.Count == 0)
            return false;

        if (!ClipboardService.Copy(devices, items, connections, links, Schematic.Layers))
            return false;

        UndoStack.BeginComposite("Duplicate");
        try
        {
            var pasted = PasteFromClipboardInto(Point.Empty);
            if (pasted is null || pasted.Items.Count == 0)
            {
                // Nothing came back -- close the (empty) composite so we
                // don't leave the undo stack mid-recording. EndComposite
                // discards an empty buffer without polluting the stack.
                UndoStack.EndComposite();
                return false;
            }

            // The pasted items are now the selection (PasteFromClipboardInto
            // did that). Arm a drag against them. The composite stays open;
            // OnMouseUp closes it.
            copyDragInProgress = true;
            dragging = true;
            dragGridAnchor = grid;
            dragItems = Schematic.Selected
                .Select(i => (i, i.Position))
                .ToList();
            return true;
        }
        catch
        {
            // Defensive: if paste threw after BeginComposite, make sure the
            // composite is closed so the undo stack isn't left recording.
            UndoStack.EndComposite();
            copyDragInProgress = false;
            throw;
        }
    }

    private SwitchUnit? HitTestSwitch(Point screenPoint)
    {
        float gx = (screenPoint.X - PanOffset.X) / (Zoom * GridPitch);
        float gy = (screenPoint.Y - PanOffset.Y) / (Zoom * GridPitch);

        foreach (var item in Schematic.ActiveItems)
        {
            if (item is not SwitchUnit sw) continue;
            var b = sw.InteractiveBounds;    // in HitTestSwitch
            if (gx >= b.Left && gx <= b.Right && gy >= b.Top && gy <= b.Bottom)
                return sw;
        }
        return null;
    }

    private SpdtSwitchUnit? HitTestSpdt(Point screenPoint)
    {
        float gx = (screenPoint.X - PanOffset.X) / (Zoom * GridPitch);
        float gy = (screenPoint.Y - PanOffset.Y) / (Zoom * GridPitch);

        foreach (var item in Schematic.ActiveItems)
        {
            if (item is not SpdtSwitchUnit sp) continue;
            var b = sp.InteractiveBounds;
            if (gx >= b.Left && gx <= b.Right && gy >= b.Top && gy <= b.Bottom)
                return sp;
        }
        return null;
    }

    private ButtonUnit? HitTestButton(Point screenPoint)
    {
        // Convert screen point to grid coordinates.
        float gx = (screenPoint.X - PanOffset.X) / (Zoom * GridPitch);
        float gy = (screenPoint.Y - PanOffset.Y) / (Zoom * GridPitch);

        foreach (var item in Schematic.ActiveItems)
        {
            if (item is not ButtonUnit btn) continue;
            var b = btn.InteractiveBounds;   // in HitTestButton
            if (gx >= b.Left && gx <= b.Right && gy >= b.Top && gy <= b.Bottom)
                return btn;
        }
        return null;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        isMouseOverCanvas = false;

        if (heldButton is not null)
        {
            heldButton.IsPressed = false;
            ButtonPressHandler?.Invoke(heldButton, false);
            heldButton = null;
            Invalidate();
        }

        probeTooltip.Hide(this);
        hoveredConnection = null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // Track the cursor in grid units for keyboard/menu paste positioning.
        lastMouseGrid = ScreenToGrid(e.Location);
        isMouseOverCanvas = true;

        // Sim mode: hover-probe tooltip on wires.
        if (ProbeProvider is not null)
        {
            Point grid = ScreenToGrid(e.Location);
            Connection? hit = HitTestConnection(grid);

            if (!ReferenceEquals(hit, hoveredConnection))
            {
                hoveredConnection = hit;
                if (hit is not null)
                {
                    string? text = ProbeProvider(hit);
                    if (text is not null)
                        probeTooltip.Show(text, this, e.Location.X + 16, e.Location.Y + 16);
                    else
                        probeTooltip.Hide(this);
                }
                else
                {
                    probeTooltip.Hide(this);
                }
            }
            // don't return -- let normal mouse-move handling continue too
        }

        if (wireStartPin != null)
        {
            wirePreviewEnd = ScreenToGrid(e.Location);
            Invalidate();
            return;
        }

        if (headerLinkStart != null)
        {
            headerLinkPreviewEnd = ScreenToGrid(e.Location);
            Invalidate();
            return;
        }

        if (panning)
        {
            PanOffset = new PointF(
                panStart.X + (e.X - panAnchor.X),
                panStart.Y + (e.Y - panAnchor.Y));
            Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (marqueeing)
        {
            marqueeEnd = ScreenToGrid(e.Location);
            Invalidate();
            return;
        }

        if (dragging && dragItems.Count > 0)
        {
            var grid = ScreenToGrid(e.Location);
            int dx = grid.X - dragGridAnchor.X;
            int dy = grid.Y - dragGridAnchor.Y;
            foreach (var (item, start) in dragItems)
                item.Position = new Point(start.X + dx, start.Y + dy);
            routeCache = null;
            coincidentCornersCache = null;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        // Sim mode: releasing a held button.
        if (heldButton is not null)
        {
            heldButton.IsPressed = false;
            ButtonPressHandler?.Invoke(heldButton, false);
            heldButton = null;
            Invalidate();
            return;
        }

        if (e.Button == MouseButtons.Middle && panning)
        {
            panning = false;
            Cursor = Cursors.Default;
        }
        if (e.Button == MouseButtons.Left && dragging)
        {
            // Capture each item's destination, revert to start, then push
            // a MoveItemCommand per moved item.
            var moves = dragItems
                .Select(p => (p.Item, From: p.Start, To: p.Item.Position))
                .Where(m => m.From != m.To)
                .ToList();

            foreach (var (item, from, _) in moves)
                item.Position = from;

            if (copyDragInProgress)
            {
                // This drag began as a Ctrl-drag duplicate. BeginCopyDrag
                // already opened the "Duplicate" composite and pasted the
                // clones into it. Record the moves into that SAME composite
                // and close it, so paste + move are one undo step.
                //
                // A zero-move Ctrl-drag (press and release without dragging)
                // still produces a "Duplicate": a copy placed exactly on the
                // original. That's the documented behaviour of the gesture --
                // Ctrl-press on a selected item means "duplicate".
                if (moves.Count > 0)
                {
                    foreach (var (item, from, to) in moves)
                        UndoStack.Do(new MoveItemCommand(item, from, to));
                }
                UndoStack.EndComposite();
                copyDragInProgress = false;
            }
            else if (moves.Count > 0)
            {
                // Ordinary move drag: its own self-contained composite.
                string desc = moves.Count == 1
                    ? $"Move {moves[0].Item.GetType().Name}"
                    : $"Move {moves.Count} items";

                UndoStack.DoComposite(desc, () =>
                {
                    foreach (var (item, from, to) in moves)
                        UndoStack.Do(new MoveItemCommand(item, from, to));
                });
            }

            dragging = false;
            dragItems.Clear();
        }
        if (e.Button == MouseButtons.Left && marqueeing)
        {
            CommitMarquee();
            marqueeing = false;
            Invalidate();
            OnSelectionChanged();
        }
    }

    /// <summary>
    /// Apply the marquee selection. Left-to-right drag (end.X >= start.X)
    /// selects items strictly contained by the rectangle. Right-to-left
    /// drag selects items whose bounding rectangle overlaps. Plain marquee
    /// replaces the selection; Ctrl-marquee adds.
    /// </summary>
    private void CommitMarquee()
    {
        int minX = Math.Min(marqueeStart.X, marqueeEnd.X);
        int maxX = Math.Max(marqueeStart.X, marqueeEnd.X);
        int minY = Math.Min(marqueeStart.Y, marqueeEnd.Y);
        int maxY = Math.Max(marqueeStart.Y, marqueeEnd.Y);
        var marquee = new Rectangle(minX, minY, maxX - minX, maxY - minY);
        bool overlapMode = marqueeEnd.X < marqueeStart.X;

        // Treat a zero-area marquee (a click without drag) as just "clear
        // selection" -- skip the actual hit-test so we don't accidentally
        // pick up degenerate items.
        bool isClick = marquee.Width == 0 && marquee.Height == 0;

        if (!marqueeCtrl) Schematic.ClearSelection();
        if (isClick) return;

        foreach (var item in Schematic.ActiveItems)
        {
            var b = item.Bounds;
            bool match = overlapMode ? marquee.IntersectsWith(b) : marquee.Contains(b);
            if (match) item.Selected = true;
        }

        foreach (var c in Schematic.ActiveConnections)
        {
            var a = c.A.WorldPosition;
            var bp = c.B.WorldPosition;
            int cMinX = Math.Min(a.X, bp.X);
            int cMaxX = Math.Max(a.X, bp.X);
            int cMinY = Math.Min(a.Y, bp.Y);
            int cMaxY = Math.Max(a.Y, bp.Y);
            var cb = new Rectangle(cMinX, cMinY, cMaxX - cMinX, cMaxY - cMinY);

            bool match = overlapMode ? marquee.IntersectsWith(cb) : marquee.Contains(cb);
            if (match) c.Selected = true;
        }
    }

    // ---------------------------------------------------------------- copy / cut / paste

    private (List<Device> Devices, List<SchematicItem> Items, List<Connection> Connections, List<HeaderLink> Links)
        GatherSelectionForClipboard()
    {
        var items = Schematic.Selected.ToList();
        var connections = Schematic.SelectedConnections.ToList();
        var links = Schematic.SelectedLinks.ToList();

        var devices = items
            .OfType<Unit>()
            .Select(u => u.Device)
            .Distinct()
            .ToList();

        return (devices, items, connections, links);
    }

    /// <summary>
    /// Copy the current selection to the clipboard. No-op (and no undo
    /// entry -- copy never mutates the schematic) if nothing is selected or
    /// the clipboard write fails. Resets the paste cascade: a new payload
    /// makes the old cascade meaningless.
    /// </summary>
    public void Copy()
    {
        var (devices, items, connections, links) = GatherSelectionForClipboard();
        if (items.Count == 0) return;

        if (ClipboardService.Copy(devices, items, connections, links, Schematic.Layers))
            pasteCascadeCount = 0;
    }

    /// <summary>
    /// Cut the current selection: copy it to the clipboard, then delete the
    /// originals. The delete reuses the exact same composite-delete path as
    /// the Delete key, so undo of a cut behaves identically to undo of a
    /// delete. If the clipboard write fails the originals are left alone --
    /// a cut that didn't copy must not destroy anything. Resets the paste
    /// cascade, same as Copy.
    /// </summary>
    public void Cut()
    {
        var (devices, items, connections, links) = GatherSelectionForClipboard();
        if (items.Count == 0) return;

        if (!ClipboardService.Cut(devices, items, connections, links, Schematic.Layers))
            return;   // clipboard write failed -- don't delete the originals

        pasteCascadeCount = 0;
        HandleDelete();
    }

    /// <summary>
    /// True when there is something on the clipboard to paste. Suitable for
    /// driving an Edit-menu item's enabled state.
    /// </summary>
    public bool CanPaste => ClipboardService.CanPaste;

    /// <summary>
    /// Paste the clipboard contents.
    ///
    /// <para>
    /// If the cursor is currently over the canvas, the paste is positioned
    /// under the cursor (via <see cref="PasteAt"/>, which also resets the
    /// cascade). Otherwise the paste lands at a cascade offset that steps
    /// further up-and-right on each successive non-mouse paste, so repeated
    /// Ctrl+V doesn't stack everything on one spot.
    /// </para>
    ///
    /// <para>
    /// Mouse-driven callers (a right-click "Paste here") should use
    /// <see cref="PasteAt"/> directly to paste at an explicit point.
    /// </para>
    /// </summary>
    public void Paste()
    {
        if (isMouseOverCanvas)
        {
            PasteAt(lastMouseGrid);
            return;
        }

        pasteCascadeCount++;
        var offset = new Point(
            CascadeStep.Width * pasteCascadeCount,
            CascadeStep.Height * pasteCascadeCount);

        var pasted = PasteFromClipboardInto(offset);
        if (pasted is not null && pasted.Items.Count > 0)
        {
            Invalidate();
            OnSelectionChanged();
        }
        else
        {
            // Nothing pasted -- don't leave the cascade counter advanced for
            // a paste that didn't happen.
            pasteCascadeCount--;
        }
    }

    /// <summary>
    /// Paste the clipboard contents positioned at an explicit grid point.
    /// The pasted group is translated so its bounding-box centre lands on
    /// <paramref name="gridPoint"/>. Resets the cascade counter -- an
    /// explicitly-positioned paste is a fresh anchor.
    /// </summary>
    public void PasteAt(Point gridPoint)
    {
        // PasteFromClipboardInto takes an OFFSET, but the caller gave us a
        // target point and we can't know the payload's bounds until it has
        // been rebuilt. So: rebuild at zero offset inside a "Paste"
        // composite, measure the result, then translate it onto gridPoint
        // with MoveItemCommands recorded into the SAME composite -- the whole
        // thing stays one undo step.
        UndoStack.BeginComposite("Paste");
        try
        {
            var pasted = PasteFromClipboardInto(Point.Empty);
            if (pasted is null || pasted.Items.Count == 0)
            {
                UndoStack.EndComposite();   // discards empty buffer
                return;
            }

            // Bounding box of the freshly pasted items, in grid units.
            int minX = int.MaxValue, minY = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue;
            foreach (var item in pasted.Items)
            {
                var b = item.Bounds;
                if (b.Left < minX) minX = b.Left;
                if (b.Top < minY) minY = b.Top;
                if (b.Right > maxX) maxX = b.Right;
                if (b.Bottom > maxY) maxY = b.Bottom;
            }

            int centreX = (minX + maxX) / 2;
            int centreY = (minY + maxY) / 2;
            int dx = gridPoint.X - centreX;
            int dy = gridPoint.Y - centreY;

            if (dx != 0 || dy != 0)
            {
                foreach (var item in pasted.Items)
                {
                    var from = item.Position;
                    var to = new Point(from.X + dx, from.Y + dy);
                    item.Position = from;   // MoveItemCommand.Execute applies 'to'
                    UndoStack.Do(new MoveItemCommand(item, from, to));
                }
            }
        }
        finally
        {
            UndoStack.EndComposite();
        }

        // A mouse-positioned paste is a fresh anchor: the next cascade paste
        // starts over rather than stepping off a stale count.
        pasteCascadeCount = 0;

        Invalidate();
        OnSelectionChanged();
    }

    /// <summary>
    /// Rebuild the clipboard payload with fresh ids, offset every pasted item
    /// by <paramref name="offset"/> grid units, and add the whole result --
    /// devices, items, connections -- to the schematic as ONE composite
    /// ("Paste"). The pasted items become the new selection.
    ///
    /// <para>
    /// Returns the MapResult on success, or null if there was nothing to
    /// paste or the payload could not be rebuilt. Note this method records
    /// its own composite; <see cref="BeginCopyDrag"/> and
    /// <see cref="PasteAt"/> call it while a composite is ALREADY open, which
    /// UndoStack handles via its nesting depth counter -- the inner composite
    /// folds into the outer one.
    /// </para>
    /// </summary>
    private SchematicDtoMapper.MapResult? PasteFromClipboardInto(Point offset)
    {
        var result = ClipboardService.Paste(Schematic);
        if (result is null || result.Items.Count == 0)
            return null;

        if (offset != Point.Empty)
        {
            foreach (var item in result.Items)
                item.Position = new Point(
                    item.Position.X + offset.X,
                    item.Position.Y + offset.Y);
        }

        UndoStack.DoComposite("Paste", () =>
        {
            foreach (var device in result.Devices)
                UndoStack.Do(new AddDeviceCommand(device));
            foreach (var item in result.Items)
                UndoStack.Do(new AddItemCommand(item));
            foreach (var connection in result.Connections)
                UndoStack.Do(new AddConnectionCommand(connection));
            foreach (var link in result.Links)
                UndoStack.Do(new AddHeaderLinkCommand(link));
        });

        // Make the pasted items and connections the selection, mirroring
        // DropPart / DropSymbol.
        Schematic.ClearSelection();
        foreach (var item in result.Items)
            item.Selected = true;
        foreach (var connection in result.Connections)
            connection.Selected = true;
        foreach (var link in result.Links)
            link.Selected = true;
        OnSelectionChanged();

        return result;
    }

    // ---------------------------------------------------------------- drag/drop from library

    protected override void OnDragEnter(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(typeof(LibraryPartDragData)) == true
            || e.Data?.GetDataPresent(typeof(LibrarySymbolDragData)) == true)
            e.Effect = DragDropEffects.Copy;
        else
            e.Effect = DragDropEffects.None;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(typeof(LibraryPartDragData)) == true
            || e.Data?.GetDataPresent(typeof(LibrarySymbolDragData)) == true)
            e.Effect = DragDropEffects.Copy;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        // Take focus so keyboard shortcuts (Space, W, Delete, ...) work
        // immediately after a drop without needing a separate canvas click.
        Focus();

        var clientPt = PointToClient(new Point(e.X, e.Y));
        var grid = ScreenToGrid(clientPt);

        // Two payload shapes: a PartDefinition (chip or passive) becomes a
        // Device; a standalone symbol factory just creates a SchematicItem.
        if (e.Data?.GetData(typeof(LibraryPartDragData)) is LibraryPartDragData partData)
        {
            DropPart(partData.Definition, grid);
            return;
        }

        if (e.Data?.GetData(typeof(LibrarySymbolDragData)) is LibrarySymbolDragData symbolData)
        {
            DropSymbol(symbolData.Factory, grid);
            return;
        }
    }

    private void DropPart(PartDefinition definition, Point dropPoint)
    {
        var device = DeviceFactory.Create(definition, dropPoint, Schematic);
        var unitsToAdd = device.Units.ToList();  // snapshot in case the list mutates

        UndoStack.DoComposite($"Add {device.Designator}", () =>
        {
            UndoStack.Do(new AddDeviceCommand(device));
            foreach (var unit in unitsToAdd)
                UndoStack.Do(new AddItemCommand(unit));
        });

        Schematic.ClearSelection();
        foreach (var unit in unitsToAdd) unit.Selected = true;
        Invalidate();
        OnSelectionChanged();
    }

    private void DropSymbol(Func<SchematicItem> factory, Point dropPoint)
    {
        var item = factory();
        item.Position = new Point(
            dropPoint.X - item.Size.Width / 2,
            dropPoint.Y - item.Size.Height / 2);

        // Designated standalone items (the canned oscillator) get the next free
        // designator, unique against the current schematic.
        if (item is IDesignatedItem designated)
            designated.Designator = Schematic.NextDesignator(designated.ReferencePrefix);

        UndoStack.DoComposite($"Add {item.GetType().Name}", () =>
        {
            UndoStack.Do(new AddItemCommand(item));
        });

        Schematic.ClearSelection();
        item.Selected = true;
        Invalidate();
        OnSelectionChanged();
    }

    // ---------------------------------------------------------------- rotation

    /// <summary>True iff exactly one item is selected and zero connections are.
    /// Rotation is gated on this so multi-select rotation doesn't surprise
    /// the user with per-item rotations around per-item centres.</summary>
    public bool CanRotateSelection
    {
        get
        {
            int itemCount = Schematic.Selected.Count();
            int connectionCount = Schematic.SelectedConnections.Count();
            return itemCount == 1 && connectionCount == 0;
        }
    }

    /// <summary>
    /// Rotate the single selected item 90 degrees clockwise (or counter-
    /// clockwise if <paramref name="clockwise"/> is false). No-op if more
    /// than one item / a connection is selected.
    /// </summary>
    public void RotateSelection(bool clockwise)
    {
        if (!CanRotateSelection) return;
        var item = Schematic.Selected.First();
        var from = item.Rotation;
        var to = clockwise
            ? RotateCw(from)
            : RotateCcw(from);
        if (from == to) return;

        UndoStack.DoComposite($"Rotate {item.GetType().Name}", () =>
        {
            UndoStack.Do(new RotateItemCommand(item, from, to));
        });
    }

    private static Rotation RotateCw(Rotation r) => r switch
    {
        Rotation.R0 => Rotation.R90,
        Rotation.R90 => Rotation.R180,
        Rotation.R180 => Rotation.R270,
        Rotation.R270 => Rotation.R0,
        _ => r
    };

    private static Rotation RotateCcw(Rotation r) => r switch
    {
        Rotation.R0 => Rotation.R270,
        Rotation.R90 => Rotation.R0,
        Rotation.R180 => Rotation.R90,
        Rotation.R270 => Rotation.R180,
        _ => r
    };

    // ---------------------------------------------------------------- keyboard

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Ctrl + C / X / V -- copy / cut / paste. Checked before the
        // unmodified-key branches below so e.g. Ctrl+V isn't mistaken for a
        // bare keystroke. Each sets Handled so the key doesn't travel on.
        if (e.Control && e.KeyCode == Keys.C)
        {
            Copy();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.Control && e.KeyCode == Keys.X)
        {
            Cut();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.Control && e.KeyCode == Keys.V)
        {
            Paste();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Delete)
        {
            HandleDelete();
        }
        else if (e.KeyCode == Keys.Home)
        {
            ResetView();
        }
        else if (e.KeyCode == Keys.Escape && (awaitingWireStart || wireStartPin != null))
        {
            CancelWirePlacement();
        }
        else if (e.KeyCode == Keys.Escape && (awaitingHeaderLinkStart || headerLinkStart != null))
        {
            CancelHeaderLinkPlacement();
        }
        else if (e.KeyCode == Keys.Escape && marqueeing)
        {
            marqueeing = false;
            Invalidate();
        }
        else if (e.KeyCode == Keys.W && !awaitingWireStart && wireStartPin == null)
        {
            BeginWirePlacement();
        }
        else if (e.KeyCode == Keys.L && !awaitingHeaderLinkStart && headerLinkStart == null
                 && !awaitingWireStart && wireStartPin == null)
        {
            BeginHeaderLinkPlacement();
        }
        else if (e.KeyCode == Keys.Space)
        {
            bool ccw = (e.Modifiers & Keys.Shift) == Keys.Shift;
            RotateSelection(clockwise: !ccw);
            e.Handled = true;
            e.SuppressKeyPress = true;  // don't let Space trigger anything else
        }
        else if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down
                 && e.Modifiers == Keys.None)
        {
            // One press = one one-cell nudge. OnKeyDown fires repeatedly while
            // the key is held; nudgeKeyHeld blocks all but the first event for
            // the current physical press.
            if (nudgeKeyHeld != e.KeyCode)
            {
                nudgeKeyHeld = e.KeyCode;
                int dx = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : 0;
                int dy = e.KeyCode == Keys.Up ? -1 : e.KeyCode == Keys.Down ? 1 : 0;
                NudgeSelection(dx, dy);
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == nudgeKeyHeld) nudgeKeyHeld = Keys.None;
    }


    /// <summary>
    /// Delete-key handler. Selecting any Unit promotes to deleting the entire
    /// Device (all its units, its power unit if any, and the Device record).
    /// Selected non-Unit items (VccSymbol, GndSymbol) and selected connections
    /// are deleted directly. Connections touching anything being deleted are
    /// deleted implicitly. Everything goes in one composite.
    /// </summary>
    private void HandleDelete()
    {
        var selectedItems = Schematic.Selected.ToList();
        var selectedConnections = Schematic.SelectedConnections.ToList();

        // Devices selected via any of their units. Each device contributes
        // ALL its units (and its power unit, if any) to the deletion set.
        var devicesToDelete = new HashSet<Device>(
            selectedItems.OfType<Unit>().Select(u => u.Device));

        // All units that go because their device is going.
        var unitsImplicit = new HashSet<SchematicItem>();
        foreach (var device in devicesToDelete)
        {
            foreach (var unit in device.Units)
                unitsImplicit.Add(unit);
            if (device.PowerUnit != null)
                unitsImplicit.Add(device.PowerUnit);
        }

        // Non-Unit items the user explicitly selected (VCC, GND).
        var nonUnitItemsSelected = selectedItems
            .Where(i => i is not Unit)
            .ToList();

        // Items going away in total: implicit-via-device-closure + explicit-non-unit.
        var allItemsToRemove = new HashSet<SchematicItem>(unitsImplicit);
        foreach (var i in nonUnitItemsSelected) allItemsToRemove.Add(i);

        // Connections implicitly removed because an attached item is going.
        var implicitConnections = new HashSet<Connection>();
        foreach (var item in allItemsToRemove)
            foreach (var c in Schematic.ConnectionsOn(item))
                implicitConnections.Add(c);

        foreach (var c in selectedConnections)
            implicitConnections.Remove(c);

        // Header links implicitly removed because an attached header is going.
        var implicitLinks = new HashSet<HeaderLink>();
        foreach (var item in allItemsToRemove)
            foreach (var l in Schematic.LinksOn(item))
                implicitLinks.Add(l);

        var selectedLinks = Schematic.SelectedLinks.ToList();
        foreach (var l in selectedLinks)
            implicitLinks.Remove(l);

        int total = allItemsToRemove.Count + selectedConnections.Count
                  + implicitConnections.Count + devicesToDelete.Count
                  + selectedLinks.Count + implicitLinks.Count;
        if (total == 0) return;

        string description;
        if (devicesToDelete.Count == 1 && allItemsToRemove.Count == devicesToDelete.First().Units.Count
            && nonUnitItemsSelected.Count == 0 && selectedConnections.Count == 0 && selectedLinks.Count == 0)
        {
            description = $"Delete {devicesToDelete.First().Designator}";
        }
        else if (devicesToDelete.Count == 0 && nonUnitItemsSelected.Count == 1
                 && selectedConnections.Count == 0 && selectedLinks.Count == 0)
        {
            description = $"Delete {nonUnitItemsSelected[0].GetType().Name}";
        }
        else if (devicesToDelete.Count == 0 && nonUnitItemsSelected.Count == 0
                 && selectedConnections.Count == 1 && selectedLinks.Count == 0)
        {
            description = "Delete Connection";
        }
        else if (devicesToDelete.Count == 0 && nonUnitItemsSelected.Count == 0
                 && selectedConnections.Count == 0 && selectedLinks.Count == 1)
        {
            description = "Delete Header Link";
        }
        else
        {
            int visibleTotal = devicesToDelete.Count + nonUnitItemsSelected.Count
                             + selectedConnections.Count + selectedLinks.Count;
            description = $"Delete {visibleTotal} items";
        }

        UndoStack.DoComposite(description, () =>
        {
            // Order matters for clean undo: connections first, then items, then devices.
            // Undo replays in reverse so devices come back first, then items, then connections.
            foreach (var c in implicitConnections)
                UndoStack.Do(new RemoveConnectionCommand(c));
            foreach (var c in selectedConnections)
                UndoStack.Do(new RemoveConnectionCommand(c));
            foreach (var l in implicitLinks)
                UndoStack.Do(new RemoveHeaderLinkCommand(l));
            foreach (var l in selectedLinks)
                UndoStack.Do(new RemoveHeaderLinkCommand(l));
            foreach (var item in allItemsToRemove)
                UndoStack.Do(new RemoveItemCommand(item));
            foreach (var device in devicesToDelete)
                UndoStack.Do(new RemoveDeviceCommand(device));
        });

        Invalidate();
        OnSelectionChanged();
    }

    /// <summary>
    /// Move every selected item by (dx, dy) grid units as one composite undo
    /// step. Skipped while a drag, wire-placement, or marquee is in progress
    /// -- those gestures own the canvas. Selected connections are ignored
    /// (they have no Position; they follow their endpoint pins).
    /// </summary>
    private void NudgeSelection(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        if (dragging || marqueeing || awaitingWireStart || wireStartPin != null) return;

        var items = Schematic.Selected.ToList();
        if (items.Count == 0) return;

        string desc = items.Count == 1
            ? $"Nudge {items[0].GetType().Name}"
            : $"Nudge {items.Count} items";

        UndoStack.DoComposite(desc, () =>
        {
            foreach (var item in items)
            {
                var from = item.Position;
                var to = new Point(from.X + dx, from.Y + dy);
                UndoStack.Do(new MoveItemCommand(item, from, to));
            }
        });

        Invalidate();
    }

}