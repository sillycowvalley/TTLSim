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
/// The CLI accepts a single .pld file or a folder. A folder input compiles
/// every *.pld it contains (non-recursive). The -o output may be a filename or
/// an existing folder; when it is a folder, each .jed is named after its input.
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
        string? outputPath = null;    // -o : may name a file OR an existing folder
        string? deviceOverride = null;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" when i + 1 < args.Length:
                    outputPath = args[++i];
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

        // Folder input: compile every *.pld in the directory.
        if (Directory.Exists(inputPath))
            return CompileFolder(inputPath, outputPath, deviceOverride);

        // Single-file input.
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Input path not found: {inputPath}");
            return 1;
        }

        return CompileOne(inputPath, ResolveOutputPath(inputPath, outputPath), deviceOverride);
    }

    private static int CompileFolder(string dir, string? outputPath, string? deviceOverride)
    {
        // A whole folder produces many .jed files, so an -o output must itself be
        // a folder -- a single filename could not hold them all.
        if (outputPath != null && !Directory.Exists(outputPath))
        {
            Console.Error.WriteLine(
                $"When the input is a folder, -o must be an existing output folder (got: {outputPath}).");
            return 1;
        }

        string[] files = Directory.GetFiles(dir, "*.pld");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        if (files.Length == 0)
        {
            Console.Error.WriteLine($"No .pld files found in {dir}");
            return 1;
        }

        int failed = 0;
        foreach (string file in files)
        {
            Console.WriteLine($"--- {Path.GetFileName(file)} ---");
            if (CompileOne(file, ResolveOutputPath(file, outputPath), deviceOverride) != 0)
                failed++;
            Console.WriteLine();
        }

        int ok = files.Length - failed;
        Console.WriteLine(failed > 0
            ? $"Compiled {ok} of {files.Length} file(s), {failed} failed."
            : $"Compiled {ok} of {files.Length} file(s).");
        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// Decide where a given input's .jed is written:
    ///   no -o             -> alongside the source, extension changed to .jed;
    ///   -o &lt;folder&gt; (exists) -> that folder, named &lt;input&gt;.jed;
    ///   -o &lt;file&gt;         -> that path, used as given.
    /// </summary>
    private static string ResolveOutputPath(string inputFile, string? outputPath)
    {
        if (outputPath == null)
            return Path.ChangeExtension(inputFile, ".jed");
        if (Directory.Exists(outputPath))
            return Path.Combine(outputPath, Path.GetFileNameWithoutExtension(inputFile) + ".jed");
        return outputPath;
    }

    private static int CompileOne(string inputPath, string jedPath, string? deviceOverride)
    {
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
  blinkyjed <input.pld> [-o <output>] [-d <device>]
  blinkyjed <folder>    [-o <folder>] [-d <device>]

A folder input compiles every *.pld in it (non-recursive). If -o is an
existing folder, each .jed is written there using the input's name; if -o
is a filename it is used as-is (single-file input only); with no -o the
.jed is written alongside each source.

Options:
  -o <output>   output .jed file, OR an existing folder to write into
                (default: alongside the input, extension changed to .jed)
  -d <device>   target device, overriding the .pld header
                (G16V8 | G20V8 | ATF22V10)

Output:
  <name>.jed    JEDEC fuse map, programmable onto the target GAL");
    }
}