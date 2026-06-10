using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// Everything a chip's optional <see cref="ChipPartDefinition.Decorate"/>
/// delegate needs to draw cosmetic detail (gate glyphs, leads, internal
/// structure) inside the box, without exposing the unit's internals.
///
/// <para><b>Local coordinate frame.</b> Before the delegate runs,
/// <see cref="ChipUnit"/> applies a TranslateTransform that puts (0,0) at the
/// TOP-CENTRE of the body, with +x to the right and +y downward. All
/// coordinates the decorator sees -- <see cref="Body"/>, <see cref="PinAt"/>,
/// the static helpers in <see cref="Decor"/> -- live in this local frame.
/// This makes per-chip decorator code position-independent: a 7400 lays out
/// the same whether the chip is dropped at (10,10) or (200,300).</para>
///
/// <para>The decorator runs AFTER the body fill but BEFORE the body outline,
/// pin stubs, pin names, and designator; those paint on top, so cosmetic
/// detail sits visually BEHIND the chip's primary geometry.</para>
/// </summary>
public sealed class ChipDecoration
{
    public Graphics G { get; }
    public RenderContext Ctx { get; }

    /// <summary>
    /// Body rectangle in LOCAL coordinates (top-centre of body = (0,0)).
    /// Body.Left and Body.Right are -bodyHalfWidth and +bodyHalfWidth in pixels;
    /// Body.Top is 0; Body.Bottom is the body height in pixels.
    /// </summary>
    public RectangleF Body { get; }

    /// <summary>Grid pitch in pixels. Decorators size traces and bodies as multiples of this.</summary>
    public float GridPitch => Ctx.GridPitch;

    private readonly Func<int, PointF> pinLookup;

    public ChipDecoration(Graphics g, RenderContext ctx, RectangleF body, Func<int, PointF> pinLookup)
    {
        G = g;
        Ctx = ctx;
        Body = body;
        this.pinLookup = pinLookup;
    }

    /// <summary>
    /// Outer stub endpoint (wire attachment point) of the given pin number, in
    /// LOCAL pixel coordinates. For a chip whose body is 8 cells wide with
    /// GridPitch=5, the leftmost pin on the left side returns x = -25 (one
    /// cell outside the body, since the body half-width is 20px and the pin
    /// sits one cell further out).
    /// </summary>
    public PointF PinAt(int pinNumber) => pinLookup(pinNumber);

    // ====================================================================
    // Static decorator helpers. Stateless. Each helper takes the
    // ChipDecoration as its first argument and renders into d.G in d's local
    // coordinate frame. Helpers are composable: a per-chip decorator is
    // typically one or two calls to Decor.Array(...).
    // ====================================================================

    /// <summary>
    /// Drawing helpers for chip decorators. Stateless -- each method takes the
    /// <see cref="ChipDecoration"/> and produces output in the decoration's
    /// local coordinate frame.
    ///
    /// The gate-drawing methods (<see cref="NandGate"/>, <see cref="AndGate"/>,
    /// etc.) take pin numbers rather than coordinates so they can be reused
    /// across any chip that hosts that gate kind. A 2-input NAND on a 7400
    /// and a 3-input NAND on a 7410 use the SAME helper -- input count is
    /// driven by the length of the inputs array.
    ///
    /// Inverting vs non-inverting variants (NAND/AND, NOR/OR, XNOR/XOR) share
    /// the same body geometry and lead routing. Each helper passes an
    /// <c>inverting</c> flag to the internal drawer so that (a) the right
    /// glyph painter is used (with or without the output bubble) and (b) the
    /// short "stem" between the body's apex and the output trace is only
    /// drawn for non-inverting gates (for inverting gates the bubble fills
    /// that space, and drawing a stem through it would visibly cross the
    /// bubble outline).
    /// </summary>
    public static class Decor
    {
        /// <summary>
        /// Cosmetic gray for production gate glyphs and leads. Sits behind the
        /// box's black outline and labels. In DEBUG builds we substitute cyan
        /// so the decorator geometry pops against the body fill and the
        /// DeepPink debug grid -- makes alignment problems obvious at a glance.
        /// </summary>
#if DEBUG
        public static readonly Color GlyphColor = Color.Cyan;
#else
        public static readonly Color GlyphColor = Color.FromArgb(150, 150, 150);
#endif

        // ------------------------------------------------------------------
        // Top-level gate-array convenience: draw N gates of the same kind.
        // Typical per-chip decorator:
        //   Decorate: d => Decor.Array(d, Decor.NandGate,
        //       (new[]{1,2}, 3), (new[]{4,5}, 6),
        //       (new[]{9,10}, 8), (new[]{12,13}, 11));
        // ------------------------------------------------------------------

        /// <summary>Function shape of a single-gate drawer (NandGate, AndGate, ...).</summary>
        public delegate void GateDrawer(ChipDecoration d, int[] inputs, int output);

        /// <summary>
        /// Draw one or more gates of the same kind. Each tuple is one gate's
        /// (input pin numbers, output pin number). Pin groupings must match
        /// ChipFactory's wiring so the drawn symbol reflects the simulation.
        /// </summary>
        public static void Array(
            ChipDecoration d,
            GateDrawer drawer,
            params (int[] Inputs, int Output)[] gates)
        {
            foreach (var (inputs, output) in gates)
                drawer(d, inputs, output);
        }

        // ------------------------------------------------------------------
        // Per-gate helpers. All share the same routing model:
        //   - body sits on the cluster side (left, right, or centred when
        //     inputs straddle), oriented vertically. By default the flat
        //     "top" edge faces UP and the cap/bubble points DOWN, with input
        //     leads coming in from above and the output exiting below. When
        //     the output pin sits ABOVE the input pins (e.g. the 7402's
        //     output-first pinout) the gate is Y-flipped so the bubble
        //     points UP toward the output and the flat edge faces DOWN.
        //   - input leads run from the flat edge through nested columns and
        //     out to each input pin's stub-edge row,
        //   - output lead drops from the body apex (via a short stem on
        //     non-inverting gates, or via the bubble on inverting gates) to
        //     the output pin row.
        // The differences between gate kinds are (a) which glyph to draw and
        // (b) whether the gate is inverting.
        // ------------------------------------------------------------------

        public static void NandGate(ChipDecoration d, int[] inputs, int output) =>
            DrawGateInternal(d, inputs, output, inverting: true,
                (g, pen, fill, body, _) => GateGlyphs.DrawNand(g, pen, fill, body));

        public static void AndGate(ChipDecoration d, int[] inputs, int output) =>
            DrawGateInternal(d, inputs, output, inverting: false,
                (g, pen, fill, body, _) => GateGlyphs.DrawAnd(g, pen, fill, body, inverting: false));

        public static void NorGate(ChipDecoration d, int[] inputs, int output) =>
            DrawGateInternal(d, inputs, output, inverting: true,
                (g, pen, fill, body, _) => GateGlyphs.DrawNor(g, pen, fill, body));

        public static void OrGate(ChipDecoration d, int[] inputs, int output) =>
            DrawGateInternal(d, inputs, output, inverting: false,
                (g, pen, fill, body, _) => GateGlyphs.DrawOr(g, pen, fill, body, inverting: false));

        public static void XnorGate(ChipDecoration d, int[] inputs, int output) =>
            DrawGateInternal(d, inputs, output, inverting: true,
                (g, pen, fill, body, _) => GateGlyphs.DrawXor(g, pen, fill, body, inverting: true));

        public static void XorGate(ChipDecoration d, int[] inputs, int output) =>
            DrawGateInternal(d, inputs, output, inverting: false,
                (g, pen, fill, body, _) => GateGlyphs.DrawXor(g, pen, fill, body, inverting: false));

        public static void NotGate(ChipDecoration d, int[] inputs, int output) =>
            DrawGateInternal(d, inputs, output, inverting: true,
                (g, pen, fill, body, _) => GateGlyphs.DrawNot(g, pen, fill, body));

        // ------------------------------------------------------------------
        // STUBS: gate variants not yet rendered with their distinctive
        // markings. Each falls back to the closest existing helper so the
        // chip's Decorate wiring is in place and the schematic doesn't need
        // to be re-edited when the proper glyph is added. Replace each stub
        // body with the correct GateGlyphs call when the glyph exists.
        // ------------------------------------------------------------------

        /// <summary>
        /// Schmitt-trigger inverter (74x14). Same body, bubble, and pin
        /// topology as the plain inverter, with a small hysteresis glyph
        /// drawn inside the triangle.
        /// </summary>
        public static void SchmittInverterGate(ChipDecoration d, int[] inputs, int output) =>
            DrawGateInternal(d, inputs, output, inverting: true,
                (g, pen, fill, body, xFlipped) => GateGlyphs.DrawSchmittNot(g, pen, fill, body, xFlipped));

        /// <summary>
        /// Non-inverting buffer (e.g. 74x244 / 74x245 / 74x541 family). Same
        /// triangle body as an inverter, with NO output bubble. Drawn as a
        /// non-inverting gate so the short output stem (rather than a bubble)
        /// bridges the body apex to the output trace.
        /// </summary>
        public static void BufferGate(ChipDecoration d, int[] inputs, int output) =>
            DrawGateInternal(d, inputs, output, inverting: false,
                (g, pen, fill, body, _) => GateGlyphs.DrawBuffer(g, pen, fill, body));

        /// <summary>Column offset (grid cells from the body centreline) where
        /// the offset (two-bank) buffer layout places its triangle. The 244's
        /// /OE enable buses pass this (signed per column) so the vertical bus
        /// lines up behind the buffer triangles.</summary>
        public const float HBufferColumnCells = 1.4f;

        /// <summary>
        /// Horizontal non-inverting buffer for the octal bus drivers (74x244
        /// and friends), offset toward its input side so two banks form two
        /// columns. See <see cref="DrawHBuffer"/> for the geometry.
        /// </summary>
        public static void HBuffer(ChipDecoration d, int[] inputs, int output) =>
            DrawHBuffer(d, inputs[0], output, HBufferColumnCells);

        /// <summary>
        /// Horizontal non-inverting buffer centred on the chip's vertical axis
        /// (no bank offset). Used by single-direction drivers like the 74x541
        /// where all inputs are on one side and all outputs on the other.
        /// </summary>
        public static void HBufferCentered(ChipDecoration d, int[] inputs, int output) =>
            DrawHBuffer(d, inputs[0], output, colOffsetCells: 0f);

        /// <summary>
        /// Core horizontal buffer drawer. Lays the triangle FLAT -- flat edge
        /// facing the input pin, apex facing the output -- a little SMALLER
        /// than the inverter glyph, and routes both leads with a dog-leg kink.
        ///
        /// <para>The long horizontal run sits on the buffer's own MID-ROW
        /// (halfway between its input- and output-pin rows) and only kinks to
        /// the pin's row at the far wall. Because adjacent buffers from
        /// opposite banks have different mid-rows, their long runs never land
        /// on the same horizontal line -- so opposite-gate wiring doesn't
        /// coincide. A non-zero <paramref name="colOffsetCells"/> offsets the
        /// triangle toward its input side so left-fed and right-fed buffers
        /// sit in two distinct columns; zero centres it on the chip axis.</para>
        ///
        /// <para>Direction is inferred from the input pin's side: input on the
        /// left -> apex points right; input on the right -> apex points left.</para>
        /// </summary>
        private static void DrawHBuffer(ChipDecoration d, int inputPin, int outputPin, float colOffsetCells)
        {
            var g = d.G;
            float p = d.GridPitch;
            var pin = d.PinAt(inputPin);
            var pout = d.PinAt(outputPin);

            using var fill = new SolidBrush(d.Ctx.FillColor);
            using var outline = new Pen(GlyphColor, 0.45f) { LineJoin = LineJoin.Miter };
            using var tracePen = new Pen(GlyphColor, 0.45f) { LineJoin = LineJoin.Miter };

            bool inputOnLeft = pin.X < 0;
            float dirX = inputOnLeft ? +1f : -1f;   // signal flow: +1 = L->R

            // Triangle deliberately smaller than the inverter glyph (~1.25p).
            float depth = p * 1.0f;     // flat edge -> apex (horizontal)
            float height = p * 1.1f;    // flat edge length (vertical)

            // Mid-row: halfway between input and output pin rows. The triangle
            // and its long output run both live here.
            float midY = (pin.Y + pout.Y) / 2f;

            // Column: offset toward the INPUT side so the two banks sit in
            // separate columns either side of the chip centreline. Zero
            // centres the triangle on the chip axis.
            float colOffset = p * colOffsetCells;
            float cx = (inputOnLeft ? -1f : +1f) * colOffset;

            float apexX = cx + dirX * depth / 2f;
            float flatX = cx - dirX * depth / 2f;

            // Body walls the leads terminate on (the chip draws the stubs
            // beyond, out to the pin endpoints).
            float inWallX = inputOnLeft ? d.Body.Left : d.Body.Right;
            float outWallX = pout.X < 0 ? d.Body.Left : d.Body.Right;

            // --- Triangle -------------------------------------------------
            var tri = RectangleF.FromLTRB(
                cx - depth / 2f, midY - height / 2f,
                cx + depth / 2f, midY + height / 2f);
            if (dirX > 0)
            {
                GateGlyphs.DrawBuffer(g, outline, fill, tri);
            }
            else
            {
                // Mirror about the column centre so the apex points left.
                var st = g.Save();
                g.TranslateTransform(cx, 0f);
                g.ScaleTransform(-1f, 1f);
                g.TranslateTransform(-cx, 0f);
                GateGlyphs.DrawBuffer(g, outline, fill, tri);
                g.Restore(st);
            }

            // --- Input lead: wall -> kink -> flat edge --------------------
            float inTurnX = flatX - dirX * p * 0.6f;
            g.DrawLines(tracePen, new[]
            {
                new PointF(inWallX, pin.Y),
                new PointF(inTurnX, pin.Y),
                new PointF(inTurnX, midY),
                new PointF(flatX,   midY),
            });

            // --- Output lead: apex -> long run at midY -> kink -> wall ----
            float outTurnX = outWallX - dirX * p * 1.0f;
            g.DrawLines(tracePen, new[]
            {
                new PointF(apexX,    midY),
                new PointF(outTurnX, midY),
                new PointF(outTurnX, pout.Y),
                new PointF(outWallX, pout.Y),
            });
        }

        // ----- 74x244 output-enable cluster --------------------------------
        // The /OE inverter and its vertical enable bus are drawn in two phases
        // so they straddle the buffer triangles in the z-order:
        //   * OeEnableBus  -- the vertical bus; call BEFORE the HBuffer array
        //                     so the buffers paint over it (it threads BEHIND).
        //   * OeEnableGate -- the inverter + /OE lead; call AFTER the array so
        //                     the inverter sits IN FRONT of any buffer wire
        //                     that crosses its row.
        // Both take the same column/gate-centre values; keep the calls paired.

        private const float OeGateTopEdgeCells = 1.1f;  // inverter top (input) edge
        private const float OeGateDepthCells = 1.0f;    // top edge -> apex (down)
        private const float OeBubbleCells = 0.4f;       // inverting-input bubble

        /// <summary>
        /// Vertical enable bus for a 74x244 bank: a straight drop down the
        /// bank's column from the inverter apex to the last buffer's mid-row.
        /// Drawn BEFORE the buffers so they paint over it.
        ///
        /// <para><paramref name="columnCells"/> is the bus x (grid cells from
        /// the body centreline), signed for the column side -- pass the same
        /// magnitude HBuffer uses for that bank (<see cref="HBufferColumnCells"/>).
        /// <paramref name="gateCenterYCells"/> matches the paired
        /// <see cref="OeEnableGate"/> call; <paramref name="busBottomYCells"/>
        /// is the mid-row of the LAST buffer in the bank.</para>
        /// </summary>
        public static void OeEnableBus(
            ChipDecoration d, float columnCells, float gateCenterYCells, float busBottomYCells)
        {
            float p = d.GridPitch;
            float cx = columnCells * p;
            float apexY = gateCenterYCells * p + OeGateDepthCells * p / 2f;
            float busBottom = busBottomYCells * p;

            using var tracePen = new Pen(GlyphColor, 0.45f) { LineJoin = LineJoin.Miter };
            d.G.DrawLine(tracePen, cx, apexY, cx, busBottom);
        }

        /// <summary>
        /// The /OE inverter for a 74x244 bank: a small downward-pointing
        /// inverting-input buffer (bubble on the top edge) with a lead in from
        /// the /OE pin. Drawn AFTER the buffer array so it sits in front of any
        /// buffer output run that crosses its row. Pair with
        /// <see cref="OeEnableBus"/> using the same column/gate-centre values.
        /// </summary>
        public static void OeEnableGate(
            ChipDecoration d, int oePin, float columnCells, float gateCenterYCells)
        {
            var g = d.G;
            float p = d.GridPitch;
            var oe = d.PinAt(oePin);

            using var fill = new SolidBrush(d.Ctx.FillColor);
            using var outline = new Pen(GlyphColor, 0.45f) { LineJoin = LineJoin.Miter };
            using var tracePen = new Pen(GlyphColor, 0.45f) { LineJoin = LineJoin.Miter };

            float cx = columnCells * p;
            float gateCy = gateCenterYCells * p;

            float w = OeGateTopEdgeCells * p;     // top (input) edge length
            float h = OeGateDepthCells * p;       // top edge -> apex (downward)
            float topY = gateCy - h / 2f;
            float apexY = gateCy + h / 2f;
            float bubbleD = OeBubbleCells * p;
            float bubbleTopY = topY - bubbleD;

            float oeWallX = oe.X < 0 ? d.Body.Left : d.Body.Right;

            // /OE pin -> column -> top of the inverting bubble.
            g.DrawLines(tracePen, new[]
            {
                new PointF(oeWallX, oe.Y),
                new PointF(cx, oe.Y),
                new PointF(cx, bubbleTopY),
            });

            // Inverting-input bubble (sits just above the flat top edge).
            var bubble = new RectangleF(cx - bubbleD / 2f, bubbleTopY, bubbleD, bubbleD);
            g.FillEllipse(fill, bubble);
            g.DrawEllipse(outline, bubble);

            // Downward triangle.
            using var tri = new GraphicsPath();
            tri.AddPolygon(new[]
            {
                new PointF(cx - w / 2f, topY),
                new PointF(cx + w / 2f, topY),
                new PointF(cx, apexY),
            });
            g.FillPath(fill, tri);
            g.DrawPath(outline, tri);
        }

        /// <summary>
        /// Horizontal BIDIRECTIONAL transceiver glyph (74x245): a centred
        /// back-to-back diamond (see <see cref="GateGlyphs.DrawBidirectional"/>)
        /// with a dog-leg lead to each side. <paramref name="inputs"/>[0] and
        /// <paramref name="output"/> are simply the two data pins (e.g. A and B);
        /// direction isn't depicted. Leads use the same own-mid-row kink as
        /// <see cref="HBuffer"/>, so opposite-channel runs don't coincide.
        /// </summary>
        public static void HBidir(ChipDecoration d, int[] inputs, int output)
        {
            var g = d.G;
            float p = d.GridPitch;
            var pinA = d.PinAt(inputs[0]);
            var pinB = d.PinAt(output);

            using var fill = new SolidBrush(d.Ctx.FillColor);
            using var outline = new Pen(GlyphColor, 0.45f) { LineJoin = LineJoin.Miter };
            using var tracePen = new Pen(GlyphColor, 0.45f) { LineJoin = LineJoin.Miter };

            float midY = (pinA.Y + pinB.Y) / 2f;
            float halfW = p * 0.7f;     // left apex .. right apex (diamond width/2)
            float halfH = p * 0.55f;    // top .. bottom (diamond height/2)

            float leftApexX = -halfW;
            float rightApexX = +halfW;

            var body = RectangleF.FromLTRB(leftApexX, midY - halfH, rightApexX, midY + halfH);
            GateGlyphs.DrawBidirectional(g, outline, fill, body);

            // One dog-leg lead per side: wall -> kink to mid-row -> apex.
            DrawSideLead(g, tracePen, pinA, midY, leftApexX, rightApexX, d.Body, p, nearWall: true);
            DrawSideLead(g, tracePen, pinB, midY, leftApexX, rightApexX, d.Body, p, nearWall: false);
        }

        // Lead from a data pin to the nearer diamond apex. nearWall=true routes
        // the short horizontal at the PIN row then drops to the apex row (the A
        // side); false runs along the apex mid-row then kinks out to the pin (B
        // side). Splitting the two keeps the long runs on different rows so the
        // two sides never share a horizontal line.
        private static void DrawSideLead(
            Graphics g, Pen pen, PointF pin, float midY,
            float leftApexX, float rightApexX, RectangleF body, float p, bool nearWall)
        {
            bool onLeft = pin.X < 0;
            float wallX = onLeft ? body.Left : body.Right;
            float apexX = onLeft ? leftApexX : rightApexX;
            float dir = onLeft ? -1f : +1f;

            if (nearWall)
            {
                float turnX = apexX + dir * p * 0.6f;
                g.DrawLines(pen, new[]
                {
                    new PointF(wallX, pin.Y),
                    new PointF(turnX, pin.Y),
                    new PointF(turnX, midY),
                    new PointF(apexX, midY),
                });
            }
            else
            {
                float turnX = wallX - dir * p * 1.0f;
                g.DrawLines(pen, new[]
                {
                    new PointF(apexX, midY),
                    new PointF(turnX, midY),
                    new PointF(turnX, pin.Y),
                    new PointF(wallX, pin.Y),
                });
            }
        }

        // ----- 74x541 dual-/OE enable -------------------------------------
        // The 541 enables its outputs only when BOTH /OE1 and /OE2 are LOW, so
        // the control gate is a 2-input AND with both inputs inverted (bubbles).
        // Drawn in two phases like the 244 cluster: Oe541EnableBus before the
        // buffer array (threads behind), Oe541EnableGate after it (in front).

        private const float Oe541GateCenterYCells = 2.5f; // AND-body centre Y
        private const float Oe541SizeCells = 1.4f;        // AND body (square D)
        private const float Oe541InputSpreadCells = 0.3f; // half the input gap

        /// <summary>
        /// Vertical enable bus for the 74x541: a straight drop from the AND
        /// gate's output down the (centred) buffer column to the last buffer's
        /// mid-row. Call BEFORE the buffer array. <paramref name="columnCells"/>
        /// is normally 0 (the buffers are centred); <paramref name="busBottomYCells"/>
        /// is the mid-row of the last buffer.
        /// </summary>
        public static void Oe541EnableBus(ChipDecoration d, float columnCells, float busBottomYCells)
        {
            float p = d.GridPitch;
            float cx = columnCells * p;
            float apexY = Oe541GateCenterYCells * p + Oe541SizeCells * p / 2f;
            float busBottom = busBottomYCells * p;

            using var tracePen = new Pen(GlyphColor, 0.45f) { LineJoin = LineJoin.Miter };
            d.G.DrawLine(tracePen, cx, apexY, cx, busBottom);
        }

        /// <summary>
        /// The 74x541 enable gate: a downward 2-input AND with both inputs
        /// inverted, fed by the two /OE pins. /OE1 (upper pin) drops straight
        /// into the near input; /OE2 (lower pin) routes in, up, and over the
        /// top of the gate into the far input so it never crosses the body.
        /// Call AFTER the buffer array so the gate sits in front of the bus.
        /// </summary>
        public static void Oe541EnableGate(ChipDecoration d, int oe1Pin, int oe2Pin, float columnCells)
        {
            var g = d.G;
            float p = d.GridPitch;
            var oe1 = d.PinAt(oe1Pin);
            var oe2 = d.PinAt(oe2Pin);

            using var fill = new SolidBrush(d.Ctx.FillColor);
            using var outline = new Pen(GlyphColor, 0.45f) { LineJoin = LineJoin.Miter };
            using var tracePen = new Pen(GlyphColor, 0.45f) { LineJoin = LineJoin.Miter };

            float cx = columnCells * p;
            float gateCy = Oe541GateCenterYCells * p;
            float size = Oe541SizeCells * p;
            float gateTop = gateCy - size / 2f;
            float spread = Oe541InputSpreadCells * p;
            float bubbleD = OeBubbleCells * p;
            float bubbleTopY = gateTop - bubbleD;

            // AND body, rotated +90 so the flat (input) edge faces up and the
            // cap (output) points down toward the bus. Same +90 convention the
            // gate-array decorators use (flat up, cap down).
            var st = g.Save();
            g.TranslateTransform(cx, gateCy);
            g.RotateTransform(90f);
            g.TranslateTransform(-cx, -gateCy);
            var hbody = RectangleF.FromLTRB(
                cx - size / 2f, gateCy - size / 2f, cx + size / 2f, gateCy + size / 2f);
            GateGlyphs.DrawAnd(g, outline, fill, hbody, inverting: false);
            g.Restore(st);

            // Two inverting-input bubbles on the (now horizontal) top edge.
            float leftInX = cx - spread;
            float rightInX = cx + spread;
            foreach (float bx in new[] { leftInX, rightInX })
            {
                var bub = new RectangleF(bx - bubbleD / 2f, bubbleTopY, bubbleD, bubbleD);
                g.FillEllipse(fill, bub);
                g.DrawEllipse(outline, bub);
            }

            // /OE1 (upper pin) -> near input, straight down from its own row.
            float oe1WallX = oe1.X < 0 ? d.Body.Left : d.Body.Right;
            float oe1InX = oe1.X < 0 ? leftInX : rightInX;
            g.DrawLines(tracePen, new[]
            {
                new PointF(oe1WallX, oe1.Y),
                new PointF(oe1InX, oe1.Y),
                new PointF(oe1InX, bubbleTopY),
            });

            // /OE2 (lower pin) -> far input: in, up to /OE1's row, over, down --
            // clearing the gate body on the way.
            float oe2WallX = oe2.X < 0 ? d.Body.Left : d.Body.Right;
            float oe2InX = oe2.X < 0 ? leftInX : rightInX;
            float oe2Dir = oe2.X < 0 ? -1f : +1f;
            float clearX = cx + oe2Dir * (size / 2f + p * 0.3f);
            g.DrawLines(tracePen, new[]
            {
                new PointF(oe2WallX, oe2.Y),
                new PointF(clearX, oe2.Y),
                new PointF(clearX, oe1.Y),
                new PointF(oe2InX, oe1.Y),
                new PointF(oe2InX, bubbleTopY),
            });
        }

        // ----- 74x245 single-/OE enable -----------------------------------
        // The 245's /OE enables the whole transceiver when LOW, so one
        // inverting-input buffer suffices. Drawn HORIZONTAL (apex at the centre
        // line, flat/input edge to the right) so it tucks into the narrow band
        // above the first diamond without colliding with it. Its apex feeds the
        // centre enable bus, which runs down the diamonds' shared-base column.
        // Two phases: Oe245EnableBus before the array, Oe245EnableGate after.

        private const float Oe245GateCenterYCells = 2.0f; // inverter centreline Y
        private const float Oe245DepthCells = 1.0f;        // apex -> flat (right)
        private const float Oe245HeightCells = 1.1f;       // flat edge length

        /// <summary>
        /// Vertical enable bus for the 74x245: from the horizontal inverter's
        /// apex (on the centre line) straight down the diamond column to the
        /// last channel's mid-row. Call BEFORE the diamond array.
        /// </summary>
        public static void Oe245EnableBus(ChipDecoration d, float columnCells, float busBottomYCells)
        {
            float p = d.GridPitch;
            float cx = columnCells * p;
            float top = Oe245GateCenterYCells * p;
            float busBottom = busBottomYCells * p;

            using var tracePen = new Pen(GlyphColor, 0.45f) { LineJoin = LineJoin.Miter };
            d.G.DrawLine(tracePen, cx, top, cx, busBottom);
        }

        /// <summary>
        /// The 74x245 enable gate: a horizontal LEFT-pointing inverting-input
        /// buffer (bubble on the right/flat edge), fed by /OE from the right.
        /// Its apex sits on the centre line and feeds the enable bus downward.
        /// Call AFTER the diamond array so it sits in front.
        /// </summary>
        public static void Oe245EnableGate(ChipDecoration d, int oePin, float columnCells)
        {
            var g = d.G;
            float p = d.GridPitch;
            var oe = d.PinAt(oePin);

            using var fill = new SolidBrush(d.Ctx.FillColor);
            using var outline = new Pen(GlyphColor, 0.45f) { LineJoin = LineJoin.Miter };
            using var tracePen = new Pen(GlyphColor, 0.45f) { LineJoin = LineJoin.Miter };

            float cx = columnCells * p;
            float gateY = Oe245GateCenterYCells * p;
            float depth = Oe245DepthCells * p;
            float h = Oe245HeightCells * p;
            float bubbleD = OeBubbleCells * p;

            float apexX = cx;            // output, on the centre line (feeds bus)
            float flatX = cx + depth;    // input edge, to the right

            // Left-pointing triangle.
            using var tri = new GraphicsPath();
            tri.AddPolygon(new[]
            {
                new PointF(flatX, gateY - h / 2f),
                new PointF(flatX, gateY + h / 2f),
                new PointF(apexX, gateY),
            });
            g.FillPath(fill, tri);
            g.DrawPath(outline, tri);

            // Inverting-input bubble on the right (flat) side.
            var bub = new RectangleF(flatX, gateY - bubbleD / 2f, bubbleD, bubbleD);
            g.FillEllipse(fill, bub);
            g.DrawEllipse(outline, bub);

            // /OE pin -> across to the bubble's outer edge -> into the bubble.
            float oeWallX = oe.X < 0 ? d.Body.Left : d.Body.Right;
            float bubbleOuterX = flatX + bubbleD;
            g.DrawLines(tracePen, new[]
            {
                new PointF(oeWallX, oe.Y),
                new PointF(bubbleOuterX, oe.Y),
                new PointF(bubbleOuterX, gateY),
            });
        }

        // ------------------------------------------------------------------
        // Core gate drawer. All gate kinds funnel through here -- only the
        // glyph painter and the inverting flag differ. Works entirely in
        // LOCAL coordinates.
        //
        // The body is positioned so the bubble-tip Y (where an inverting
        // gate's bubble would end) lands on the output pin's row. The output
        // trace runs from (outTipX, outTipY) to the pin -- which is the same
        // location for both gate kinds. The difference is what fills the gap
        // between the body's apex and outTipY:
        //   - inverting: the bubble occupies that gap (drawn by the painter),
        //   - non-inverting: a short straight stem is drawn explicitly.
        // We skip the stem on inverting gates so it doesn't paint over the
        // bubble's outlined interior.
        //
        // Y orientation: ySign = +1 means the body sits BELOW the output pin
        // (bubble pointing DOWN toward it, inputs feeding from ABOVE) -- the
        // default for chips like the 7400/08/32/86 where output pins (3, 6,
        // 8, 11) sit below the input pin pairs. ySign = -1 means the body
        // sits ABOVE the output pin (bubble pointing UP, inputs feeding from
        // BELOW) -- used by the 7402 whose output pins (1, 4, 10, 13) sit
        // above the input pairs.
        // ------------------------------------------------------------------

        private delegate void GlyphPainter(Graphics g, Pen outline, Brush fill, RectangleF body, bool xFlipped);

        private static void DrawGateInternal(
            ChipDecoration d, int[] inputPins, int outputPin,
            bool inverting, GlyphPainter drawBody)
        {
            var g = d.G;
            float p = d.GridPitch;
            var pOut = d.PinAt(outputPin);

            var ins = new PointF[inputPins.Length];
            for (int i = 0; i < inputPins.Length; i++) ins[i] = d.PinAt(inputPins[i]);

            // Cluster side: majority of input pins. The body lives on that
            // side; any cross-side input routes a longer horizontal lead.
            // In LOCAL coords the body centre column is x=0, so "left side"
            // means pin.X < 0.
            int leftCount = 0;
            foreach (var pt in ins) if (pt.X < 0) leftCount++;
            bool onLeft = leftCount * 2 >= ins.Length;

            // Y orientation: if the output pin sits ABOVE the average input
            // row (e.g. 7402 -- inputs at pins 2,3, output at pin 1, which
            // is higher up the chip), the body must be Y-flipped so the
            // bubble points UP toward the output and the inputs feed in
            // from below. ySign carries this orientation through every
            // downstream Y offset:
            //   +1 = bubble DOWN (default; 7400/08/32/86 etc.)
            //   -1 = bubble UP   (7402 with its output-first pinout)
            float avgInY = 0;
            foreach (var pt in ins) avgInY += pt.Y;
            avgInY /= System.Math.Max(1, ins.Length);
            bool yFlip = pOut.Y < avgInY;
            float ySign = yFlip ? -1f : +1f;

            using var fill = new SolidBrush(d.Ctx.FillColor);
            using var outline = new Pen(GlyphColor, 0.45f) { LineJoin = LineJoin.Miter };
            using var tracePen = new Pen(GlyphColor, 0.45f) { LineJoin = LineJoin.Miter };

            // For right-cluster gates draw in a horizontally flipped frame
            // about the body's vertical centre line (x=0 in local coords), so
            // the same left-cluster code path serves both sides. Pin x-values
            // are mapped through CanonX before use.
            GraphicsState? state = null;
            float CanonX(float worldX) => onLeft ? worldX : -worldX;
            if (!onLeft)
            {
                state = g.Save();
                g.ScaleTransform(-1f, 1f);   // mirror about x=0 (the body centreline)
            }

            // Inner edge of the cluster's pin stubs (where the chip's own
            // black pin stubs terminate at the body wall). In the canonical
            // (possibly flipped) frame this is d.Body.Left for both sides --
            // because for right-cluster we've already flipped, so the right
            // edge has been mapped to the left side of the local frame.
            float pinStubX = d.Body.Left;

            // --- Body geometry --------------------------------------------
            // GateGlyphs draws horizontally (flat left edge, cap on right).
            // We rotate +/-90 about the body centre so the flat edge ends up
            // on the INPUT side and the cap/bubble points toward the OUTPUT.
            // Square rect (bodyW == bodyH) makes the AND-path arc fill the
            // body entirely -- no flat-edge extension on the input side.
            float bodyH = p * 1.25f;     // pre-rotation rect.Height -> input-edge length after rotation
            float bodyW = bodyH;         // pre-rotation rect.Width  -> body depth after rotation
            float bubble = GateGlyphs.BubbleDiameter(bodyH);

            // Horizontal placement: fixed inset from the pin stubs, EXCEPT
            // for gates whose inputs straddle both sides of the chip (7410
            // gate 1, 7430) -- those sit on the chip's vertical centreline
            // so leads can reach in from both sides symmetrically.
            bool straddles = false;
            {
                int lc = 0, rc = 0;
                foreach (var pt in ins) { if (pt.X < 0) lc++; else if (pt.X > 0) rc++; }
                straddles = lc > 0 && rc > 0;
            }
            float bodyCx = straddles ? 0f : pinStubX + p * 1.6f + bodyW / 2f;
            float bodyLeft = bodyCx - bodyW / 2f;
            float bodyRight = bodyCx + bodyW / 2f;

            // Vertical: anchor so the bubble-tip Y (the OUTPUT-facing tip of
            // an inverting gate) lands on the output pin's row. ySign controls
            // which side of the output pin the body sits on:
            //   ySign=+1 -> body below the pin, bubble points DOWN to it
            //   ySign=-1 -> body above the pin, bubble points UP to it
            float outToCentre = bodyW / 2f + bubble;
            float bodyCy = pOut.Y - ySign * outToCentre;
            float bodyTop = bodyCy - bodyH / 2f;
            float bodyBot = bodyCy + bodyH / 2f;

            // Post-rotation INPUT-edge Y (the flat edge where input leads
            // enter) and bubble-tip Y where the output lead starts.
            // For ySign=+1 the input edge is ABOVE bodyCy (-bodyW/2), output
            // tip is BELOW (+bodyW/2 + bubble). For ySign=-1 both flip.
            float topEdgeY = bodyCy - ySign * bodyW / 2f;
            float outTipY = bodyCy + ySign * (bodyW / 2f + bubble);
            float outTipX = bodyCx;

            // --- Draw the body, rotated about its centre ------------------
            // +90 puts the flat edge UP and the cap/bubble pointing DOWN
            // (default). -90 puts the flat edge DOWN and the cap/bubble
            // pointing UP (yFlip).
            var bodyState = g.Save();
            g.TranslateTransform(bodyCx, bodyCy);
            g.RotateTransform(yFlip ? -90f : 90f);
            g.TranslateTransform(-bodyCx, -bodyCy);
            var horizBody = RectangleF.FromLTRB(bodyLeft, bodyTop, bodyRight, bodyBot);
            drawBody(g, outline, fill, horizBody, !onLeft);
            g.Restore(bodyState);

            // --- Input leads ----------------------------------------------
            // Each input runs one clean L: vertical from the input edge to
            // the pin's row, then a short horizontal jog out to the stub edge.
            //
            // Sort key: distance from the BODY along Y. For ySign=+1 the body
            // sits below the inputs so closest-to-body = largest Y (descending).
            // For ySign=-1 (yFlip, 7402) the body sits above the inputs so
            // closest-to-body = smallest Y (ascending). The Comparison below
            // returns the same ordering as the previous descending-Y sort
            // when ySign=+1 and reverses it when ySign=-1.
            int ByDistanceFromBody(int a, int b) =>
                yFlip ? ins[a].Y.CompareTo(ins[b].Y) : ins[b].Y.CompareTo(ins[a].Y);

            var order = new int[ins.Length];
            for (int i = 0; i < ins.Length; i++) order[i] = i;
            if (straddles)
            {
                var left = new System.Collections.Generic.List<int>();
                var right = new System.Collections.Generic.List<int>();
                for (int i = 0; i < ins.Length; i++)
                    (ins[i].X < 0 ? left : right).Add(i);
                left.Sort(ByDistanceFromBody);
                right.Sort(ByDistanceFromBody);
                int k = 0;
                foreach (int i in left) order[k++] = i;
                foreach (int i in right) order[k++] = i;
            }
            else
            {
                System.Array.Sort(order, ByDistanceFromBody);
            }

            // Column positions across the body's input edge. For N>=2 inputs
            // we spread them edge-to-edge across topSpan; for a single input
            // (inverter) we centre it on bodyCx so the lead drops straight
            // into the apex from the pin row.
            float topSpan = bodyH * 0.72f;
            float firstCol = ins.Length > 1 ? bodyCx - topSpan / 2f : bodyCx;
            float colStep = ins.Length > 1 ? topSpan / (ins.Length - 1) : 0f;

            for (int k = 0; k < order.Length; k++)
            {
                var pin = ins[order[k]];
                float colX = firstCol + colStep * k;

                // Which stub edge does this pin terminate at? Same-cluster
                // pins go to the cluster's stub edge (body.Left in canonical
                // frame); cross-side pins (e.g. 7410 gate 1's pin 13 on the
                // right while the cluster is left) reach across to the
                // opposite stub (body.Right). Under a right-cluster flip both
                // edges map to their screen mirrors automatically.
                bool pinSameSide = onLeft ? (pin.X < 0) : (pin.X > 0);
                float jogEndX = pinSameSide ? d.Body.Left : d.Body.Right;

                g.DrawLines(tracePen, new[] {
                    new PointF(colX, topEdgeY),
                    new PointF(colX, pin.Y),
                    new PointF(jogEndX, pin.Y),
                });
            }

            // --- Output lead ----------------------------------------------
            // For non-inverting gates (AND/OR/XOR/buffer), draw a short
            // vertical stem from the body's APEX to outTipY. This is the
            // wire that, on an inverting gate, would be occupied by the
            // output bubble. Skip it for inverting gates so the stem doesn't
            // draw through the bubble's interior. Apex is on the OPPOSITE
            // side from the input edge: below for ySign=+1, above for -1.
            if (!inverting)
            {
                float apexY = bodyCy + ySign * bodyW / 2f;
                g.DrawLine(tracePen, outTipX, apexY, outTipX, outTipY);
            }

            float outStubX = CanonX(pOut.X);
            if (Math.Abs(outTipX - outStubX) < 0.5f)
            {
                g.DrawLine(tracePen, outTipX, outTipY, outStubX, pOut.Y);
            }
            else
            {
                g.DrawLines(tracePen, new[] {
                    new PointF(outTipX, outTipY),
                    new PointF(outTipX, pOut.Y),
                    new PointF(outStubX, pOut.Y),
                });
            }

            if (state is not null) g.Restore(state);
        }
    }
}