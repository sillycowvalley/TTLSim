// ============================================================================
//  Blinky-M Virtual Front Panel  -  Windows Forms host
//
//  Reads the firmware's plain 8-hex lines (one per capture) from the serial
//  port and renders a white-on-black instrument panel: 16-bit address bus,
//  8-bit data bus, and eight control lines. Shows a running cycle count, and
//  offers Clear, Save (raw TSV log) and Disasm (annotated disassembly steps,
//  folded out of the same log by Disassembler.cs). Both remember the last
//  folder across runs.
//
//  Frame bit order (LSB first), matching the firmware and hookup diagram:
//     bits  0-15 : A0..A15
//     bits 16-23 : D0..D7
//     bits 24-31 : CLKG /RESET /FETCH T0 T1 T2 HALT N
//  Levels are raw (1 = high). Active-low display inversion is applied here.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows.Forms;

namespace BlinkyMFrontPanel
{
    // ------------------------------------------------------------------ signal
    internal enum Group { Address, Data, Control }

    // One capture: the raw 32-bit word plus the PC-side arrival time (free-running
    // microseconds from app start). The log's saved timestamp is this minus the
    // first row's value, so it reads 0 at the start of each capture (Clear resets it).
    internal struct Sample
    {
        public uint Word;
        public long RawMicros;
        public Sample(uint word, long rawMicros) { Word = word; RawMicros = rawMicros; }
    }

    internal sealed class Signal
    {
        public string Name;
        public int Bit;
        public Color Color;
        public bool ActiveLow;

        public Signal(string name, int bit, Color color, bool activeLow)
        {
            Name = name;
            Bit = bit;
            Color = color;
            ActiveLow = activeLow;
        }
    }

    // -------------------------------------------------------------- LED panel
    internal sealed class LedPanel : Control
    {
        // Design-space dimensions; everything is drawn in these units and then
        // scaled uniformly to the control size.
        private const float BaseW = 780f;
        private const float BaseH = 440f;

        private static readonly Color FaceColor = ColorTranslator.FromHtml("#2b2f33");
        private static readonly Color EdgeColor = ColorTranslator.FromHtml("#111111");
        private static readonly Color OffColor = ColorTranslator.FromHtml("#14171a");
        private static readonly Color BezelColor = ColorTranslator.FromHtml("#555555");
        private static readonly Color LabelColor = ColorTranslator.FromHtml("#b7bdc2");
        private static readonly Color DimColor = ColorTranslator.FromHtml("#8a9096");
        private static readonly Color BrightColor = ColorTranslator.FromHtml("#eaeaea");
        private static readonly Color DividerColor = ColorTranslator.FromHtml("#3a3f44");

        private static readonly Color AddrColor = ColorTranslator.FromHtml("#2db83f");
        private static readonly Color DataColor = ColorTranslator.FromHtml("#3a97f0");
        private static readonly Color BlueColor = ColorTranslator.FromHtml("#3a97f0");
        private static readonly Color RedColor = ColorTranslator.FromHtml("#ee3524");
        private static readonly Color YlwColor = ColorTranslator.FromHtml("#f2c400");

        private readonly Signal[] control;
        private readonly float[] addrX = new float[16];

        private readonly Font titleFont;
        private readonly Font subFont;
        private readonly Font groupFont;
        private readonly Font bitFont;
        private readonly Font ctrlFont;
        private readonly Font hexFont;
        private readonly Font cycleFont;
        private readonly Font cycleLabelFont;
        private readonly Font statusFont;

        private readonly StringFormat sfCenter;
        private readonly StringFormat sfRight;
        private readonly StringFormat sfLeft;

        private uint word;
        private int cycleCount;
        private bool connected;
        private bool logFull;
        private string portInfo = "OFFLINE";

        public uint Word
        {
            get { return word; }
            set { word = value; Invalidate(); }
        }

        public int CycleCount
        {
            get { return cycleCount; }
            set { cycleCount = value; Invalidate(); }
        }

        public bool Connected
        {
            get { return connected; }
            set { connected = value; Invalidate(); }
        }

        public bool LogFull
        {
            get { return logFull; }
            set { if (logFull != value) { logFull = value; Invalidate(); } }
        }

        public string PortInfo
        {
            get { return portInfo; }
            set { portInfo = value; Invalidate(); }
        }

        public LedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = ColorTranslator.FromHtml("#23272b");

            control = new Signal[]
            {
                new Signal("CLKG",   24, YlwColor,  false),
                new Signal("/RESET", 25, RedColor,  true),
                new Signal("/FETCH", 26, YlwColor,  true),
                new Signal("T0",     27, BlueColor, false),
                new Signal("T1",     28, BlueColor, false),
                new Signal("T2",     29, BlueColor, false),
                new Signal("HALT",   30, RedColor,  false),
                new Signal("N",      31, YlwColor,  false),
            };

            // Precompute the 16 address LED centres (nibble-grouped).
            for (int i = 0; i < 16; i++)
            {
                int nibble = i / 4;
                addrX[i] = 74f + i * 36f + nibble * 14f;
            }

            titleFont = new Font("Arial", 22f, FontStyle.Bold);
            subFont = new Font("Arial", 8f, FontStyle.Regular);
            groupFont = new Font("Arial", 11f, FontStyle.Bold);
            bitFont = new Font("Arial", 8f, FontStyle.Regular);
            ctrlFont = new Font("Arial", 9f, FontStyle.Bold);
            hexFont = new Font("Consolas", 19f, FontStyle.Bold);
            cycleFont = new Font("Consolas", 26f, FontStyle.Bold);
            cycleLabelFont = new Font("Arial", 10f, FontStyle.Bold);
            statusFont = new Font("Consolas", 10f, FontStyle.Bold);

            sfCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            sfRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            sfLeft = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);

            float scale = Math.Min(Width / BaseW, Height / BaseH);
            float offX = (Width - BaseW * scale) / 2f;
            float offY = (Height - BaseH * scale) / 2f;
            g.TranslateTransform(offX, offY);
            g.ScaleTransform(scale, scale);

            // Panel face.
            using (GraphicsPath face = Rounded(6f, 6f, BaseW - 12f, BaseH - 12f, 8f))
            using (SolidBrush faceBrush = new SolidBrush(FaceColor))
            using (Pen edgePen = new Pen(EdgeColor, 1f))
            {
                g.FillPath(faceBrush, face);
                g.DrawPath(edgePen, face);
            }
            DrawScrews(g);

            // Header.
            DrawString(g, "BLINKY-M", titleFont, BrightColor, 390f, 40f, sfCenter);
            DrawString(g, "MICROCODED TTL CPU  \u00B7  VIRTUAL FRONT PANEL", subFont, DimColor, 390f, 60f, sfCenter);

            // Link indicator (top-right).
            DrawLed(g, 726f, 40f, 6f, AddrColor, connected);
            DrawString(g, "LINK", bitFont, connected ? BrightColor : DimColor, 726f, 55f, sfCenter);

            using (Pen div = new Pen(DividerColor, 1f))
            {
                g.DrawLine(div, 40f, 76f, 740f, 76f);
                g.DrawLine(div, 40f, 236f, 740f, 236f);
                g.DrawLine(div, 40f, 344f, 740f, 344f);
            }

            // ADDRESS row.
            DrawString(g, "ADDRESS", groupFont, LabelColor, 40f, 100f, sfLeft);
            for (int i = 0; i < 16; i++)
            {
                int bit = 15 - i;                 // display MSB-first
                bool on = ((word >> bit) & 1u) != 0;
                float cx = addrX[i];
                DrawString(g, bit.ToString(), bitFont, DimColor, cx, 102f, sfCenter);
                DrawLed(g, cx, 128f, 9f, AddrColor, on);
            }
            DrawString(g, "$" + ((word & 0xFFFFu)).ToString("X4"), hexFont, AddrColor, 744f, 128f, sfRight);

            // DATA row (aligned under the low address byte, A7..A0 columns).
            DrawString(g, "DATA", groupFont, LabelColor, 40f, 190f, sfLeft);
            for (int i = 0; i < 8; i++)
            {
                int bit = 23 - i;                 // D7..D0, MSB-first
                bool on = ((word >> bit) & 1u) != 0;
                float cx = addrX[8 + i];          // sit beneath A7..A0
                DrawString(g, (7 - i).ToString(), bitFont, DimColor, cx, 190f, sfCenter);
                DrawLed(g, cx, 214f, 9f, DataColor, on);
            }
            DrawString(g, "$" + (((word >> 16) & 0xFFu)).ToString("X2"), hexFont, DataColor, 744f, 214f, sfRight);

            // CONTROL row.
            DrawString(g, "CONTROL", groupFont, LabelColor, 40f, 264f, sfLeft);
            for (int i = 0; i < control.Length; i++)
            {
                Signal s = control[i];
                bool level = ((word >> s.Bit) & 1u) != 0;
                bool lit = s.ActiveLow ? !level : level;
                float cx = 82f + i * 93f;
                DrawLed(g, cx, 300f, 11f, s.Color, lit);
                DrawString(g, s.Name, ctrlFont, lit ? BrightColor : DimColor, cx, 326f, sfCenter);
            }

            // Footer: cycle count + link status.
            DrawString(g, "CYCLES", cycleLabelFont, LabelColor, 40f, 370f, sfLeft);
            DrawString(g, cycleCount.ToString(), cycleFont, BrightColor, 40f, 398f, sfLeft);
            if (logFull)
                DrawString(g, "LOG FULL", cycleLabelFont, DataColor, 190f, 400f, sfLeft);

            Color statusColor = connected ? AddrColor : DataColor;
            DrawString(g, (connected ? "\u25CF  " : "\u25CB  ") + portInfo, statusFont, statusColor, 744f, 400f, sfRight);
        }

        private void DrawScrews(Graphics g)
        {
            float[] xs = { 16f, 752f };
            float[] ys = { 16f, 412f };
            using (SolidBrush b = new SolidBrush(EdgeColor))
            using (Pen p = new Pen(ColorTranslator.FromHtml("#666666"), 0.6f))
            {
                foreach (float x in xs)
                    foreach (float y in ys)
                    {
                        g.FillEllipse(b, x - 3f, y - 3f, 6f, 6f);
                        g.DrawEllipse(p, x - 3f, y - 3f, 6f, 6f);
                    }
            }
        }

        private void DrawLed(Graphics g, float cx, float cy, float r, Color color, bool on)
        {
            if (on)
            {
                float gr = r * 2.3f;
                using (SolidBrush glow = new SolidBrush(Color.FromArgb(60, color)))
                    g.FillEllipse(glow, cx - gr, cy - gr, gr * 2f, gr * 2f);
            }

            using (Pen bezel = new Pen(BezelColor, 1.1f))
                g.DrawEllipse(bezel, cx - (r + 2.2f), cy - (r + 2.2f), (r + 2.2f) * 2f, (r + 2.2f) * 2f);

            using (SolidBrush fill = new SolidBrush(on ? color : OffColor))
                g.FillEllipse(fill, cx - r, cy - r, r * 2f, r * 2f);

            using (Pen inner = new Pen(EdgeColor, 1f))
                g.DrawEllipse(inner, cx - r, cy - r, r * 2f, r * 2f);

            if (on)
            {
                float hr = r * 0.45f;
                float hx = cx - r * 0.35f;
                float hy = cy - r * 0.35f;
                using (SolidBrush hi = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                    g.FillEllipse(hi, hx - hr, hy - hr, hr * 2f, hr * 2f);
            }
        }

        private static void DrawString(Graphics g, string text, Font font, Color color, float x, float y, StringFormat sf)
        {
            using (SolidBrush b = new SolidBrush(color))
                g.DrawString(text, font, b, x, y, sf);
        }

        private static GraphicsPath Rounded(float x, float y, float w, float h, float radius)
        {
            float d = radius * 2f;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(x, y, d, d, 180f, 90f);
            path.AddArc(x + w - d, y, d, d, 270f, 90f);
            path.AddArc(x + w - d, y + h - d, d, d, 0f, 90f);
            path.AddArc(x, y + h - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                titleFont.Dispose(); subFont.Dispose(); groupFont.Dispose();
                bitFont.Dispose(); ctrlFont.Dispose(); hexFont.Dispose();
                cycleFont.Dispose(); cycleLabelFont.Dispose(); statusFont.Dispose();
                sfCenter.Dispose(); sfRight.Dispose(); sfLeft.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // -------------------------------------------------------------- main form
    internal sealed class MainForm : Form
    {
        private const int Baud = 115200;
        private const int MaxLogTicks = 10000;   // log only the first N captures; display stays live

        private readonly LedPanel ledPanel;
        private readonly Panel buttonStrip;
        private readonly ComboBox portCombo;
        private readonly Button connectButton;
        private readonly Button clearButton;
        private readonly Button saveButton;
        private readonly Button disasmButton;
        private readonly CheckBox verboseCheck;
        private readonly Timer uiTimer;          // ~30 Hz display refresh, decoupled from serial rate

        private readonly List<Sample> logSamples = new List<Sample>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly object stateLock = new object();

        private uint latestWord;                 // most recent frame (serial thread writes, timer reads)
        private int latestCount;
        private bool stateDirty;
        private readonly StringBuilder rxBuffer = new StringBuilder();

        private SerialPort serialPort;
        private string settingsPath;
        private string saveDir;

        private static readonly string[] ControlNames =
            { "CLKG", "/RESET", "/FETCH", "T0", "T1", "T2", "HALT", "N" };

        public MainForm()
        {
            Text = "Blinky-M Virtual Front Panel";
            BackColor = ColorTranslator.FromHtml("#23272b");
            ClientSize = new Size(840, 540);
            MinimumSize = new Size(640, 460);
            StartPosition = FormStartPosition.CenterScreen;

            ledPanel = new LedPanel { Dock = DockStyle.Fill };

            buttonStrip = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                BackColor = ColorTranslator.FromHtml("#1c2023")
            };

            portCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 130,
                Left = 10,
                Top = 12,
                FlatStyle = FlatStyle.Flat,
                BackColor = ColorTranslator.FromHtml("#2b2f33"),
                ForeColor = ColorTranslator.FromHtml("#eaeaea")
            };
            portCombo.DropDown += delegate { RefreshPorts(); };

            connectButton = MakeButton("Connect", 150);
            connectButton.Click += ConnectClick;

            saveButton = MakeButton("Save", 0);
            saveButton.Click += SaveClick;

            clearButton = MakeButton("Clear", 0);
            clearButton.Click += ClearClick;

            disasmButton = MakeButton("Disasm", 0);
            disasmButton.Width = 100;
            disasmButton.Click += DisasmClick;

            verboseCheck = new CheckBox
            {
                Text = "T-states",
                Checked = true,
                AutoSize = true,
                Top = 19,
                Left = 0,
                ForeColor = ColorTranslator.FromHtml("#b7bdc2"),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Arial", 9f, FontStyle.Regular)
            };

            buttonStrip.Controls.Add(portCombo);
            buttonStrip.Controls.Add(connectButton);
            buttonStrip.Controls.Add(verboseCheck);
            buttonStrip.Controls.Add(disasmButton);
            buttonStrip.Controls.Add(clearButton);
            buttonStrip.Controls.Add(saveButton);
            buttonStrip.Resize += delegate { PositionRightButtons(); };

            Controls.Add(ledPanel);
            Controls.Add(buttonStrip);
            PositionRightButtons();

            uiTimer = new Timer { Interval = 33 };      // ~30 Hz
            uiTimer.Tick += UiTimerTick;
            uiTimer.Start();

            LoadSettings();
            RefreshPorts();
            UpdateStatus();

            FormClosing += delegate { ClosePort(); SaveSettings(); };
        }

        private Button MakeButton(string text, int left)
        {
            Button b = new Button
            {
                Text = text,
                Width = 92,
                Height = 32,
                Top = 12,
                Left = left,
                FlatStyle = FlatStyle.Flat,
                BackColor = ColorTranslator.FromHtml("#2b2f33"),
                ForeColor = ColorTranslator.FromHtml("#eaeaea"),
                Font = new Font("Arial", 9f, FontStyle.Bold)
            };
            b.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#4a5157");
            b.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#353b40");
            return b;
        }

        private void PositionRightButtons()
        {
            if (buttonStrip == null || saveButton == null || clearButton == null) return;
            if (disasmButton == null || verboseCheck == null) return;
            saveButton.Left = buttonStrip.ClientSize.Width - saveButton.Width - 12;
            clearButton.Left = saveButton.Left - clearButton.Width - 8;
            disasmButton.Left = clearButton.Left - disasmButton.Width - 8;
            verboseCheck.Left = disasmButton.Left - verboseCheck.Width - 8;
        }

        private void RefreshPorts()
        {
            string current = portCombo.SelectedItem as string;
            string[] names = SerialPort.GetPortNames();
            Array.Sort(names);
            portCombo.Items.Clear();
            portCombo.Items.AddRange(names);
            if (current != null && portCombo.Items.Contains(current))
                portCombo.SelectedItem = current;
            else if (portCombo.Items.Count > 0 && portCombo.SelectedIndex < 0)
                portCombo.SelectedIndex = 0;
        }

        private bool IsConnected
        {
            get { return serialPort != null && serialPort.IsOpen; }
        }

        private void ConnectClick(object sender, EventArgs e)
        {
            if (IsConnected)
            {
                ClosePort();
                UpdateStatus();
                return;
            }

            string portName = portCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(portName))
            {
                MessageBox.Show(this, "Select a COM port first.", "No port",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                serialPort = new SerialPort(portName, Baud, Parity.None, 8, StopBits.One);
                serialPort.NewLine = "\n";
                serialPort.ReadTimeout = 500;
                serialPort.DataReceived += SerialDataReceived;
                serialPort.Open();
                rxBuffer.Clear();
            }
            catch (Exception ex)
            {
                serialPort = null;
                MessageBox.Show(this, "Could not open " + portName + ":\n" + ex.Message,
                    "Serial error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            UpdateStatus();
        }

        private void ClosePort()
        {
            if (serialPort == null) return;
            try
            {
                serialPort.DataReceived -= SerialDataReceived;
                if (serialPort.IsOpen) serialPort.Close();
            }
            catch { /* ignore teardown races */ }
            serialPort.Dispose();
            serialPort = null;
        }

        private void SerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            string chunk;
            try { chunk = serialPort.ReadExisting(); }
            catch { return; }

            List<Sample> frames = null;
            lock (rxBuffer)
            {
                rxBuffer.Append(chunk);
                int nl;
                while ((nl = IndexOfNewline(rxBuffer)) >= 0)
                {
                    string line = rxBuffer.ToString(0, nl).Trim();
                    rxBuffer.Remove(0, nl + 1);
                    uint value;
                    if (TryParseFrame(line, out value))
                    {
                        if (frames == null) frames = new List<Sample>();
                        frames.Add(new Sample(value, CurrentMicros()));
                    }
                }
            }

            if (frames == null) return;
            lock (stateLock)
            {
                foreach (Sample s in frames)
                {
                    if (logSamples.Count < MaxLogTicks)
                        logSamples.Add(s);
                    latestWord = s.Word;
                }
                latestCount = logSamples.Count;
                stateDirty = true;
            }
        }

        private long CurrentMicros()
        {
            return clock.ElapsedTicks * 1000000L / Stopwatch.Frequency;
        }

        private static int IndexOfNewline(StringBuilder sb)
        {
            for (int i = 0; i < sb.Length; i++)
                if (sb[i] == '\n') return i;
            return -1;
        }

        private static bool TryParseFrame(string line, out uint value)
        {
            value = 0;
            if (line.Length != 8) return false;
            for (int i = 0; i < 8; i++)
            {
                char c = line[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
                if (!hex) return false;
            }
            return uint.TryParse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        private void UiTimerTick(object sender, EventArgs e)
        {
            uint word; int count;
            lock (stateLock)
            {
                if (!stateDirty) return;
                word = latestWord;
                count = latestCount;
                stateDirty = false;
            }
            ledPanel.Word = word;                       // property setters invalidate;
            ledPanel.CycleCount = count;                // at most once per timer tick
            ledPanel.LogFull = count >= MaxLogTicks;
        }

        private void ClearClick(object sender, EventArgs e)
        {
            lock (stateLock)
            {
                logSamples.Clear();
                latestCount = 0;
                stateDirty = true;
            }
            // LEDs keep showing the last captured state. The next sample becomes
            // the new t=0, and a fresh 10,000-tick log window opens.
        }

        private void SaveClick(object sender, EventArgs e)
        {
            Sample[] snapshot;
            lock (stateLock)
            {
                snapshot = logSamples.ToArray();
            }

            if (snapshot.Length == 0)
            {
                MessageBox.Show(this, "The log is empty.", "Nothing to save",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "Tab-separated log (*.tsv)|*.tsv|Text file (*.txt)|*.txt|All files (*.*)|*.*";
                dlg.DefaultExt = "tsv";
                dlg.FileName = "blinkym_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".tsv";
                dlg.RestoreDirectory = false;
                if (!string.IsNullOrEmpty(saveDir) && Directory.Exists(saveDir))
                    dlg.InitialDirectory = saveDir;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    WriteLog(dlg.FileName, snapshot);
                    saveDir = Path.GetDirectoryName(dlg.FileName);
                    SaveSettings();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not save:\n" + ex.Message, "Save error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void DisasmClick(object sender, EventArgs e)
        {
            Sample[] snapshot;
            lock (stateLock)
            {
                snapshot = logSamples.ToArray();
            }

            if (snapshot.Length == 0)
            {
                MessageBox.Show(this, "The log is empty.", "Nothing to disassemble",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "Disassembly (*.dis)|*.dis|Text file (*.txt)|*.txt|All files (*.*)|*.*";
                dlg.DefaultExt = "dis";
                dlg.FileName = "blinkym_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".dis";
                dlg.RestoreDirectory = false;
                if (!string.IsNullOrEmpty(saveDir) && Directory.Exists(saveDir))
                    dlg.InitialDirectory = saveDir;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    File.WriteAllText(dlg.FileName,
                        Disassembler.Build(snapshot, verboseCheck.Checked), Encoding.UTF8);
                    saveDir = Path.GetDirectoryName(dlg.FileName);
                    SaveSettings();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not save:\n" + ex.Message, "Save error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void WriteLog(string path, Sample[] samples)
        {
            using (StreamWriter w = new StreamWriter(path, false, Encoding.UTF8))
            {
                w.WriteLine("# Blinky-M Virtual Front Panel capture log");
                w.WriteLine("# Saved: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                w.WriteLine("# Micros  = PC-side elapsed microseconds since the first row (Clear resets to 0).");
                w.WriteLine("# dMicros = elapsed since the previous row.");
                w.WriteLine("#   These are host arrival times (include USB/OS jitter), not edge-exact device timing.");
                w.WriteLine("# Word is a 32-bit hex value, bit 0 = A0.");
                w.WriteLine("#   bits 0-15  = A0..A15  (address) -> Addr column, 16-bit hex");
                w.WriteLine("#   bits 16-23 = D0..D7   (data)    -> Data column, 8-bit hex");
                w.WriteLine("#   bits 24-31 = CLKG /RESET /FETCH T0 T1 T2 HALT N  (raw pin levels, 1 = high)");
                w.WriteLine("# Active-low signals: /RESET, /FETCH (asserted when 0).");
                w.WriteLine("# Tab-separated. Lines starting with # are comments.");
                w.WriteLine("Cycle\tMicros\tdMicros\tWord\tAddr\tData\t" + string.Join("\t", ControlNames));

                long origin = samples[0].RawMicros;
                long prev = origin;
                for (int i = 0; i < samples.Length; i++)
                {
                    Sample s = samples[i];
                    uint word = s.Word;
                    long micros = s.RawMicros - origin;
                    long dmicros = s.RawMicros - prev;
                    prev = s.RawMicros;
                    string[] fields = new string[6 + 8];
                    fields[0] = i.ToString();
                    fields[1] = micros.ToString();
                    fields[2] = dmicros.ToString();
                    fields[3] = word.ToString("X8");
                    fields[4] = (word & 0xFFFFu).ToString("X4");
                    fields[5] = ((word >> 16) & 0xFFu).ToString("X2");
                    for (int b = 0; b < 8; b++)
                        fields[6 + b] = (((word >> (24 + b)) & 1u) != 0) ? "1" : "0";
                    w.WriteLine(string.Join("\t", fields));
                }
            }
        }

        private void UpdateStatus()
        {
            bool up = IsConnected;
            connectButton.Text = up ? "Disconnect" : "Connect";
            portCombo.Enabled = !up;
            ledPanel.Connected = up;
            ledPanel.PortInfo = up
                ? (serialPort.PortName + " \u00B7 " + Baud)
                : "OFFLINE";
        }

        // ------------------------------------------------------- settings
        private void LoadSettings()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlinkyMFrontPanel");
            settingsPath = Path.Combine(dir, "settings.txt");

            try
            {
                if (File.Exists(settingsPath))
                {
                    foreach (string raw in File.ReadAllLines(settingsPath))
                    {
                        int eq = raw.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = raw.Substring(0, eq).Trim();
                        string val = raw.Substring(eq + 1).Trim();
                        if (key == "SaveDir") saveDir = val;
                        else if (key == "Port") pendingPort = val;
                    }
                }
            }
            catch { /* first run or unreadable - ignore */ }
        }

        private string pendingPort;

        private void SaveSettings()
        {
            try
            {
                string dir = Path.GetDirectoryName(settingsPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string port = IsConnected ? serialPort.PortName : (portCombo.SelectedItem as string);
                using (StreamWriter w = new StreamWriter(settingsPath, false, Encoding.UTF8))
                {
                    w.WriteLine("SaveDir=" + (saveDir ?? ""));
                    w.WriteLine("Port=" + (port ?? ""));
                }
            }
            catch { /* non-fatal */ }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            PositionRightButtons();
            if (!string.IsNullOrEmpty(pendingPort) && portCombo.Items.Contains(pendingPort))
                portCombo.SelectedItem = pendingPort;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}