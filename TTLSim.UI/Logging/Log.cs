using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace TTLSim.UI.Logging;

/// <summary>
/// Global logging facade. Serilog underneath, exposed via the Microsoft
/// ILogger abstraction so non-UI code can log without referencing Serilog.
///
/// Verbose mode (Debug level) is toggled at runtime via Help -> Verbose Logging.
/// The choice is persisted to %APPDATA%\TTLSim\logging-state.txt.
/// </summary>
public static class Log
{
    private static ILoggerFactory? factory;
    private static readonly LoggingLevelSwitch levelSwitch = new(LogEventLevel.Information);

    public static string LogFolder { get; private set; } = "";

    /// <summary>True when verbose (Debug) logging is on; false for Information.</summary>
    public static bool Verbose
    {
        get => levelSwitch.MinimumLevel <= LogEventLevel.Debug;
        set
        {
            levelSwitch.MinimumLevel = value ? LogEventLevel.Debug : LogEventLevel.Information;
            SaveState(value);
        }
    }

    public static void Initialize()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string ttlsimFolder = Path.Combine(appData, "TTLSim");
        Directory.CreateDirectory(ttlsimFolder);

        LogFolder = Path.Combine(ttlsimFolder, "Logs");
        Directory.CreateDirectory(LogFolder);

        // Start each launch with a clean log for today.
        try
        {
            string todayLog = Path.Combine(
                LogFolder, $"ttlsim-{DateTime.Now:yyyyMMdd}.log");
            if (File.Exists(todayLog)) File.Delete(todayLog);
        }
        catch { /* not fatal */ }

        // Load persisted verbose-mode choice.
        levelSwitch.MinimumLevel = LoadState() ? LogEventLevel.Debug : LogEventLevel.Information;

        string pathTemplate = Path.Combine(LogFolder, "ttlsim-.log");

        var serilog = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .WriteTo.Async(a => a.File(
                path: pathTemplate,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 50_000_000,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"))
            .CreateLogger();

        Serilog.Log.Logger = serilog;
        factory = new SerilogLoggerFactory(serilog, dispose: true);
    }

    /// <summary>
    /// Tear the logger down, delete every file in <see cref="LogFolder"/>,
    /// and re-initialize -- giving a genuinely fresh, empty log. The current
    /// verbose-mode choice is preserved (it is reloaded from the state file
    /// by Initialize). Any file the OS still has locked is skipped rather
    /// than throwing, so a stray held handle can't break a build.
    ///
    /// <para>
    /// Callers must not hold a cached <see cref="ILogger"/> across this call:
    /// Shutdown disposes the factory, so loggers created before Reset stop
    /// working. The two long-lived loggers in the codebase
    /// (SchematicDtoMapper, SimulationController) fetch theirs fresh per use
    /// for exactly this reason.
    /// </para>
    /// </summary>
    public static void Reset()
    {
        string folder = LogFolder;

        Shutdown();

        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            foreach (string file in Directory.GetFiles(folder))
            {
                try { File.Delete(file); }
                catch
                { /* still locked by the async sink's tail, or AV --
                           skip it; Initialize will write alongside it */
                }
            }
        }

        Initialize();
    }

    public static ILogger For<T>() => factory!.CreateLogger<T>();
    public static ILogger For(string category) => factory!.CreateLogger(category);

    public static void Shutdown()
    {
        factory?.Dispose();
        factory = null;
        Serilog.Log.CloseAndFlush();
    }

    // ----- persisted state -----

    private static string StateFile =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TTLSim",
            "logging-state.txt");

    private static bool LoadState()
    {
        try
        {
            return File.Exists(StateFile) && File.ReadAllText(StateFile).Trim() == "verbose";
        }
        catch { return false; }
    }

    private static void SaveState(bool verbose)
    {
        try
        {
            File.WriteAllText(StateFile, verbose ? "verbose" : "normal");
        }
        catch { /* not fatal */ }
    }
}