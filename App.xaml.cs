using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Nova.Core;
using Nova.Services;

namespace Nova;

public partial class App : Application
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nova");

    protected override void OnStartup(StartupEventArgs e)
    {
        // Beklenmeyen bir hata konsol deneyimini tamamen kapatmasın: logla ve devam et.
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        var args = e.Args;

        var store = ProfileStore.Load();
        string? forced = null;
        int pi = Array.IndexOf(args, "--profile");
        if (pi >= 0 && pi + 1 < args.Length) forced = args[pi + 1];
        AppSettings.Use(forced ?? store.LastId);
        L.Init(AppSettings.Current.Language);

        // Nova kapalıyken kısayolu ve kolu dinleyen küçük süreç.
        if (args.Contains("--watcher"))
        {
            Watcher.Run(this);
            return;
        }

        if (args.Contains("--uninstall"))
        {
            Installer.Uninstall();
            Shutdown();
            return;
        }

        // İkinci kez açılırsa var olan Nova'yı öne getir ve çık.
        if (!SingleInstance.Claim())
        {
            Shutdown();
            return;
        }
        Watcher.StopRunning();
        Updater.CleanupOld();

        bool background = args.Contains("--background");
        var window = new MainWindow(store, forced, startHidden: background, skipBoot: args.Contains("--noboot"));
        MainWindow = window;
        SingleInstance.Listen(window.BringBack);
        if (background) new WindowInteropHelper(window).EnsureHandle();
        else window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SingleInstance.Release();
        base.OnExit(e);
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(Path.Combine(DataDir, "error.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch { }
    }
}
