using Microsoft.Extensions.Logging;
using TTLSim.Core;

namespace TTLSim.Chips.Registers;

/// <summary>
/// 74HC74 — dual positive-edge-triggered D-type flip-flop with asynchronous,
/// active-LOW preset (/PRE) and clear (/CLR), 14-pin DIP.
///
/// Each half latches D on the rising edge of its clock. /CLR LOW forces Q=0
/// and /PRE LOW forces Q=1, both asynchronous and overriding the clock. If
/// both are LOW at once the real part drives Q and /Q both HIGH (an illegal
/// input combination); that is modelled here for completeness, though normal
/// use never asserts both. /Q is the complement of Q outside that state.
///
/// Pin map (ChipPartDefinition.Ic7474):
///   A: /CLR=1  D=2  CLK=3  /PRE=4  Q=5  /Q=6
///   B: /CLR=13 D=12 CLK=11 /PRE=10 Q=9  /Q=8
///   VCC=14  GND=7   (power consumed by the build pipeline)
///
/// Clocking one half from an inverted clock turns it into a negative-edge
/// flip-flop -- the basis of glitch-free clock gating.
/// </summary>
public sealed class Hc74 : IChip
{
    public const long PropagationDelayPs = 20_000;

    private readonly Net[] nets;
    private readonly Ff ffA;
    private readonly Ff ffB;

    public Hc74(
        Net aClrN, Net aD, Net aClk, Net aPreN, Net aQ, Net aQn,
        Net bQn, Net bQ, Net bPreN, Net bClk, Net bD, Net bClrN,
        string label = "74",
        ILogger? logger = null,
        long delayPs = PropagationDelayPs)
    {
        // Order MUST match PinNumbers below (ascending pin number).
        //  0 /ACLR  1 AD  2 ACLK  3 /APRE  4 AQ  5 /AQ
        //  6 /BQ    7 BQ  8 /BPRE 9 BCLK  10 BD 11 /BCLR
        nets = new[]
        {
            aClrN, aD, aClk, aPreN, aQ, aQn,
            bQn, bQ, bPreN, bClk, bD, bClrN
        };

        ILogger lg = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        ffA = new Ff(clrN: 0, d: 1, clk: 2, preN: 3,
                     new Driver(aQ, DriveStrength.Strong),
                     new Driver(aQn, DriveStrength.Strong), label, "A", lg, delayPs);
        ffB = new Ff(clrN: 11, d: 10, clk: 9, preN: 8,
                     new Driver(bQ, DriveStrength.Strong),
                     new Driver(bQn, DriveStrength.Strong), label, "B", lg, delayPs);
    }

    public IReadOnlyList<int> PinNumbers { get; }
        = new[] { 1, 2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 13 };

    public IReadOnlyList<Net> Nets => nets;

    public void Initialize(IScheduler scheduler)
    {
        ffA.Initialize(nets, scheduler);
        ffB.Initialize(nets, scheduler);
    }

    public void OnInputChanged(int pinIndex, IScheduler scheduler)
    {
        if (ffA.Owns(pinIndex)) ffA.OnInputChanged(pinIndex, nets, scheduler);
        if (ffB.Owns(pinIndex)) ffB.OnInputChanged(pinIndex, nets, scheduler);
    }

    /// <summary>One independent flip-flop half.</summary>
    private sealed class Ff
    {
        private readonly int clrN, d, clk, preN;
        private readonly Driver qDrv, qnDrv;
        private readonly string label, half;
        private readonly ILogger logger;
        private readonly long delayPs;
        private bool state;
        private Signal prevClk = Signal.Unknown;

        public Ff(int clrN, int d, int clk, int preN,
                  Driver qDrv, Driver qnDrv, string label, string half, ILogger logger, long delayPs)
        {
            this.clrN = clrN; this.d = d; this.clk = clk; this.preN = preN;
            this.qDrv = qDrv; this.qnDrv = qnDrv;
            this.label = label; this.half = half; this.logger = logger;
            this.delayPs = delayPs;
        }

        public bool Owns(int pinIndex) =>
            pinIndex == clrN || pinIndex == d || pinIndex == clk || pinIndex == preN;

        public void Initialize(Net[] nets, IScheduler s)
        {
            prevClk = nets[clk].Value;
            if (!ApplyAsync(nets, s))   // async preset/clear win at power-up
            {
                state = false;          // otherwise start cleared
                Emit(s);
            }
        }

        public void OnInputChanged(int pinIndex, Net[] nets, IScheduler s)
        {
            if (pinIndex == clrN || pinIndex == preN)
            {
                ApplyAsync(nets, s);
                return;
            }
            if (pinIndex == clk)
            {
                Signal nc = nets[clk].Value;
                bool rising = prevClk == Signal.Low && nc == Signal.High;
                prevClk = nc;
                if (!rising) return;
                if (Low(nets[clrN]) || Low(nets[preN])) return;  // async overrides clock
                bool next = nets[d].Value == Signal.High;
                if (next != state)
                {
                    state = next;
                    logger.LogDebug("{Label}{Half} CLK^ -> Q={Q}", label, half, state ? 1 : 0);
                    Emit(s);
                }
            }
            // D change is sampled at the clock edge -- no asynchronous action.
        }

        private static bool Low(Net n) => n.Value == Signal.Low;  // active-low input asserted

        /// <returns>true if an asynchronous input currently owns the state.</returns>
        private bool ApplyAsync(Net[] nets, IScheduler s)
        {
            bool clr = Low(nets[clrN]);
            bool pre = Low(nets[preN]);
            if (clr && pre)
            {
                // Illegal combo: the '74 drives both outputs HIGH.
                s.Schedule(delayPs, qDrv, Signal.High);
                s.Schedule(delayPs, qnDrv, Signal.High);
                return true;
            }
            if (clr) { state = false; Emit(s); return true; }
            if (pre) { state = true; Emit(s); return true; }
            return false;
        }

        private void Emit(IScheduler s)
        {
            s.Schedule(delayPs, qDrv, state ? Signal.High : Signal.Low);
            s.Schedule(delayPs, qnDrv, state ? Signal.Low : Signal.High);
        }
    }
}