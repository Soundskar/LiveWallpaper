using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace LiveWallpaper;

public class Settings
{
    public string? LastFile { get; set; }
    public int Volume { get; set; } = 0; // muted by default
    public bool PetEnabled { get; set; } = false;
    public string PetName { get; set; } = "Pixel";
    public int PetHappiness { get; set; } = 70;
    public int PetHunger { get; set; } = 30;   // 0 = full, 100 = starving
    public int PetEnergy { get; set; } = 80;
    public bool PetReminders { get; set; } = true;
    public DateTime PetLastSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime PetBornUtc { get; set; } = DateTime.UtcNow;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveWallpaper");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* ignore corrupt settings */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    // ---- Run at Windows startup (HKCU Run key) ----
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "LiveWallpaper";

    public static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(AppName) != null;
    }

    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key == null) return;
        if (enabled)
        {
            string exe = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(exe))
                key.SetValue(AppName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }
}
