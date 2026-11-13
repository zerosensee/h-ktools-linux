using System.Globalization;
using System.Text.Json;

namespace HKTools.Media;

public sealed class Track
{
    public int Index;            // абсолютный индекс потока (0:N)
    public string Type = "";     // video | audio | subtitle | attachment | data
    public int TypeIndex;        // индекс внутри типа (0:a:N)
    public string Codec = "", CodecLong = "", Profile = "", Language = "und", Title = "";
    public bool Default, Forced, HearingImpaired, Commentary, Original;
    public int Channels, SampleRate, Width, Height, BitDepth;
    public string ChannelLayout = "", PixFmt = "", Filename = "", Mimetype = "";
    public double Fps, Duration;
    public long Bitrate;

    static readonly HashSet<string> FontMimes = new()
    {
        "application/x-truetype-font", "application/x-font-ttf", "application/x-font-otf", "font/ttf", "font/otf",
        "font/sfnt", "application/vnd.ms-opentype", "application/font-sfnt", "application/x-font-opentype",
        "font/collection", "application/x-font", "font/woff", "font/woff2",
    };
    static readonly string[] FontExts = [".ttf", ".otf", ".ttc", ".otc", ".woff", ".woff2", ".fon", ".pfb"];
    public static readonly HashSet<string> BitmapSubs = new() { "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "xsub" };

    public bool IsFont => Type == "attachment" &&
                          (FontMimes.Contains(Mimetype.ToLowerInvariant()) || FontExts.Any(e => Filename.ToLowerInvariant().EndsWith(e)));
    public bool IsBitmapSub => Type == "subtitle" && BitmapSubs.Contains(Codec);
    public string Resolution => Width > 0 && Height > 0 ? $"{Width}x{Height}" : "";

    public static string TypeRu(string t) => t switch
    {
        "video" => "Видео", "audio" => "Аудио", "subtitle" => "Субтитры", "attachment" => "Вложение", _ => "Данные",
    };

    public string Describe()
    {
        var b = new List<string>();
        switch (Type)
        {
            case "video":
                b.Add(Codec.ToUpperInvariant());
                if (Resolution != "") b.Add(Resolution);
                if (Fps > 0) b.Add(Fps.ToString("0.###", CultureInfo.InvariantCulture) + " fps");
                if (BitDepth > 8) b.Add($"{BitDepth}-bit");
                break;
            case "audio":
                b.Add(Codec == "dts" && Profile != "" ? Profile : Codec.ToUpperInvariant());
                if (ChannelLayout != "" || Channels > 0) b.Add(ChannelLayout != "" ? ChannelLayout : $"{Channels} кан.");
                if (SampleRate > 0) b.Add((SampleRate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " кГц");
                if (Bitrate > 0) b.Add($"{Bitrate / 1000} кбит/с");
                break;
            case "subtitle": b.Add(Codec.ToUpperInvariant()); break;
            case "attachment": b.Add(Filename != "" ? Filename : Mimetype); break;
        }
        if (Language != "" && Language != "und") b.Add(Language);
        if (Title != "") b.Add($"«{Title}»");
        var flags = new List<string>();
        if (Default) flags.Add("по умолч.");
        if (Forced) flags.Add("принуд.");
        if (flags.Count > 0) b.Add("[" + string.Join(", ", flags) + "]");
        return string.Join(" · ", b.Where(x => x != ""));
    }
}

public sealed class MediaInfo
{
    public string Path = "";
    public double Duration;
    public string FormatName = "", Title = "";
    public long Size, Bitrate;
    public int Chapters;
    public List<Track> Tracks = new();

    public List<Track> OfType(string k) => Tracks.Where(t => t.Type == k).ToList();

    public List<Track> Video
    {
        get
        {
            var v = OfType("video");
            return v.Count == 1 ? v : v.Where(t => t.Codec is not ("mjpeg" or "png" or "bmp")).ToList();
        }
    }

    public List<Track> Audio => OfType("audio");
    public List<Track> Subtitles => OfType("subtitle");
    public List<Track> Attachments => OfType("attachment");
    public List<Track> Fonts => Attachments.Where(t => t.IsFont).ToList();
    public List<Track> MediaTracks => Tracks.Where(t => t.Type is "video" or "audio" or "subtitle").ToList();
    public Track TrackAt(int index) => Tracks.FirstOrDefault(t => t.Index == index);
    public string Signature => string.Join("|", MediaTracks.Select(t => $"{t.Type}:{t.Codec}:{t.Language}"));
}

public static class Probe
{
    static readonly object CacheLock = new();
    static readonly Dictionary<string, MediaInfo> Cache = new();

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static double Dbl(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    static int Int(JsonElement e, string name) => (int)Dbl(e, name);

    static Dictionary<string, string> Tags(JsonElement e)
    {
        var d = new Dictionary<string, string>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object)
            foreach (var p in t.EnumerateObject())
                d[p.Name.ToLowerInvariant()] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
        return d;
    }

    static bool Disp(JsonElement s, string name) =>
        s.TryGetProperty("disposition", out var d) && d.ValueKind == JsonValueKind.Object &&
        d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0;

    static double ParseFps(JsonElement s)
    {
        foreach (var key in new[] { "avg_frame_rate", "r_frame_rate" })
        {
            var v = Str(s, key);
            var parts = v.Split('/');
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0)
            {
                var f = a / b;
                if (f > 0 && f < 1000) return f;
            }
        }
        return 0;
    }

    static int BitDepthOf(JsonElement s)
    {
        var raw = Int(s, "bits_per_raw_sample");
        if (raw > 0) return raw;
        var pix = Str(s, "pix_fmt");
        foreach (var d in new[] { 16, 12, 10 }) if (pix.Contains($"p{d}")) return d;
        return pix != "" ? 8 : 0;
    }

    /// <summary>Проанализировать файл (кэш по пути, размеру и времени изменения).</summary>
    public static MediaInfo Run(string path, bool useCache = true)
    {
        FileInfo fi;
        try { fi = new FileInfo(path); if (!fi.Exists) throw new FileNotFoundException(); }
        catch (Exception ex) { throw new InvalidOperationException($"Файл недоступен: {ex.Message}"); }
        var key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        if (useCache) lock (CacheLock) if (Cache.TryGetValue(key, out var c)) return c;

        var ffprobe = Tools.Require("ffprobe");
        var (code, o, e) = Proc.Capture([ffprobe, "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", path], 120000);
        if (code != 0 || string.IsNullOrWhiteSpace(o))
            throw new InvalidOperationException("ffprobe не смог прочитать файл: " + (e.Length > 400 ? e[..400] : e).Trim());
        using var doc = JsonDocument.Parse(o);
        var root = doc.RootElement;
        root.TryGetProperty("format", out var fmt);
        var info = new MediaInfo
        {
            Path = path,
            Duration = Dbl(fmt, "duration"),
            FormatName = Str(fmt, "format_name"),
            Size = (long)Dbl(fmt, "size") is var sz && sz > 0 ? sz : fi.Length,
            Bitrate = (long)Dbl(fmt, "bit_rate"),
            Title = Tags(fmt).GetValueOrDefault("title", ""),
            Chapters = root.TryGetProperty("chapters", out var ch) && ch.ValueKind == JsonValueKind.Array ? ch.GetArrayLength() : 0,
        };
        var counters = new Dictionary<string, int>();
        if (root.TryGetProperty("streams", out var streams))
            foreach (var s in streams.EnumerateArray())
            {
                var kind = Str(s, "codec_type");
                if (kind == "") kind = "data";
                var tags = Tags(s);
                counters.TryGetValue(kind, out var ti);
                counters[kind] = ti + 1;
                var dur = Dbl(s, "duration");
                if (dur <= 0 && tags.TryGetValue("duration", out var dt) && TimeSpan.TryParse(dt.Split('.')[0], out var ts))
                    dur = ts.TotalSeconds;
                long br = (long)Dbl(s, "bit_rate");
                if (br == 0 && long.TryParse(tags.GetValueOrDefault("bps") ?? tags.GetValueOrDefault("bps-eng"), out var bps)) br = bps;
                var codec = Str(s, "codec_name");
                info.Tracks.Add(new Track
                {
                    Index = s.TryGetProperty("index", out var ix) ? ix.GetInt32() : info.Tracks.Count,
                    Type = kind,
                    TypeIndex = ti,
                    Codec = codec == "" && kind == "attachment" ? "ttf" : codec,
                    CodecLong = Str(s, "codec_long_name"),
                    Profile = Str(s, "profile"),
                    Language = string.IsNullOrEmpty(tags.GetValueOrDefault("language")) ? "und" : tags["language"],
                    Title = tags.GetValueOrDefault("title", ""),
                    Default = Disp(s, "default"), Forced = Disp(s, "forced"), HearingImpaired = Disp(s, "hearing_impaired"),
                    Commentary = Disp(s, "comment"), Original = Disp(s, "original"),
                    Channels = Int(s, "channels"), ChannelLayout = Str(s, "channel_layout"), SampleRate = Int(s, "sample_rate"),
                    Width = Int(s, "width"), Height = Int(s, "height"),
                    Fps = kind == "video" ? ParseFps(s) : 0,
                    PixFmt = Str(s, "pix_fmt"),
                    BitDepth = kind == "video" ? BitDepthOf(s) : Int(s, "bits_per_raw_sample"),
                    Bitrate = br, Duration = dur,
                    Filename = tags.GetValueOrDefault("filename", ""), Mimetype = tags.GetValueOrDefault("mimetype", ""),
                });
            }
        if (info.Duration <= 0 && info.Tracks.Count > 0) info.Duration = info.Tracks.Max(t => t.Duration);
        lock (CacheLock)
        {
            if (Cache.Count > 256) Cache.Clear();
            Cache[key] = info;
        }
        return info;
    }

    /// <summary>mkvmerge -J (идентификаторы дорожек mkvmerge).</summary>
    public static JsonDocument MkvIdentify(string path)
    {
        var mkvmerge = Tools.Require("mkvmerge");
        var (code, o, e) = Proc.Capture([mkvmerge, "-J", path], 60000);
        if (code is not (0 or 1)) throw new InvalidOperationException("mkvmerge не смог прочитать файл: " + e.Trim());
        return JsonDocument.Parse(o);
    }

    public static List<(int Id, string Type)> MkvTracks(string path)
    {
        using var doc = MkvIdentify(path);
        var res = new List<(int, string)>();
        if (doc.RootElement.TryGetProperty("tracks", out var tr))
            foreach (var t in tr.EnumerateArray())
                res.Add((t.GetProperty("id").GetInt32(), t.GetProperty("type").GetString()));
        return res;
    }

    static readonly Dictionary<string, string> IsoLang = new()
    {
        ["rus"] = "ru", ["eng"] = "en", ["jpn"] = "ja", ["spa"] = "es", ["fra"] = "fr", ["fre"] = "fr", ["deu"] = "de",
        ["ger"] = "de", ["ita"] = "it", ["por"] = "pt", ["zho"] = "zh", ["chi"] = "zh", ["ara"] = "ar", ["kor"] = "ko",
        ["pol"] = "pl", ["ukr"] = "uk", ["hin"] = "hi", ["tur"] = "tr", ["heb"] = "he", ["vie"] = "vi", ["tha"] = "th",
        ["nld"] = "nl", ["dut"] = "nl", ["swe"] = "sv", ["dan"] = "da", ["fin"] = "fi", ["nob"] = "no", ["nor"] = "no",
        ["ces"] = "cs", ["cze"] = "cs", ["hun"] = "hu", ["ron"] = "ro", ["rum"] = "ro", ["ell"] = "el", ["gre"] = "el",
        ["ind"] = "id", ["msa"] = "ms", ["may"] = "ms", ["bul"] = "bg", ["srp"] = "sr",
    };

    public static string NormalizeLang(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang) || lang.Equals("und", StringComparison.OrdinalIgnoreCase)) return "und";
        var b = lang.Split('-')[0].Trim().ToLowerInvariant();
        return IsoLang.GetValueOrDefault(b, b);
    }

    static readonly Dictionary<string, string> RawExt = new()
    {
        ["ac3"] = ".ac3", ["eac3"] = ".eac3", ["dts"] = ".dts", ["aac"] = ".aac", ["aac_latm"] = ".aac", ["opus"] = ".opus",
        ["flac"] = ".flac", ["vorbis"] = ".ogg", ["mp3"] = ".mp3", ["mp2"] = ".mp2", ["mp1"] = ".mp2", ["truehd"] = ".thd",
        ["mlp"] = ".mlp", ["alac"] = ".m4a", ["wavpack"] = ".wv", ["ape"] = ".mka", ["tta"] = ".tta",
        ["subrip"] = ".srt", ["srt"] = ".srt", ["ass"] = ".ass", ["ssa"] = ".ass", ["webvtt"] = ".vtt", ["mov_text"] = ".srt",
        ["text"] = ".srt", ["hdmv_pgs_subtitle"] = ".sup", ["dvd_subtitle"] = ".mks", ["dvb_subtitle"] = ".mks",
        ["h264"] = ".h264", ["hevc"] = ".h265", ["mpeg2video"] = ".m2v", ["mpeg1video"] = ".m1v", ["vc1"] = ".vc1",
        ["vp8"] = ".ivf", ["vp9"] = ".ivf", ["av1"] = ".ivf", ["mjpeg"] = ".mjpeg", ["prores"] = ".mov", ["mpeg4"] = ".m4v",
    };

    public static readonly Dictionary<string, string> SubConvert = new() { ["mov_text"] = "srt", ["text"] = "srt" };

    public static string RawExtension(Track t)
    {
        if (t.Codec.StartsWith("pcm_")) return ".wav";
        if (RawExt.TryGetValue(t.Codec, out var e)) return e;
        return t.Type switch { "video" => ".mkv", "audio" => ".mka", "subtitle" => ".mks", _ => ".bin" };
    }
}
