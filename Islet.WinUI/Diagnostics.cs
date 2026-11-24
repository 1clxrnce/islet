using System.Diagnostics;
using System.IO;
using Microsoft.UI.Dispatching;

namespace Islet;

// startup.log (each step of the last start) and error.log, written next to the exe and in %AppData%\islet.
internal static class Log
{
    private static readonly object Gate = new();

    private static IEnumerable<string> Folders => new[] { AppContext.BaseDirectory, AppSettings.Folder };

    public static void Trace(string message, bool fresh = false)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
        Write("startup.log", line, fresh);
    }

    public static void Error(Exception? exception, string? context = null)
    {
        if (exception is null) return;
        Debug.WriteLine($"{context}: {exception}");
        string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(context is null ? "" : context + ": ")}{exception}{Environment.NewLine}{Environment.NewLine}";
        Write("error.log", entry, fresh: false);
    }

    // Runs one startup step; a failure is logged and startup carries on with the next step.
    public static bool Step(string name, Action action)
    {
        try
        {
            action();
            Trace($"{name}: ok");
            return true;
        }
        catch (Exception ex)
        {
            Trace($"{name}: FAILED - {ex.GetType().Name}: {ex.Message}");
            Error(ex, name);
            return false;
        }
    }

    private static void Write(string file, string text, bool fresh)
    {
        lock (Gate)
        {
            foreach (string folder in Folders)
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, file);
                    if (fresh) File.WriteAllText(path, text);
                    else File.AppendAllText(path, text);
                }
                catch
                {
                    // Logging must never throw.
                }
            }
        }
    }
}

// The UI thread's queue. Set once at startup; services post Windows' callbacks through it.
internal static class Ui
{
    public static DispatcherQueue? Queue { get; set; }

    public static void Post(Action action)
    {
        var queue = Queue;
        if (queue is null || queue.HasThreadAccess) action();
        else queue.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error(ex, "UI callback"); }
        });
    }
}
