using System.Windows;
using System.Windows.Forms;

namespace DynamicIslandMusic;

public partial class OptionsWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly MainWindow _main;

    public OptionsWindow(MainViewModel vm, MainWindow main)
    {
        InitializeComponent();
        _vm = vm;
        _main = main;

        FillSources();
        FillMonitors();

        AutoHideCheck.IsChecked = MainViewModel.AutoHide;
        DodgeCheck.IsChecked = MainViewModel.DodgeOnHover;
        HotkeysCheck.IsChecked = MainViewModel.HotkeysEnabled;
        StartupCheck.IsChecked = TrayIcon.IsStartupEnabled();

        SourceCombo.SelectionChanged += (_, _) =>
        {
            if (SourceCombo.SelectedItem is not MediaSessionInfo info) return;
            _vm.PreferredSessionId = string.IsNullOrEmpty(info.Id) ? null : info.Id;
        };
        MonitorCombo.SelectionChanged += (_, _) =>
        {
            MainViewModel.MonitorIndex = MonitorCombo.SelectedIndex <= 0 ? -1 : MonitorCombo.SelectedIndex - 1;
            _main.ApplyOptions();
        };
        AutoHideCheck.Checked += (_, _) => SetAutoHide();
        AutoHideCheck.Unchecked += (_, _) => SetAutoHide();
        DodgeCheck.Checked += (_, _) => SetDodge();
        DodgeCheck.Unchecked += (_, _) => SetDodge();
        HotkeysCheck.Checked += (_, _) => MainViewModel.HotkeysEnabled = HotkeysCheck.IsChecked == true;
        StartupCheck.Checked += (_, _) => TrayIcon.SetStartupEnabled(StartupCheck.IsChecked == true);
        StartupCheck.Unchecked += (_, _) => TrayIcon.SetStartupEnabled(false);
    }

    private void SetAutoHide()
    {
        MainViewModel.AutoHide = AutoHideCheck.IsChecked == true;
        _main.ApplyOptions();
    }

    private void SetDodge()
    {
        MainViewModel.DodgeOnHover = DodgeCheck.IsChecked == true;
        _main.ApplyOptions();
    }

    private void FillSources()
    {
        string? current = _vm.PreferredSessionId;
        SourceCombo.Items.Clear();
        SourceCombo.Items.Add(new MediaSessionInfo("", "Automatic"));
        foreach (var session in _vm.Sessions) SourceCombo.Items.Add(session);

        SourceCombo.SelectedItem = SourceCombo.Items
            .Cast<MediaSessionInfo>()
            .FirstOrDefault(s => s.Id == current) ?? SourceCombo.Items[0];
    }

    private void FillMonitors()
    {
        MonitorCombo.Items.Clear();
        MonitorCombo.Items.Add("Primary monitor");
        foreach (var screen in Screen.AllScreens) MonitorCombo.Items.Add(screen.DeviceName);

        int index = MainViewModel.MonitorIndex;
        MonitorCombo.SelectedIndex = index < 0 ? 0 : Math.Min(index + 1, MonitorCombo.Items.Count - 1);
    }

    private void RefreshSources_Click(object sender, RoutedEventArgs e)
    {
        _vm.Media.Refresh();
        FillSources();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
