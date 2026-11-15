using System.Text.Json;

namespace RepeatDesktop;

/// <summary>Per-PC settings in %APPDATA%\RepeatDesktop\settings.json: where each
/// window mode was left, and the YouTube Data API key if one was added (it
/// never leaves this PC and is never sent to the page).</summary>
sealed class Settings
{
    public string YoutubeApiKey { get; set; } = "";
    public bool Pinned { get; set; } = true;
    public int[]? Full { get; set; }          // x, y, w, h (device pixels)
    public int[]? Mini { get; set; }          // x, y
    // Save audio: mp3 | m4a | opus, the folder ("" = Music\On repeat), and
    // whether the picker was ever confirmed (the first save shows it)
    public string AudioFormat { get; set; } = "mp3";
    public string AudioFolder { get; set; } = "";
    public bool AudioChosen { get; set; }
    // Save video (V1, Monkey's pick 2026-11-15, mockups/save-video-options.html):
    // MP4 up to 720 | 1080 | best, and the folder ("" = Videos\On repeat)
    public string VideoQuality { get; set; } = "1080";
    public string VideoFolder { get; set; } = "";

    /// <summary>REPEAT_DESKTOP_DATA moves everything (settings, counts, the
    /// browser profile) to another folder: tests/check.js runs against a
    /// throwaway one, so it never writes into Monkey's real counts.</summary>
    static string? Override => Environment.GetEnvironmentVariable("REPEAT_DESKTOP_DATA") is { Length: > 0 } d ? d : null;

    public static string Dir => Override ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RepeatDesktop");
    static string File => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DataDir(string name)
    {
        var d = Path.Combine(Override ?? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "RepeatDesktop"), name);
        Directory.CreateDirectory(d);
        return d;
    }

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) ?? new();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = File + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        System.IO.File.Move(tmp, File, overwrite: true);
    }
}
