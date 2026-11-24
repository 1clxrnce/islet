using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Islet;

public enum ThemeMode { System, Dark, Light, Black, AlbumArt }
public enum AccentSource { System, AlbumArt, Custom }
public enum IslandPosition { TopLeft, TopCenter, TopRight }

// User options, persisted as JSON in %AppData%\islet\settings.json (same file and keys as the WPF app).
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly object FileLock = new();
    private static System.Threading.Timer? _saveTimer;

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "islet");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    // General
    public bool AutoHide { get; set; } = true;
    public bool HotkeysEnabled { get; set; } = true;
    public int MonitorIndex { get; set; } = -1;          // -1 = primary monitor, else index into the display list
    public string? PreferredSessionId { get; set; }      // Pinned media app, or null to follow what plays

    // Appearance
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public AccentSource Accent { get; set; } = AccentSource.System;
    public string AccentColor { get; set; } = "#0078D4";
    public double BlurStrength { get; set; } = 0.6;      // 0 = solid, 1 = most see-through
    public double CornerRadius { get; set; } = 20;       // Panel corners; the pill stays a capsule
    public double AnimationSpeed { get; set; } = 1.0;    // 0.5 slow .. 2 fast
    public bool ReduceMotion { get; set; }

    // Island
    public double PillScale { get; set; } = 1.0;         // 0.8 .. 1.4
    public IslandPosition Position { get; set; } = IslandPosition.TopCenter;
    public double OffsetX { get; set; }                  // Fine-tune, DIPs (negative is left)
    public double OffsetY { get; set; }                  // Fine-tune, DIPs down from the top edge
    public bool FadeOnHover { get; set; } = true;

    // Expanded panel
    public bool ShowProgress { get; set; } = true;
    public bool ShowVolume { get; set; } = true;
    public bool ShowAppVolume { get; set; } = true;
    public bool ShowDevices { get; set; } = true;
    public bool ShowSystemPage { get; set; } = true;
    public int LastPage { get; set; }

    // Battery
    public bool BatteryAlerts { get; set; } = true;

    // Our Reduce motion setting, or Windows' own "Animation effects" switched off.
    [JsonIgnore]
    public bool MotionReduced =>
#if DEBUG
        // Debug builds only: ISLET_FORCE_MOTION=1 shows animations even with reduce motion on, for testing.
        Environment.GetEnvironmentVariable("ISLET_FORCE_MOTION") is null &&
#endif
        (ReduceMotion || !WindowsAnimationsEnabled());

    private static Windows.UI.ViewManagement.UISettings? _uiSettings;

    private static bool WindowsAnimationsEnabled()
    {
        try { return (_uiSettings ??= new Windows.UI.ViewManagement.UISettings()).AnimationsEnabled; }
        catch (Exception) { return true; }
    }

    // Duration in ms for a base animation length, after speed and reduce-motion.
    public int Ms(int baseMs) => MotionReduced ? 0 : (int)(baseMs / Math.Clamp(AnimationSpeed, 0.5, 2));

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Settings could not be read, using defaults: {ex.Message}");
            Current = new AppSettings();
        }
        Current.Clamp();
    }

    public static void ResetToDefaults()
    {
        Current = new AppSettings();
        Save();
    }

    // The file is hand-editable; keep values inside what the UI can show.
    private void Clamp()
    {
        BlurStrength = Math.Clamp(BlurStrength, 0, 1);
        CornerRadius = Math.Clamp(CornerRadius, 8, 32);
        AnimationSpeed = Math.Clamp(AnimationSpeed, 0.5, 2);
        PillScale = Math.Clamp(PillScale, 0.8, 1.4);
        OffsetX = Math.Clamp(OffsetX, -200, 200);
        OffsetY = Math.Clamp(OffsetY, 0, 60);
        LastPage = Math.Clamp(LastPage, 0, 1);
    }

    // Writes once, half a second after the last call; a slider drag becomes one write.
    public static void SaveSoon()
    {
        _saveTimer ??= new System.Threading.Timer(_ => Save());
        _saveTimer.Change(500, System.Threading.Timeout.Infinite);
    }

    public static void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Current, JsonOptions);
            lock (FileLock)
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, json);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Settings could not be saved: {ex.Message}");
        }
    }
}
