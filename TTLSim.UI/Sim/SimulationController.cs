using Microsoft.Extensions.Logging;
using System;
using System.Windows.Forms;
using TTLSim.Chips;
using TTLSim.Chips.Displays;
using TTLSim.Chips.Passives;
using TTLSim.Core;
using TTLSim.UI.Components;
using TTLSim.UI.Model;
using TTLSim.UI.Model.Sim;

namespace TTLSim.UI.Sim;

public enum SimState { Edit, Built, Running, Paused }

/// <summary>
/// Owns the simulator lifecycle and the Edit/Built/Running/Paused state machine.
/// MainForm subscribes to StateChanged and Ticked to update toolbar buttons
/// and trigger canvas redraws.
/// </summary>
public sealed class SimulationController
{
    private readonly Schematic schematic;
    private readonly System.Windows.Forms.Timer timer;

    private Simulator? simulator;
    private BuildResult? buildResult;

    private DateTime lastFrameWallClock;

    // Fetched fresh per access rather than cached: Logging.Log.Reset()
    // (called on Build) disposes and replaces the logger factory, which
    // would leave a cached ILogger stale.
    private Microsoft.Extensions.Logging.ILogger log => Logging.Log.For<SimulationController>();

    public SimulationController(Schematic schematic)
    {
        this.schematic = schematic;

        timer = new System.Windows.Forms.Timer { Interval = 16 };   // ~60 Hz
        timer.Tick += OnTimerTick;
    }

    /// <summary>Multiplier on real time. 1.0 = realtime, 1000.0 = 1000x.</summary>
    public double SpeedFactor { get; set; } = 1.0;

    public SimState State { get; private set; } = SimState.Edit;

    public Simulator? Simulator => simulator;
    public BuildResult? LastBuild => buildResult;

    /// <summary>Current simulated tick in picoseconds.</summary>
    public long CurrentTick => simulator?.CurrentTick ?? 0;

    /// <summary>Raised whenever State changes (so toolbar buttons can be enabled/disabled).</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised after each timer tick once simulator has advanced. UI redraws here.</summary>
    public event EventHandler? Ticked;


    public IReadOnlyDictionary<SchematicItem, ButtonInput> ButtonBindings { get; private set; } =
    new Dictionary<SchematicItem, ButtonInput>();

    public IReadOnlyDictionary<SchematicItem, SwitchInput> SwitchBindings { get; private set; } =
    new Dictionary<SchematicItem, SwitchInput>();

    public IReadOnlyDictionary<SchematicItem, SpdtSwitchInput> SpdtBindings { get; private set; } =
    new Dictionary<SchematicItem, SpdtSwitchInput>();

    // ------------------------------------------------------------ commands

    public BuildResult Build()
    {
        var input = new SchematicBuildInput(schematic);
        var factory = new ChipFactory(log);
        buildResult = new SchematicBuilder(factory, log).Build(input);

        simulator = buildResult.Simulator;
        DisplayBindings = BuildDisplayMap();
        ButtonBindings = BuildButtonMap();
        SwitchBindings = BuildSwitchMap();
        SpdtBindings = BuildSpdtMap();

        if (simulator is not null)
        {
            simulator.Start();
            // Drain only what's at tick 0 -- the Initialize events for VCC, GND
            // and the chips' initial output schedules. Do NOT keep going; the
            // clock would otherwise run away to whatever the event cap is.
            simulator.RunUntil(0);
        }

        log.LogInformation("Build: {Errors} errors, {Warnings} warnings, {Chips} chips, {Nets} nets",
            buildResult.ErrorCount, buildResult.WarningCount,
            simulator?.Chips.Count ?? 0, buildResult.NetTable?.Nets.Count ?? 0);

        SetState(buildResult.Succeeded ? SimState.Built : SimState.Edit);
        return buildResult;
    }

    private IReadOnlyDictionary<SchematicItem, SpdtSwitchInput> BuildSpdtMap()
    {
        Dictionary<SchematicItem, SpdtSwitchInput> map = new();
        if (simulator is null || buildResult?.NetTable is not NetTable table) return map;

        foreach (Device dev in schematic.Devices)
        {
            if (dev.Definition.Identifier is not ("spdt-switch" or "jumper-3pin")) continue;
            foreach (Unit unit in dev.Units)
            {
                Net? a = table.FindNet(new PinRef(unit.Id, 1));
                Net? com = table.FindNet(new PinRef(unit.Id, 2));
                Net? b = table.FindNet(new PinRef(unit.Id, 3));
                if (a is null || com is null || b is null) continue;
                foreach (IChip chip in simulator.Chips)
                    if (chip is SpdtSwitchInput sp
                        && ReferenceEquals(sp.Nets[0], a)
                        && ReferenceEquals(sp.Nets[1], com)
                        && ReferenceEquals(sp.Nets[2], b))
                    { map[unit] = sp; break; }
            }
        }
        return map;
    }

    public void SetSpdtPosition(SpdtSwitchInput sw, bool throwB)
    {
        if (simulator is null) return;
        sw.SetThrowB(throwB, (IScheduler)simulator);
        simulator.RunUntil(simulator.CurrentTick);
    }

    /// <summary>Press or release a button during simulation.</summary>
    public void SetButtonPressed(ButtonInput button, bool pressed)
    {
        if (simulator is null) return;
        button.SetPressed(pressed, (IScheduler)simulator);
        // The press schedules tick-0 events; drain them so the canvas reflects
        // the change on the next repaint without waiting for the timer.
        simulator.RunUntil(simulator.CurrentTick);
    }

    /// <summary>Open or close a switch during simulation.</summary>
    public void SetSwitchClosed(SwitchInput sw, bool closed)
    {
        if (simulator is null) return;
        sw.SetClosed(closed, (IScheduler)simulator);
        // Drain the tick-0 events the toggle scheduled so the canvas reflects
        // the change on the next repaint without waiting for the timer.
        simulator.RunUntil(simulator.CurrentTick);
    }

    /// <summary>
    /// Format a probe tooltip for a connection's net: pins, current value,
    /// how long ago it last changed, and — when the schematic has exactly one
    /// clock source — how long after the launching clock edge the net settled,
    /// plus the max clock that settle time implies. Null if the net isn't in
    /// the build.
    /// </summary>
    public string? GetProbeText(Connection connection)
    {
        if (buildResult?.NetTable is not NetTable table) return null;
        if (connection.A.Owner is null) return null;

        Net? net = table.FindNet(new PinRef(connection.A.Owner.Id, connection.A.Number));
        if (net is null) return null;

        var pinNames = net.Pins
            .Select(p => $"{ResolveDesignator(p.ItemId)}.{p.PinNumber}")
            .OrderBy(s => s);
        string pins = string.Join(", ", pinNames);

        string state = net.Value.ToString();
        string ago = net.LastChangeTick < 0
            ? "never"
            : FormatAge(CurrentTick - net.LastChangeTick);

        var sb = new System.Text.StringBuilder();
        sb.Append(pins).Append('\n')
          .Append("State: ").Append(state).Append('\n')
          .Append("Last change: ").Append(ago);

        // Clock-relative timing: the gap from the most recent clock edge at or
        // before this net's last change to that change is the path delay
        // feeding this node. Only meaningful with a single system clock, and
        // only a true delay while the clock is slow enough that the node
        // settles within a half-cycle (otherwise the measured edge isn't the
        // launching edge and the figure under-reports).
        if (net.LastChangeTick >= 0 && SingleClockHalfPeriodPs() is long halfPeriod)
        {
            long edge = PrecedingEdge(net.LastChangeTick, halfPeriod, out bool rising);
            if (edge >= 0)
            {
                long settle = net.LastChangeTick - edge;
                sb.Append('\n')
                  .Append("Settled ").Append(FormatDelay(settle))
                  .Append(rising ? " after rising edge" : " after falling edge");

                if (settle > 0)
                {
                    double maxHz = 1.0e12 / settle;   // 1 / settle(seconds)
                    sb.Append('\n')
                      .Append("Max clock (this node): ")
                      .Append(FormatFreq(maxHz))
                      .Append(" (excl. setup)");
                }
            }
        }

        return sb.ToString();
    }

    private string ResolveDesignator(string itemId)
    {
        foreach (Device dev in schematic.Devices)
            foreach (Unit u in dev.Units)
                if (u.Id == itemId)
                    return dev.Units.Count > 1
                        ? $"{dev.Designator}{u.UnitLetter}"
                        : dev.Designator;

        foreach (SchematicItem item in schematic.Items)
            if (item.Id == itemId)
                return item switch
                {
                    VccSymbol => "VCC",
                    GndSymbol => "GND",
                    ClockSource => "CLK",
                    CanOscillator => "OSC",
                    _ => itemId
                };

        return itemId;
    }

    private static string FormatAge(long ticks)
    {
        double sec = ticks / 1.0e12;
        return sec < 1.0 ? $"{sec * 1000.0:0.###} ms ago" : $"{sec:0.###} s ago";
    }

    /// <summary>
    /// Most recent clock edge at or before <paramref name="tick"/>. The sim
    /// clock starts Low at tick 0 and toggles every <paramref name="halfPeriod"/>
    /// ticks, so edges land on every multiple of the half-period: k*halfPeriod
    /// is a rising edge when k is odd, a falling edge when k is even. Returns
    /// -1 before the first edge. <paramref name="rising"/> reports polarity.
    /// </summary>
    private static long PrecedingEdge(long tick, long halfPeriod, out bool rising)
    {
        rising = false;
        if (halfPeriod <= 0 || tick < halfPeriod) return -1;
        long k = tick / halfPeriod;          // largest k with k*halfPeriod <= tick
        rising = (k % 2) == 1;
        return k * halfPeriod;
    }

    /// <summary>
    /// Half-period (ps) of the schematic's clock, but only when there is
    /// exactly one clock source — edge-relative timing is ambiguous with
    /// zero or several. Uses the same period formula the build pipeline uses
    /// (1e12 / FrequencyHz, then halved), so it matches the running clock.
    /// </summary>
    private long? SingleClockHalfPeriodPs()
    {
        long half = 0;
        int count = 0;
        foreach (SchematicItem item in schematic.Items)
        {
            if (item is ClockSource clk && clk.FrequencyHz > 0)
            {
                count++;
                half = (long)(1e12 / clk.FrequencyHz) / 2;
            }
            if (item is CanOscillator osc && osc.FrequencyHz > 0)
            {
                count++;
                half = (long)(1e12 / osc.FrequencyHz) / 2;
            }
        }
        return count == 1 ? half : (long?)null;
    }

    /// <summary>Format an absolute delay (ps) as ns or µs for the probe tooltip.</summary>
    private static string FormatDelay(long ps)
    {
        double ns = ps / 1000.0;
        return ns < 1000.0 ? $"{ns:0.###} ns" : $"{ns / 1000.0:0.###} µs";
    }

    /// <summary>Format a frequency (Hz) as Hz / kHz / MHz for the probe tooltip.</summary>
    private static string FormatFreq(double hz)
    {
        if (hz >= 1e6) return $"{hz / 1e6:0.###} MHz";
        if (hz >= 1e3) return $"{hz / 1e3:0.###} kHz";
        return $"{hz:0.###} Hz";
    }

    private IReadOnlyDictionary<SchematicItem, ButtonInput> BuildButtonMap()
    {
        Dictionary<SchematicItem, ButtonInput> map = new();
        if (simulator is null || buildResult?.NetTable is not NetTable table) return map;

        foreach (Device dev in schematic.Devices)
        {
            bool isButton4 = dev.Definition.Identifier == "button-4";
            if (dev.Definition.Identifier != "button" && !isButton4) continue;

            // The ButtonInput contact spans pins 1 & 2 (2-pin) or 1 & 3 (4-pin,
            // one leg of each terminal).
            int pinB = isButton4 ? 3 : 2;

            foreach (Unit unit in dev.Units)
            {
                Net? p1Net = table.FindNet(new PinRef(unit.Id, 1));
                Net? pBNet = table.FindNet(new PinRef(unit.Id, pinB));
                if (p1Net is null || pBNet is null) continue;

                foreach (IChip chip in simulator.Chips)
                {
                    if (chip is ButtonInput btn
                        && ReferenceEquals(btn.Nets[0], p1Net)
                        && ReferenceEquals(btn.Nets[1], pBNet))
                    {
                        map[unit] = btn;
                        break;
                    }
                }
            }
        }
        return map;
    }

    private IReadOnlyDictionary<SchematicItem, SwitchInput> BuildSwitchMap()
    {
        Dictionary<SchematicItem, SwitchInput> map = new();
        if (simulator is null || buildResult?.NetTable is not NetTable table) return map;

        foreach (Device dev in schematic.Devices)
        {
            if (dev.Definition.Identifier is not ("switch" or "jumper-2pin")) continue;   // BuildSwitchMap

            foreach (Unit unit in dev.Units)
            {
                // Match by BOTH pin nets -- pin 1 alone isn't unique when two
                // switches share a rail (e.g. both pin-1 to VCC).
                Net? p1Net = table.FindNet(new PinRef(unit.Id, 1));
                Net? p2Net = table.FindNet(new PinRef(unit.Id, 2));
                if (p1Net is null || p2Net is null) continue;

                foreach (IChip chip in simulator.Chips)
                {
                    if (chip is SwitchInput sw
                        && ReferenceEquals(sw.Nets[0], p1Net)
                        && ReferenceEquals(sw.Nets[1], p2Net))
                    {
                        map[unit] = sw;
                        break;
                    }
                }
            }
        }
        return map;
    }

    private IReadOnlyDictionary<SchematicItem, SevenSegCa> BuildDisplayMap()
    {
        Dictionary<SchematicItem, SevenSegCa> map = new();
        if (simulator is null || buildResult?.NetTable is not NetTable table) return map;

        foreach (Device dev in schematic.Devices)
        {
            if (dev.Definition.Identifier != "7seg-ca") continue;

            foreach (Unit unit in dev.Units)
            {
                // Use segment pin 'a' (pin 1) as the identifier. It's unique per
                // display in any realistic circuit -- two displays never share
                // their segment-a net.
                Net? segANet = table.FindNet(new PinRef(unit.Id, 1));
                if (segANet is null) continue;

                foreach (IChip chip in simulator.Chips)
                {
                    if (chip is SevenSegCa disp && ReferenceEquals(disp.Nets[0], segANet))
                    {
                        map[unit] = disp;
                        break;
                    }
                }
            }
        }
        return map;
    }

    /// <summary>Called by MainForm when the schematic mutates in a way that invalidates the build.</summary>
    public void Invalidate()
    {
        if (State == SimState.Edit) return;
        // If running/paused, force back to Edit -- the simulator instance is stale.
        timer.Stop();
        simulator = null;
        buildResult = null;
        SetState(SimState.Edit);
    }

    public void Run()
    {
        log.LogInformation("Run requested");

        if (simulator is null) return;
        lastFrameWallClock = DateTime.UtcNow;
        timer.Start();
        SetState(SimState.Running);
    }

    public void Pause()
    {
        log.LogInformation("Pause requested");

        if (State != SimState.Running) return;
        timer.Stop();
        SetState(SimState.Paused);
    }

    public void Stop()
    {
        log.LogInformation("Stop requested");

        if (State != SimState.Running && State != SimState.Paused) return;
        timer.Stop();

        // Rebuild from scratch so the next Run starts at tick 0 with all
        // chips re-initialised. Cleaner than trying to reset every chip.
        Build();
    }

    /// <summary>Advance one event (for debugging). Only valid in Paused state.</summary>
    public void Step()
    {
        if (State != SimState.Paused || simulator is null) return;

        long nextTick = simulator.Nets.Nets.Count > 0
            ? simulator.CurrentTick
            : simulator.CurrentTick;
        // Run until just after the next pending event.
        simulator.RunUntil(simulator.CurrentTick + 1);
        Ticked?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------ internals

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (simulator is null) { timer.Stop(); return; }

        DateTime now = DateTime.UtcNow;
        double realMs = (now - lastFrameWallClock).TotalMilliseconds;
        lastFrameWallClock = now;

        // Convert real milliseconds to simulated picoseconds.
        // 1 ms = 1e9 ps. Speed factor scales how many sim-ps per real-ms.
        long simDelta = (long)(realMs * 1.0e9 * SpeedFactor);
        if (simDelta < 1) simDelta = 1;

        simulator.RunUntil(simulator.CurrentTick + simDelta);
        Ticked?.Invoke(this, EventArgs.Empty);
    }

    private void SetState(SimState newState)
    {
        if (State == newState) return;
        State = newState;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Look up the current resolved signal on the net containing this
    /// connection. Returns null if the connection's net isn't in the build.
    /// </summary>
    public Signal? GetSignal(Connection connection)
    {
        if (buildResult?.NetTable is not NetTable table) return null;
        if (connection.A.Owner is null) return null;

        Net? net = table.FindNet(new PinRef(connection.A.Owner.Id, connection.A.Number));
        return net?.Value;
    }

    /// <summary>
    /// Look up the current resolved signal on the net attached to a specific
    /// pin of an item. Returns null if that pin isn't on a net in the build.
    /// </summary>
    public Signal? GetPinSignal(SchematicItem item, int pinNumber)
    {
        if (buildResult?.NetTable is not NetTable table) return null;
        Net? net = table.FindNet(new PinRef(item.Id, pinNumber));
        return net?.Value;
    }

    /// <summary>
    /// Discard the current build entirely and return to Edit state.
    /// Called by Esc in Built or Paused state.
    /// </summary>
    public void ClearBuild()
    {
        if (State == SimState.Edit) return;
        timer.Stop();
        simulator = null;
        buildResult = null;
        DisplayBindings = new Dictionary<SchematicItem, SevenSegCa>();
        SetState(SimState.Edit);
        log.LogInformation("Build cleared");
    }

    /// <summary>
    /// Map from each 7-segment display unit on the canvas to its bound
    /// SevenSegCa chip. Rebuilt each time Build runs.
    /// </summary>
    public IReadOnlyDictionary<SchematicItem, SevenSegCa> DisplayBindings { get; private set; } =
        new Dictionary<SchematicItem, SevenSegCa>();
}