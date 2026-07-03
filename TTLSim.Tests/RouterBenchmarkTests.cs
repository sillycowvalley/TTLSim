using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TTLSim.UI.Persistence;
using TTLSim.UI.Routing;
using Xunit;
using Xunit.Abstractions;

namespace TTLSim.Benchmarks;

/// <summary>
/// Wall-clock benchmark for <see cref="WireRouter.RouteAll"/> against a
/// fixed representative schematic, so router changes are measured on
/// identical work instead of compared across profiler sessions.
///
/// Setup (once): add the .ttlproj to the test project in Solution
/// Explorer under a "Benchmarks" folder, open its Properties, and set
/// "Copy to Output Directory" to "Copy if newer".
///
/// Run it from Test Explorer; the timing appears in the test's output
/// pane ("Open additional output for this result").
///
/// Besides the timing, the test reports route-geometry stats (polyline
/// count, total cells, total bends). Cost-optimal router changes can
/// legitimately move wires between equal-cost routes, but a jump in
/// bends or length after a "pure performance" change is a red flag
/// worth eyeballing on the canvas before trusting the speed number.
/// </summary>
public class RouterBenchmarkTests
{
    private const string BenchmarkFile = "Blinky_PC_-_4_Bit_-_CALL.ttlproj";
    private const int WarmupIterations = 3;
    private const int TimedIterations =  50;

    private readonly ITestOutputHelper output;

    public RouterBenchmarkTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void RouteAll_benchmark()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "Benchmarks", BenchmarkFile);
        Assert.True(File.Exists(path),
            $"Benchmark schematic not found: {path}. Add it to the test " +
            "project under a Benchmarks folder with Copy to Output " +
            "Directory = Copy if newer.");

        var schematic = SchematicSerializer.Load(path).Schematic;
        var router = new WireRouter();

        // Warm-up: JIT, first-touch allocations, branch predictors.
        RouteResult result = router.RouteAll(schematic);
        for (int i = 1; i < WarmupIterations; i++)
            result = router.RouteAll(schematic);

        // Timed runs.
        var perRun = new double[TimedIterations];
        var sw = new Stopwatch();
        for (int i = 0; i < TimedIterations; i++)
        {
            sw.Restart();
            result = router.RouteAll(schematic);
            sw.Stop();
            perRun[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(perRun);
        double min = perRun[0];
        double median = perRun[TimedIterations / 2];
        double average = perRun.Average();

        // Geometry stats: a quality fingerprint for the routed result.
        int polylines = result.Polylines.Count;
        int totalCells = 0;
        int totalBends = 0;
        foreach (var poly in result.Polylines.Values)
        {
            totalCells += poly.Count;
            totalBends += Math.Max(0, poly.Count - 2);   // interior vertices
        }

        output.WriteLine($"RouteAll over {TimedIterations} iterations " +
                         $"({WarmupIterations} warm-up):");
        output.WriteLine($"  min     {min:F1} ms");
        output.WriteLine($"  median  {median:F1} ms");
        output.WriteLine($"  average {average:F1} ms");
        output.WriteLine($"Route geometry:");
        output.WriteLine($"  polylines {polylines}");
        output.WriteLine($"  cells     {totalCells}");
        output.WriteLine($"  bends     {totalBends}");
        output.WriteLine($"  junctions {result.Junctions.Count}");

        Assert.True(polylines > 0, "Router produced no polylines.");
    }
}