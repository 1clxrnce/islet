using System.ComponentModel;
using Microsoft.UI.Xaml;
using WinUIEx;

namespace Islet;

public sealed partial class TestWindow : Window
{
    private readonly MainViewModel _vm;
    private string _loggedTitle = "";

    public TestWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        this.SetWindowSize(460, 300);
        this.CenterOnScreen();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "islet.ico"));

        _vm.PropertyChanged += OnChanged;
        Closed += (_, _) => _vm.PropertyChanged -= OnChanged;
        Refresh();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        TitleText.Text = _vm.DisplayTitle;
        ArtistText.Text = _vm.Artist;
        Art.Source = _vm.AlbumArt;
        StateText.Text = !_vm.IsActive
            ? "No active media session"
            : $"{(_vm.IsPlaying ? "Playing" : "Paused")} in {_vm.AppName}  ·  {_vm.Elapsed} / {MainViewModel.FormatTime(_vm.Duration)}";
        VolumeText.Text = $"System volume {Math.Round(_vm.Volume)}%{(_vm.IsMuted ? " (muted)" : "")}"
                          + (_vm.HasAppVolume ? $"  ·  {_vm.AppName} {Math.Round(_vm.AppVolume)}%" : "");
        SessionsText.Text = "Media sessions: " + string.Join(", ", _vm.Sessions.Select(s => s.Name).DefaultIfEmpty("none"));

        // So the check can be read from startup.log too.
        string state = $"{_vm.DisplayTitle} | {_vm.Artist} | {_vm.AppName} | playing={_vm.IsPlaying}";
        if (state != _loggedTitle)
        {
            _loggedTitle = state;
            Log.Trace($"Now playing: {state}");
        }
    }
}
