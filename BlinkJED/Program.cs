using System;
using System.Collections.Generic;
using System.IO;

namespace BlinkyJed;

/// <summary>
/// BlinkyJED -- PLD logic compiler for the Blinky / Mini Blinky decode GALs.
///
/// Pipeline (mirrors a classic CUPL flow):
///
///     .pld source
///        -> PldParser.Parse      (tokens -> pin map + boolean equations)
///        -> TargetDevice.Resolve (G16V8 / G20V8 / ATF22V10)
///        -> Compiler.Compile     (SOP build + minimise + map to fuse array)
///        -> JedecWriter.Write     (fuse map -> JESD3 .jed text)
///     .jed output
///
/// The CLI shell here is complete. The three middle stages are the substantial
/// work and are scaffolded as stubs -- see each stage class for what it must do.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        int exitCode = Run(args);

        // Keep the console window open when launched from Explorer or a shortcut.
        // Skipped when input is redirected (piped or scripted), where there is no
        // interactive key to wait for and Console.ReadKey would otherwise throw.
        if (!Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.Write("Press any key to exit...");
            Console.ReadKey(intercept: true);
            Console.WriteLine();
        }

        return exitCode;
    }

    private static int Run(string[] args)
    {
        if (args.Length < 1 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length < 1 ? 1 : 0;
        }

        string inputPath = args[0];
        string? jedPath = null;
        string? deviceOverride = null;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" when i + 1 < args.Length:
                    jedPath = args[++i];
                    break;
                case "-d" when i + 1 < args.Length:
                    deviceOverride = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    PrintUsage();
                    return 1;
            }
        }

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Input file not found: {inputPath}");
            return 1;
        }

        jedPath ??= Path.ChangeExtension(inputPath, ".jed");

        string source = File.ReadAllText(inputPath);
        var errors = new List<string>();

        // ---- Stage 1: parse -------------------------------------------------
        PldDocument doc = PldParser.Parse(source, errors);
        if (errors.Count > 0) return Report(errors);

        // ---- Stage 2: resolve target device --------------------------------
        string deviceName = deviceOverride ?? doc.DeviceName;
        TargetDevice? device = TargetDevice.Resolve(deviceName, errors);
        if (device == null || errors.Count > 0) return Report(errors);

        // ---- Stage 3: compile to a fuse map --------------------------------
        FuseMap fuses = Compiler.Compile(doc, device, errors);
        if (errors.Count > 0) return Report(errors);

        // ---- Stage 4: emit JEDEC -------------------------------------------
        string jed = JedecWriter.Write(fuses, device, doc);
        File.WriteAllText(jedPath, jed);

        Console.WriteLine($"{device.Name}: {device.FuseCount} fuses -> {jedPath}");
        return 0;
    }

    private static int Report(List<string> errors)
    {
        foreach (string e in errors) Console.Error.WriteLine(e);
        Console.Error.WriteLine($"\nCompile failed: {errors.Count} error(s).");
        return 1;
    }

    private static bool IsHelp(string a) =>
        a is "-h" or "--help" or "/?" or "-?";

    private static void PrintUsage()
    {
        Console.WriteLine(
@"BlinkyJED -- PLD compiler for the Blinky decode GALs

Usage:
  blinkyjed <input.pld> [-o <output.jed>] [-d <device>]

Options:
  -o <output.jed>   output path (default: <input>.jed)
  -d <device>       target device, overriding the .pld header
                    (G16V8 | G20V8 | ATF22V10)

Output:
  <input>.jed       JEDEC fuse map, programmable onto the target GAL");
    }
}