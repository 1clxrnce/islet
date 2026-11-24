using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Islet;

public static class Program
{
    private const string InstanceKey = "islet.main";

    // Single instance through the Windows App SDK: a second launch hands its activation to the
    // running copy (which opens Settings) and exits, instead of fighting it over hotkeys and screen space.
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!mainInstance.IsCurrent)
        {
            try
            {
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                // Off the STA thread, so waiting does not block COM calls the redirect needs.
                Task.Run(() => mainInstance.RedirectActivationToAsync(activation).AsTask()).Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Redirecting to the running islet");
            }
            return 0;
        }

        Application.Start(_ =>
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
            Ui.Queue = queue;
            var app = new App();
            mainInstance.Activated += (_, _) => Ui.Post(app.OnSecondLaunch);
        });
        return 0;
    }
}
