using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Oversight.Linux.Platform;
using Oversight.Linux.Services;
using Oversight.Services;

namespace Oversight.Linux;

public static class Program
{
    public static string[] Args { get; private set; } = [];
    public static bool SelfTest { get; private set; }
    public static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        SelfTest = args.Contains("--self-test");
        X11.InitThreads();   // before any other Xlib call: the hotkey thread has its own connection

        if (SelfTest)
        {
            // A throwaway config folder, so the test never touches real settings.
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("oversight-selftest-").FullName);
            Linux.SelfTest.StartWatchdog();
            if (!X11.Available) { Console.WriteLine("FAIL display: no X display (set DISPLAY)"); return 1; }
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }

        // Never leave the desktop zoomed: a crash or logout puts the user's magnifier back.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { Magnifier.EmergencyReset(); ErrorLog.Write(e.ExceptionObject); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Magnifier.EmergencyReset();

        if (!X11.Available)
        {
            const string message = "Oversight needs an X11 session or XWayland for its hotkeys, crosshair and auto clicker, and there's no X display.";
            Console.Error.WriteLine(message);
            new Notifier().ShowAsync("Oversight can't start", message).GetAwaiter().GetResult();
            return 1;
        }

        // After an update the new copy waits for the old one to exit, then removes the old file.
        if (args.SkipWhile(a => a != "--wait").Skip(1).FirstOrDefault() is string pidText && int.TryParse(pidText, out int pid))
        {
            try
            {
                using var old = Process.GetProcessById(pid);
                Updater.Log($"{Updater.CurrentVersion}: waiting for {old.ProcessName} ({pid}) to exit.");
                // An old copy that didn't quit would hold the hotkeys and the tray, so close it (name check guards against a reused pid).
                if (old.ProcessName.StartsWith("oversight", StringComparison.OrdinalIgnoreCase) && !old.WaitForExit(15000))
                {
                    Updater.Log($"{Updater.CurrentVersion}: old copy still running after 15 s, closing it.");
                    old.Kill();
                    old.WaitForExit(5000);
                }
            }
            catch (ArgumentException) { }   // already gone
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Updater.Log($"{Updater.CurrentVersion}: couldn't close the old copy: {ex.Message}");
            }
        }
        if (Updater.CanSelfUpdate) Updater.CleanUpAfterUpdate();

        // One copy at a time; launching again opens the running copy's Settings.
        Instance = new SingleInstance("oversight" + AppProfile.Suffix);
        if (!Instance.IsFirst) return 0;
        Magnifier.EmergencyReset();   // a previous run crashed while zoomed

        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown); }
        finally { Instance.Dispose(); }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
