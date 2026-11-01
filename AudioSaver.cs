using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepeatDesktop;

/// <summary>Save audio (Monkey's picks 2026-11-01, mockups/save-audio-options.html:
/// S1 icon button, MP3 by default, tools fetched on first use). yt-dlp fetches
/// the audio and ffmpeg converts it; neither ships with the app, so the release
/// stays small. They are downloaded once from yt-dlp's own GitHub releases into
/// %LOCALAPPDATA%\RepeatDesktop\tools, each checked against the release's
/// published SHA-256 before it is kept. An ffmpeg already on PATH is used as is.
/// yt-dlp updates itself before a save once it is a week old: YouTube changes
/// often and an old yt-dlp stops working.
///
/// Saves run one at a time, in the order asked. The page polls Status() while
/// something is running. Saved files are remembered in saved-audio.json (video
/// id -> file); a file deleted by hand simply stops showing as saved.</summary>
sealed class AudioSaver(Settings settings)
{
    const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
    const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/";
    const string FfmpegZip = "ffmpeg-master-latest-win64-gpl-shared.zip";
    public static readonly string[] Formats = ["mp3", "m4a", "opus"];
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    readonly string tools = Settings.DataDir("tools");
    readonly string savedFile = Path.Combine(Settings.Dir, "saved-audio.json");
    readonly Lock gate = new();
    readonly List<Job> queue = [];
    readonly Dictionary<string, Job> jobs = [];      // this run's saves, by video id
    bool running;
    Process? current;                                // the yt-dlp running now, stopped when the app closes
    string toolsNote = "";                           // "Getting the audio tools… 40%" while that runs
    int toolsPct;

    sealed class Job
    {
        public required string Id, Title, Sub;
        public string State = "waiting";             // waiting | saving | done | error
        public int Pct;
        public string File = "", Error = "";
        public long Bytes;
        public string Format = "";
    }

    public string Folder => string.IsNullOrWhiteSpace(settings.AudioFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "On repeat")
        : settings.AudioFolder;

    // ---------- what the page sees ----------

    public JsonObject Status()
    {
        var saved = LoadSaved();
        lock (gate)
        {
            var js = new JsonObject();
            foreach (var j in jobs.Values)
                js[j.Id] = new JsonObject
                {
                    ["state"] = j.State, ["pct"] = j.Pct, ["error"] = j.Error,
                    ["title"] = j.Title, ["format"] = j.Format, ["bytes"] = j.Bytes,
                };
            return new JsonObject
            {
                ["format"] = settings.AudioFormat,
                ["chosen"] = settings.AudioChosen,
                ["folder"] = Folder,
                ["busy"] = running,
                ["tools"] = toolsNote.Length > 0 ? new JsonObject { ["note"] = toolsNote, ["pct"] = toolsPct } : null,
                ["saved"] = new JsonArray([.. saved.Keys.Select(k => (JsonNode)k)]),
                ["jobs"] = js,
            };
        }
    }

    public JsonObject SetFormat(string format)
    {
        if (!Formats.Contains(format)) throw new ArgumentException("bad format");
        settings.AudioFormat = format;
        settings.AudioChosen = true;
        settings.Save();
        return Status();
    }

    /// <summary>Queues saves. Songs already saved (their file still there) or
    /// already queued are skipped; sub is a playlist's name, saved into its own
    /// subfolder.</summary>
    public JsonObject Save(IEnumerable<(string id, string title)> songs, string sub)
    {
        var saved = LoadSaved();
        lock (gate)
        {
            foreach (var (id, title) in songs)
            {
                if (!RepeatApi.IsVideoId(id) || saved.ContainsKey(id)) continue;
                if (jobs.TryGetValue(id, out var old) && old.State is "waiting" or "saving") continue;
                var j = new Job { Id = id, Title = title, Sub = SafeName(sub) };
                jobs[id] = j;
                queue.Add(j);
            }
            if (!running && queue.Count > 0) { running = true; _ = Task.Run(Run); }
        }
        return Status();
    }

    /// <summary>Closing the app stops a save in progress (its half-written file
    /// is yt-dlp's .part, never mistaken for a saved song).</summary>
    public void Stop()
    {
        lock (gate)
        {
            queue.Clear();
            try { current?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>The saved file of a video, for "Show" (never a path from the page).</summary>
    public string? FileOf(string id) => LoadSaved().GetValueOrDefault(id);

    // ---------- the work ----------

    async Task Run()
    {
        while (true)
        {
            Job j;
            lock (gate)
            {
                if (queue.Count == 0) { running = false; return; }
                j = queue[0];
                queue.RemoveAt(0);
                j.State = "saving";
            }
            try
            {
                var (ytdlp, ffmpegDir) = await EnsureTools();
                await SaveOne(j, ytdlp, ffmpegDir);
                lock (gate) j.State = "done";
            }
            catch (Exception e)
            {
                lock (gate) { j.State = "error"; j.Error = e.Message; }
            }
            finally
            {
                lock (gate) { toolsNote = ""; toolsPct = 0; }
            }
        }
    }

    async Task SaveOne(Job j, string ytdlp, string? ffmpegDir)
    {
        var format = settings.AudioFormat;
        var dir = j.Sub.Length > 0 ? Path.Combine(Folder, j.Sub) : Folder;
        Directory.CreateDirectory(dir);
        lock (gate) j.Format = format;
        var args = new List<string>
        {
            "--no-playlist", "--no-colors", "--newline", "--no-mtime", "--windows-filenames",
            // M4A and Opus take YouTube's own stream of that kind, so it is only
            // repackaged, never re-encoded; MP3 is always a conversion
            "-f", format switch { "m4a" => "bestaudio[ext=m4a]/bestaudio", "opus" => "bestaudio[acodec=opus]/bestaudio", _ => "bestaudio/best" },
            "-x", "--audio-format", format, "--audio-quality", "0",
            "--embed-metadata", "--embed-thumbnail", "--convert-thumbnails", "jpg",
            "--progress-template", "download:PCT %(progress._percent_str)s",
            "--print", "after_move:FILE %(filepath)s",
            "-P", dir, "-o", "%(title)s.%(ext)s",
        };
        if (ffmpegDir is not null) args.AddRange(["--ffmpeg-location", ffmpegDir]);
        args.Add($"https://www.youtube.com/watch?v={j.Id}");

        var psi = new ProcessStartInfo(ytdlp)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";      // titles in any script reach us intact
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("yt-dlp didn't start");
        lock (gate) current = p;
        var file = "";
        var err = new List<string>();
        var errTask = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync() is { } line)
                if (line.StartsWith("ERROR:")) lock (err) err.Add(line[6..].Trim());
        });
        while (await p.StandardOutput.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("PCT ") && double.TryParse(line[4..].Trim().TrimEnd('%'),
                    System.Globalization.CultureInfo.InvariantCulture, out var pct))
                lock (gate) j.Pct = (int)Math.Clamp(pct * 0.95, 0, 95);    // the last 5% is converting
            else if (line.StartsWith("FILE ")) file = line[5..].Trim();
        }
        await p.WaitForExitAsync();
        await errTask;
        lock (gate) current = null;
        if (p.ExitCode != 0 || file.Length == 0 || !File.Exists(file))
            throw new InvalidOperationException(err.Count > 0 ? Clip(err[^1]) : $"yt-dlp stopped (exit {p.ExitCode})");
        lock (gate) { j.File = file; j.Pct = 100; j.Bytes = new FileInfo(file).Length; }
        Remember(j.Id, file);
    }

    // ---------- tools ----------

    async Task<(string ytdlp, string? ffmpegDir)> EnsureTools()
    {
        var ytdlp = Path.Combine(tools, "yt-dlp.exe");
        if (!File.Exists(ytdlp))
            await Fetch("yt-dlp.exe", YtDlpUrl + "yt-dlp.exe", YtDlpUrl + "SHA2-256SUMS", ytdlp, "yt-dlp", 0, 20);
        else if (DateTime.UtcNow - File.GetLastWriteTimeUtc(ytdlp) > TimeSpan.FromDays(7))
            await SelfUpdate(ytdlp);

        if (OnPath("ffmpeg.exe") is { } onPath && File.Exists(Path.Combine(onPath, "ffprobe.exe"))) return (ytdlp, null);
        var ffdir = Path.Combine(tools, "ffmpeg");
        if (!File.Exists(Path.Combine(ffdir, "ffmpeg.exe")) || !File.Exists(Path.Combine(ffdir, "ffprobe.exe")))
        {
            var zip = Path.Combine(tools, FfmpegZip);
            await Fetch(FfmpegZip, FfmpegUrl + FfmpegZip, FfmpegUrl + "checksums.sha256", zip, "ffmpeg", 20, 100);
            var tmp = ffdir + ".tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            using (var a = ZipFile.OpenRead(zip))
                foreach (var e in a.Entries)
                {
                    // only ffmpeg, ffprobe and the libraries they load (bin\*.dll)
                    var parts = e.FullName.Split('/');
                    if (parts.Length != 3 || parts[1] != "bin" || !(e.Name is "ffmpeg.exe" or "ffprobe.exe" || e.Name.EndsWith(".dll"))) continue;
                    e.ExtractToFile(Path.Combine(tmp, e.Name), true);
                }
            File.Delete(zip);
            if (Directory.Exists(ffdir)) Directory.Delete(ffdir, true);
            Directory.Move(tmp, ffdir);
        }
        return (ytdlp, ffdir);
    }

    /// <summary>Downloads url to dest, but keeps it only if its SHA-256 matches
    /// the line for name in the release's checksum file.</summary>
    async Task Fetch(string name, string url, string sumsUrl, string dest, string tool, int from, int to)
    {
        var sums = await Http.GetStringAsync(sumsUrl);
        var want = sums.Split('\n').Select(l => l.Trim().Split(' ', 2, StringSplitOptions.TrimEntries))
            .FirstOrDefault(x => x.Length == 2 && x[1].TrimStart('*') == name)?[0]
            ?? throw new InvalidOperationException($"no checksum published for {name}");
        using var r = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        r.EnsureSuccessStatusCode();
        var total = r.Content.Headers.ContentLength ?? 0;
        var tmp = dest + ".part";
        using (var sha = SHA256.Create())
        {
            await using (var src = await r.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[1 << 16];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    sha.TransformBlock(buf, 0, n, null, 0);
                    got += n;
                    lock (gate)
                    {
                        toolsNote = $"Getting the audio tools, once ({tool}, {got / 1_000_000} of {total / 1_000_000} MB)";
                        toolsPct = total > 0 ? from + (int)((to - from) * got / total) : from;
                    }
                }
            }
            sha.TransformFinalBlock([], 0, 0);
            if (!Convert.ToHexString(sha.Hash!).Equals(want, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                throw new InvalidOperationException($"{name} didn't match its published checksum, so it wasn't kept");
            }
        }
        File.Move(tmp, dest, overwrite: true);
    }

    /// <summary>yt-dlp -U checks its own update against the release's signed
    /// checksums. A failed update keeps the old one: it may still work.</summary>
    async Task SelfUpdate(string ytdlp)
    {
        lock (gate) toolsNote = "Updating yt-dlp";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ytdlp, "-U --no-colors") { UseShellExecute = false, CreateNoWindow = true });
            if (p is not null) await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (Exception e) when (e is TimeoutException or System.ComponentModel.Win32Exception) { }
        File.SetLastWriteTimeUtc(ytdlp, DateTime.UtcNow);     // next check in a week, updated or not
    }

    static string? OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault(d => File.Exists(Path.Combine(d.Trim(), exe)))?.Trim();

    // ---------- saved files ----------

    Dictionary<string, string> LoadSaved()
    {
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(savedFile)) ?? [];
            return d.Where(kv => File.Exists(kv.Value)).ToDictionary();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException) { return []; }
    }

    void Remember(string id, string file)
    {
        lock (gate)
        {
            var d = LoadSaved();
            d[id] = file;
            Directory.CreateDirectory(Settings.Dir);
            var tmp = savedFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, savedFile, overwrite: true);
        }
    }

    /// <summary>A playlist name as a folder name: no path tricks, no characters Windows refuses.</summary>
    static string SafeName(string s)
    {
        s = Regex.Replace(s ?? "", @"[\\/:*?""<>|\x00-\x1f]", "_").Trim().TrimEnd('.', ' ');
        return s is "" or "." or ".." ? "" : s.Length > 60 ? s[..60] : s;
    }

    static string Clip(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
