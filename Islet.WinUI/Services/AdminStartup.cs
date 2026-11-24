using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace Islet;

// "Start with admin rights" for CPU temperatures: a Task Scheduler logon task with highest privileges,
// so Windows starts the app elevated at sign-in without a UAC prompt each time. Creating or removing
// the task needs admin once, so those two calls show a single UAC prompt (only when the user asks).
internal static class AdminStartup
{
    private const string TaskName = "islet (admin)";
    public const string PawnIoUrl = "https://pawnio.eu/";

    public static bool IsRegistered()
    {
        var (exitCode, _) = Run(false, $"/Query /TN \"{TaskName}\"");
        return exitCode == 0;
    }

    public static Task<bool> SetAsync(bool enabled) => Task.Run(() =>
    {
        if (!enabled) return Run(true, $"/Delete /F /TN \"{TaskName}\"").ExitCode == 0;

        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        // XML instead of plain switches: the defaults would skip the task on battery power.
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string xmlPath = Path.Combine(Path.GetTempPath(), "islet-admin-task.xml");
        File.WriteAllText(xmlPath, TaskXml(user, exe), Encoding.Unicode);
        try
        {
            return Run(true, $"/Create /F /TN \"{TaskName}\" /XML \"{xmlPath}\"").ExitCode == 0;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch (IOException) { }
        }
    });

    private static (int ExitCode, bool Started) Run(bool elevated, string arguments)
    {
        try
        {
            var info = new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = elevated // "runas" needs the shell
            };
            if (elevated) info.Verb = "runas";
            using var process = Process.Start(info);
            if (process is null) return (-1, false);
            process.WaitForExit(15000);
            return (process.HasExited ? process.ExitCode : -1, true);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (-1, false); // The user said no to the UAC prompt.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"schtasks failed: {ex.Message}");
            return (-1, false);
        }
    }

    private static string TaskXml(string user, string exe) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{SecurityElement.Escape(user)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(user)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(exe)}</Command>
            </Exec>
          </Actions>
        </Task>
        """;
}
