using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using TTLSim.UI.Model;

namespace TTLSim.UI.Components;

/// <summary>
/// A full-can (DIP-14 footprint) crystal oscillator module. Electrically it
/// behaves exactly like <see cref="ClockSource"/> -- a single output pin that
/// emits a square wave at FrequencyHz -- but it is drawn as a 14-pin DIP
/// outline with only the four populated corner pins, matching a real canned
/// oscillator:
///
///   Pin 1  (top-left)     N/C
///   Pin 7  (bottom-left)  Ground
///   Pin 8  (bottom-right) Output     &lt;- the driven pin
///   Pin 14 (top-right)    +5 VDC
///
/// The four non-output pins are drawn for layout/realism and are wired like
/// any other part, but the simulation only drives the output -- the can needs
/// no modelled supply, just as the CLK symbol doesn't.
///
/// Geometry matches the app's DIP convention (see ChipUnit): 14 cells tall
/// (1-cell margin + 6*2-cell pitch + 1-cell margin), body inset one cell each
/// side for the pin stubs. The frequency is printed on the body like the part's
/// silkscreen.
/// </summary>
public sealed class CanOscillator : SchematicItem
{
    // DIP-14 layout constants, kept in step with ChipUnit.
    private const int PinPitch = 2;
    private const int VerticalMargin = 1;
    private const int PinsPerSide = 7;          // 14-pin package
    private const int BodyWidthCells = 8;       // matches the standard DIP body width

    // Pin numbers for the four populated corners.
    private const int OutputPin = 8;
    private const int GroundPin = 7;
    private const int PowerPin = 14;
    private const int NcPin = 1;

    /// <summary>
    /// Output frequency in hertz. Offers a dropdown of common oscillator and
    /// UART baud-rate values, but free-form entry ("1MHz", "10k", "1e6") still
    /// works -- the converter is non-exclusive.
    /// </summary>
    [Category("Signal")]
    [DisplayName("Frequency")]
    [TypeConverter(typeof(OscillatorFrequencyConverter))]
    [Description("Oscillator output frequency. Pick a standard value or type your own.")]
    public double FrequencyHz { get; set; } = 1_000_000.0;

    public CanOscillator()
    {
        int top = VerticalMargin;                               // y of slot 0  -> 1
        int bottom = VerticalMargin + (PinsPerSide - 1) * PinPitch;  // y of slot 6 -> 13
        Size = new Size(BodyWidthCells + 2, bottom + VerticalMargin); // 10 x 14

        // OUTPUT MUST be added first: the build pipeline drives PinNumbers[0],
        // and ChipFactory.CreateForItem reads that first entry as the clock net.
        AddPin(new Pin("OUT", OutputPin, new Point(Size.Width, bottom), PinDirection.Right));
        AddPin(new Pin("NC", NcPin, new Point(0, top), PinDirection.Left));
        AddPin(new Pin("GND", GroundPin, new Point(0, bottom), PinDirection.Left));
        AddPin(new Pin("+5V", PowerPin, new Point(Size.Width, top), PinDirection.Right));
    }

    public override Rectangle RoutingBounds
    {
        get
        {
            var unrotated = new Rectangle(
                Position.X - 1, Position.Y - 1,
                Size.Width + 2, Size.Height + 2);

            if (Rotation == Rotation.R0 || Rotation == Rotation.R180)
                return unrotated;

            int cx = Position.X + Size.Width / 2;
            int cy = Position.Y + Size.Height / 2;
            int w = unrotated.Height;
            int h = unrotated.Width;
            return new Rectangle(cx - w / 2, cy - h / 2, w, h);
        }
    }

    public override void Draw(Graphics g, RenderContext ctx)
    {
        var state = g.Save();
        ApplyRotationTransform(g, ctx);
        DrawShape(g, ctx);
        g.Restore(state);

        DrawFrequencyLabel(g, ctx);
    }

    private void DrawShape(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        int leftX = Position.X * p;
        int rightX = (Position.X + Size.Width) * p;
        int topY = Position.Y * p;
        int bottomY = (Position.Y + Size.Height) * p;

        int bodyLeftX = leftX + p;          // one stub cell on each side
        int bodyRightX = rightX - p;

        var color = Selected ? ctx.SelectedColor : ctx.ForegroundColor;
        using var outline = new Pen(color, 1.2f);
        using var fillBrush = new SolidBrush(ctx.FillColor);
        using var pinBrush = new SolidBrush(color);

        // Can body -- "top view" of the metal can. Three corners are rounded;
        // the top-left (pin-1 / NC) corner is squared off, which is the
        // package's pin-1 orientation marker on a real canned oscillator.
        float radius = p;                   // ~1 cell corner radius
        using (var body = SquaredPinOneRect(bodyLeftX, topY, bodyRightX - bodyLeftX, bottomY - topY, radius))
        {
            g.FillPath(fillBrush, body);
            g.DrawPath(outline, body);
        }

        // Pin stubs, dots, and names (inside).
        var prevSmoothing = g.SmoothingMode;
        using var textBrush = new SolidBrush(ctx.ForegroundColor);
        var tightFormat = System.Drawing.StringFormat.GenericTypographic;
        const float inset = 0.2f;

        foreach (var pin in Pins)
        {
            int py = (Position.Y + pin.LocalPosition.Y) * p;
            bool isLeft = pin.LocalDirection == PinDirection.Left;

            int bodyEdgeX = isLeft ? bodyLeftX : bodyRightX;
            int endX = isLeft ? leftX : rightX;

            g.SmoothingMode = SmoothingMode.None;
            g.DrawLine(outline, bodyEdgeX, py, endX, py);
            g.FillEllipse(pinBrush, endX - 2, py - 2, 4, 4);
            g.SmoothingMode = prevSmoothing;

            // Pin name just inside the body edge.
            if (!string.IsNullOrEmpty(pin.Name))
            {
                var nameSize = g.MeasureString(pin.Name, ctx.PinFont, int.MaxValue, tightFormat);
                float nameX = isLeft
                    ? bodyEdgeX + inset * p
                    : bodyEdgeX - inset * p - nameSize.Width;
                g.DrawString(pin.Name, ctx.PinFont, textBrush, nameX, py - nameSize.Height / 2, tightFormat);
            }
        }
    }

    /// <summary>
    /// Draw the formatted frequency centred on the body. Drawn screen-aligned
    /// (outside the rotation transform) so it stays readable at every rotation,
    /// matching the CLK symbol's label behaviour.
    /// </summary>
    private void DrawFrequencyLabel(Graphics g, RenderContext ctx)
    {
        int p = ctx.GridPitch;
        var b = Bounds;     // rotated visual extent
        using var brush = new SolidBrush(ctx.ForegroundColor);

        string text = ClockSource.FormatFrequency(FrequencyHz);
        var size = g.MeasureString(text, ctx.LabelFont);

        float cx = (b.X + b.Width / 2f) * p;
        float cy = (b.Y + b.Height / 2f) * p;
        g.DrawString(text, ctx.LabelFont, brush, cx - size.Width / 2f, cy - size.Height / 2f);
    }

    private void ApplyRotationTransform(Graphics g, RenderContext ctx)
    {
        if (Rotation == Rotation.R0) return;
        int p = ctx.GridPitch;
        float pivotX = Pivot.X * p;
        float pivotY = Pivot.Y * p;
        g.TranslateTransform(pivotX, pivotY);
        g.RotateTransform((float)(int)Rotation);
        g.TranslateTransform(-pivotX, -pivotY);
    }

    /// <summary>
    /// Body outline: three rounded corners, with the top-left (pin-1 / NC)
    /// corner left square. The squared corner is the package's pin-1
    /// orientation marker.
    /// </summary>
    private static GraphicsPath SquaredPinOneRect(float x, float y, float w, float h, float radius)
    {
        float r = Math.Min(radius, Math.Min(w, h) / 2f);
        float d = r * 2f;
        var path = new GraphicsPath();
        path.AddLine(x, y, x + w - r, y);                  // top edge from the square corner
        path.AddArc(x + w - d, y, d, d, 270, 90);          // top-right
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);    // bottom-right
        path.AddArc(x, y + h - d, d, d, 90, 90);           // bottom-left
        path.AddLine(x, y + h - r, x, y);                  // left edge back to the square corner
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Frequency converter with a dropdown of common oscillator and UART
    /// baud-rate frequencies. Non-exclusive: the standard values populate the
    /// dropdown, but free-form text entry still parses (reusing the CLK
    /// symbol's parser/formatter), so any value remains typeable.
    /// </summary>
    public sealed class OscillatorFrequencyConverter : TypeConverter
    {
        // Hz. 1/2/4/8/10 MHz plus the classic UART baud-rate crystals.
        private static readonly double[] Standard =
        {
            1, 1_000, // for testing only
            1_000_000, 1_843_200, 2_000_000, 3_686_400, 4_000_000,
        };

        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;

        // Non-exclusive: dropdown suggestions, but typing a custom value is allowed.
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => false;

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new(Standard);

        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
            sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

        public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
            destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

        public override object? ConvertFrom(ITypeDescriptorContext? context,
            System.Globalization.CultureInfo? culture, object value)
        {
            if (value is string s)
            {
                var parsed = ClockSource.ParseFrequency(s);
                if (parsed.HasValue) return parsed.Value;
                throw new FormatException($"'{s}' is not a valid frequency.");
            }
            return base.ConvertFrom(context, culture, value);
        }

        public override object? ConvertTo(ITypeDescriptorContext? context,
            System.Globalization.CultureInfo? culture, object? value, Type destinationType)
        {
            if (destinationType == typeof(string) && value is double hz)
                return ClockSource.FormatFrequency(hz);
            return base.ConvertTo(context, culture, value, destinationType);
        }
    }
}