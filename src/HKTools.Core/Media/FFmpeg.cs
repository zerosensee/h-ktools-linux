using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HKTools.Media;

public sealed class FfProgress
{
    public double Percent, TimeS;
    public double? Speed, Fps;
    public string Bitrate = "", Eta = "";

    public string Label(string prefix = "")
    {
        var p = new List<string>();
        if (prefix != "") p.Add(prefix);
        p.Add(Percent > 0 ? $"{Percent:0.0}%" : FFmpeg.FmtTime(TimeS));
        if (Fps is > 0) p.Add($"{Fps:0} fps");
        if (Speed is > 0) p.Add($"{Speed:0.00}x");
        if (Eta != "") p.Add($"ETA {Eta}");
        return string.Join(" · ", p);
    }
}

/// <summary>Обёртки над ffmpeg: прогресс (-progress pipe:1), поиск чёрных полос, вложения.</summary>
public static partial class FFmpeg
{
    /// <summary>Стерео-даунмикс с коэффициентами HandBrake (центр/тылы 0.7071, LFE отбрасывается).</summary>
    public const string StereoDownmixPan = "pan=stereo|FL=FL+0.7071*FC+0.7071*BL+0.7071*SL|FR=FR+0.7071*FC+0.7071*BR+0.7071*SR";

    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Bin => Tools.Require("ffmpeg");

    public static string FmtTime(double sec)
    {
        var s = (int)Math.Max(0, sec);
        return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60:00}:{s % 60:00}";
    }

    public static ProcResult Run(IEnumerable<string> args, CancelToken token, double duration = 0, Action<FfProgress> onProgress = null,
        bool overwrite = true, string cwd = null, string loglevel = "error", string source = "FFmpeg")
    {
        var cmd = new List<string> { Bin, "-hide_banner", "-nostdin", "-loglevel", loglevel, "-progress", "pipe:1", "-nostats", overwrite ? "-y" : "-n" };
        cmd.AddRange(args);
        var state = new Dictionary<string, string>();
        var sw = Stopwatch.StartNew();
        long lastEmit = -1000;
        void OnLine(string line)
        {
            var i = line.IndexOf('=');
            if (i <= 0) return;
            var key = line[..i].Trim();
            var val = line[(i + 1)..].Trim();
            state[key] = val;
            if (key != "progress" || onProgress == null) return;
            var now = sw.ElapsedMilliseconds;
            if (val != "end" && now - lastEmit < 250) return;
            lastEmit = now;
            var p = new FfProgress();
            var us = state.GetValueOrDefault("out_time_us") ?? state.GetValueOrDefault("out_time_ms");
            if (long.TryParse(us, out var u)) p.TimeS = Math.Max(0, u / 1e6);
            if (duration > 0) p.Percent = Math.Min(100, p.TimeS / duration * 100);
            if (double.TryParse(state.GetValueOrDefault("speed", "").TrimEnd('x'), NumberStyles.Float, Inv, out var sp) && sp > 0) p.Speed = sp;
            if (double.TryParse(state.GetValueOrDefault("fps", "0"), NumberStyles.Float, Inv, out var f) && f > 0) p.Fps = f;
            var br = state.GetValueOrDefault("bitrate", "");
            p.Bitrate = br is "" or "N/A" ? "" : br;
            if (duration > 0 && p.Speed is > 0) p.Eta = FmtTime((duration - p.TimeS) / p.Speed.Value);
            else if (duration > 0 && p.Percent > 1) p.Eta = FmtTime(now / 1000.0 / p.Percent * (100 - p.Percent));
            if (val == "end" && duration > 0) p.Percent = 100;
            onProgress(p);
        }
        return Proc.Run(cmd, token, onStdout: OnLine, cwd: cwd, source: source);
    }

    /// <summary>Экранирование пути для опций фильтров (subtitles=filename='…').</summary>
    public static string FilterEscape(string path) =>
        path.Replace("\\", "\\\\").Replace(":", "\\:").Replace("'", "\\'").Replace(",", "\\,")
            .Replace("[", "\\[").Replace("]", "\\]").Replace(";", "\\;");

    [GeneratedRegex(@"crop=(\d+):(\d+):(\d+):(\d+)")]
    private static partial Regex CropRe();

    public static (int W, int H, int X, int Y)? DetectCrop(string path, double seek, int frames, double limit, int round,
        int skip, int reset, string mode, CancelToken token)
    {
        var prm = new List<string> { $"limit={limit.ToString("0.0000", Inv)}", $"round={Math.Max(1, round)}", $"reset={reset}" };
        if (skip > 0) prm.Add($"skip={skip}");
        if (!string.IsNullOrEmpty(mode) && mode != "black") prm.Add($"mode={mode}");
        var cmd = new List<string> { Bin, "-hide_banner", "-nostdin", "-loglevel", "info" };
        if (seek > 0) cmd.AddRange(["-ss", seek.ToString("0.000", Inv)]);
        cmd.AddRange(["-i", path, "-map", "0:v:0", "-frames:v", frames.ToString(), "-vf", "cropdetect=" + string.Join(":", prm),
            "-an", "-sn", "-f", "null", "-"]);
        (int, int, int, int)? last = null;
        var res = Proc.Run(cmd, token, onStderr: l =>
        {
            var m = CropRe().Match(l);
            if (m.Success) last = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value));
        }, source: "cropdetect");
        return res.Cancelled ? null : last;
    }

    public static List<string> DumpAttachments(string path, string outDir, CancelToken token)
    {
        Directory.CreateDirectory(outDir);
        var before = Directory.GetFiles(outDir).ToHashSet();
        Proc.Run([Bin, "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-dump_attachment:t", "", "-i", path, "-t", "0", "-f", "null", "-"],
            token, cwd: outDir, source: "FFmpeg");
        return Directory.GetFiles(outDir).Where(f => !before.Contains(f)).OrderBy(f => f).ToList();
    }

    public static bool DumpAttachment(string path, int streamIndex, string outFile, CancelToken token)
    {
        Proc.Run([Bin, "-hide_banner", "-nostdin", "-loglevel", "error", "-y", $"-dump_attachment:{streamIndex}", outFile, "-i", path,
            "-t", "0", "-f", "null", "-"], token, source: "FFmpeg");
        return File.Exists(outFile) && new FileInfo(outFile).Length > 0;
    }
}
