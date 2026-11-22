using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Glimpse.App;

public partial class App : Application
{
    private static readonly object LogLock = new();

    /// <summary>Command-line arguments; the first is Skyline's $(SkylineConnection) when launched as a tool.</summary>
    public static string[] LaunchArgs { get; private set; } = Array.Empty<string>();

    /// <summary>%LOCALAPPDATA%\SkylineGlimpse\glimpse.log. The packaging smoke test reads it.</summary>
    public static string LogFilePath { get; } = InitLogPath();

    /// <summary>The release version, from Directory.Build.props (CalVer YY.feature.patch).</summary>
    public static string Version { get; } =
        typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?";

    /// <summary>"Glimpse 26.1.0": the window title, so a screenshot or a bug report says which build it was.</summary>
    public static string ProductTitle => "Glimpse " + Version;

    public static void WriteLog(string line)
    {
        try
        {
            lock (LogLock)
                File.AppendAllText(LogFilePath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
        }
        catch (Exception)
        {
            // Logging must never take the tool down.
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        LaunchArgs = e.Args;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Report("background thread", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Report("unobserved task", args.Exception);
            args.SetObserved();
        };
        Glimpse.Core.Diagnostics.LogFile.RollOver(LogFilePath);
        WriteLog($"==== Glimpse {Version} started ====");
        WriteLog("Args: " + string.Join(" ", e.Args));
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report("UI thread", e.Exception);
        e.Handled = true; // keep the window open so the error can be read
        MessageBox.Show(e.Exception.Message, "Glimpse", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void Report(string where, Exception? ex) =>
        WriteLog($"UNHANDLED ({where}): {ex}");

    private static string InitLogPath()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkylineGlimpse");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "glimpse.log");
        }
        catch (Exception)
        {
            return Path.Combine(Path.GetTempPath(), "glimpse.log");
        }
    }
}
