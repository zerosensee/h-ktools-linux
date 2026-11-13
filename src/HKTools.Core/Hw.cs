using HKTools.Media;

namespace HKTools;

public sealed class HwCaps
{
    public string FfmpegVersion { get; set; } = "";
    public List<string> Encoders { get; set; } = new();
    public List<string> Filters { get; set; } = new();
    public Dictionary<string, bool> Nvenc { get; set; } = new();
    public Dictionary<string, bool> Vaapi { get; set; } = new();
    public string VaapiDevice { get; set; } = "";
    public List<string> Gpus { get; set; } = new();

    public bool HasEncoder(string n) => Encoders.Contains(n);
    public bool NvencOk => Nvenc.Values.Any(v => v);
    public bool VaapiOk => Vaapi.Values.Any(v => v);
}

/// <summary>Аппаратное кодирование: NVENC (NVIDIA), VAAPI (AMD / Intel). Проверка тестовым кодированием + кэш.</summary>
public static class Hw
{
    static readonly object Lock = new();
    static HwCaps _caps;
    static readonly Dictionary<string, string> Vendors = new() { ["0x10de"] = "NVIDIA", ["0x1002"] = "AMD", ["0x8086"] = "Intel" };

    public static string[] RenderDevices() =>
        Directory.Exists("/dev/dri") ? Directory.GetFiles("/dev/dri", "renderD*").OrderBy(x => x).ToArray() : [];

    static string VendorOf(string dev)
    {
        try { return File.ReadAllText($"/sys/class/drm/{System.IO.Path.GetFileName(dev)}/device/vendor").Trim(); }
        catch { return ""; }
    }

    public static List<string> GpuVendors() =>
        RenderDevices().Select(d =>
        {
            var v = VendorOf(d);
            return $"{Vendors.GetValueOrDefault(v, v == "" ? "?" : v)} ({System.IO.Path.GetFileName(d)})";
        }).ToList();

    public static string PickVaapiDevice()
    {
        var cfg = Settings.Instance.GetStr("vaapi_device").Trim();
        if (cfg != "") return cfg;
        var devs = RenderDevices();
        return devs.FirstOrDefault(d => VendorOf(d) != "0x10de") ?? devs.FirstOrDefault() ?? "";
    }

    static List<string> FfList(string ffmpeg, string what)
    {
        var (_, o, _) = Proc.Capture([ffmpeg, "-hide_banner", $"-{what}"], 15000);
        var res = new List<string>();
        bool started = false;
        foreach (var raw in o.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("---")) { started = true; continue; }
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (what == "filters")
            {
                if (parts.Length >= 2 && parts[0].Length <= 4 && line.Contains("->")) res.Add(parts[1]);
                continue;
            }
            if (started && parts.Length >= 2) res.Add(parts[1]);
        }
        return res;
    }

    static bool TestEncode(IReadOnlyList<string> cmd) => Proc.Capture(cmd, 20000).Code == 0;

    static HwCaps Detect()
    {
        var caps = new HwCaps();
        var ffmpeg = Tools.Which("ffmpeg");
        if (ffmpeg == null) return caps;
        caps.FfmpegVersion = Tools.ProbeVersion("ffmpeg").Version;
        caps.Gpus = GpuVendors();
        var cacheFile = System.IO.Path.Combine(Paths.CacheDir, "hwcaps-cs.json");
        var key = $"{ffmpeg}|{caps.FfmpegVersion}|{string.Join(",", caps.Gpus)}|{Settings.Instance.GetStr("vaapi_device")}";
        try
        {
            if (File.Exists(cacheFile))
            {
                var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cacheFile));
                if (doc.RootElement.GetProperty("key").GetString() == key)
                    return System.Text.Json.JsonSerializer.Deserialize<HwCaps>(doc.RootElement.GetProperty("caps").GetRawText());
            }
        }
        catch { }
        caps.Encoders = FfList(ffmpeg, "encoders");
        caps.Filters = FfList(ffmpeg, "filters");
        string[] src = ["-f", "lavfi", "-i", "color=c=black:s=320x240:r=25:d=0.4"];
        foreach (var enc in new[] { "hevc_nvenc", "h264_nvenc", "av1_nvenc" })
            if (caps.HasEncoder(enc))
                caps.Nvenc[enc] = TestEncode([ffmpeg, "-hide_banner", "-loglevel", "error", .. src, "-c:v", enc, "-f", "null", "-"]);
        var dev = PickVaapiDevice();
        caps.VaapiDevice = dev;
        if (dev != "")
            foreach (var enc in new[] { "hevc_vaapi", "h264_vaapi", "av1_vaapi" })
                if (caps.HasEncoder(enc))
                    caps.Vaapi[enc] = TestEncode([ffmpeg, "-hide_banner", "-loglevel", "error", "-vaapi_device", dev, .. src,
                        "-vf", "format=nv12,hwupload", "-c:v", enc, "-f", "null", "-"]);
        try
        {
            File.WriteAllText(cacheFile, System.Text.Json.JsonSerializer.Serialize(new { key, caps }));
        }
        catch { }
        Log.Info($"Аппаратное кодирование: NVENC={Fmt(caps.Nvenc)}, VAAPI={Fmt(caps.Vaapi)} ({(dev == "" ? "нет устройства" : dev)})", "Оборудование");
        return caps;
    }

    static string Fmt(Dictionary<string, bool> d)
    {
        var ok = d.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        return ok.Count > 0 ? string.Join(",", ok) : "нет";
    }

    public static HwCaps Caps(bool refresh = false)
    {
        lock (Lock)
        {
            if (refresh)
            {
                try { File.Delete(System.IO.Path.Combine(Paths.CacheDir, "hwcaps-cs.json")); } catch { }
                _caps = null;
            }
            return _caps ??= Detect();
        }
    }

    public static bool HasFilter(string name) => Caps().Filters.Contains(name);
}
