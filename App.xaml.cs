using System.Windows;
using System.Windows.Threading;

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
