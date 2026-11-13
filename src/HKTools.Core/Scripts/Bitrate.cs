namespace HKTools.Scripts;

public sealed class BitrateData
{
    public string Path = "", StreamLabel = "", Codec = "";
    public double Duration, Fps;
    public List<double> PerSecond = new();   // Мбит/с
    public List<double> Keyframes = new();
    public long Packets, TotalBytes;

    public double Mean => PerSecond.Count > 0 ? PerSecond.Average() : 0;
    public double Peak => PerSecond.Count > 0 ? PerSecond.Max() : 0;
    public double Minimum => PerSecond.Count > 0 ? PerSecond.Min() : 0;
    public double Stdev
    {
        get
        {
            if (PerSecond.Count < 2) return 0;
            var m = Mean;
            return Math.Sqrt(PerSecond.Sum(x => (x - m) * (x - m)) / (PerSecond.Count - 1));
        }
    }
    public double Gop => Keyframes.Count < 2 ? 0 : (Keyframes[^1] - Keyframes[0]) / (Keyframes.Count - 1);
}

/// <summary>Минимальный 2D-холст: реализуется через Cairo в графическом приложении.</summary>
public interface IChartCanvas
{
    void Rgba(double r, double g, double b, double a = 1);
    void Paint();
    void MoveTo(double x, double y);
    void LineTo(double x, double y);
    void Rect(double x, double y, double w, double h);
    void ClosePath();
    void Stroke();
    void Fill(bool preserve = false);
    void Clip();
    void Save();
    void Restore();
    void Dash(params double[] d);
    void LineWidth(double w);
    void FontSize(double s);
    double TextWidth(string s);
    void ShowText(string s);
}

public static class BitrateChart
{
    /// <summary>Сохранение PNG (задаётся приложением с Cairo).</summary>
    public static Action<BitrateData, string, int, int, bool> PngRenderer;

    public static double NiceStep(double maxv, int ticks = 5)
    {
        if (maxv <= 0) return 1;
        var raw = maxv / ticks;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
            if (raw <= m * mag) return m * mag;
        return 10 * mag;
    }

    static string G(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    static string F2(double v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Отрисовка графика битрейта (используется и в окне, и для PNG).</summary>
    public static void Draw(IChartCanvas cr, double width, double height, BitrateData data, bool dark = true, bool title = false,
        (double T0, double T1)? view = null, double? hoverX = null, (double R, double G, double B)? accent = null)
    {
        var (ar, ag, ab) = accent ?? (0.11, 0.62, 0.89);
        (double, double, double) fg = dark ? (0.92, 0.92, 0.94) : (0.12, 0.12, 0.14);
        (double, double, double) bg = dark ? (0.12, 0.12, 0.14) : (0.98, 0.98, 0.98);
        void Fg(double a = 1) => cr.Rgba(fg.Item1, fg.Item2, fg.Item3, a);
        void Bg(double a = 1) => cr.Rgba(bg.Item1, bg.Item2, bg.Item3, a);
        void Grid() { if (dark) cr.Rgba(1, 1, 1, 0.08); else cr.Rgba(0, 0, 0, 0.08); }
        Bg();
        cr.Paint();
        double left = 64, right = 20, top = title ? 48 : 20, bottom = 36;
        double pw = Math.Max(10, width - left - right), ph = Math.Max(10, height - top - bottom);
        var series = data.PerSecond;
        int n = series.Count;
        if (n == 0) return;
        var (t0, t1) = view ?? (0, n);
        t1 = Math.Max(t0 + 1, t1);
        int i0 = Math.Max(0, (int)t0), i1 = Math.Min(n, (int)Math.Ceiling(t1));
        double peak = i1 > i0 ? series.Skip(i0).Take(i1 - i0).Max() : 0;
        if (peak <= 0) peak = 1;
        var step = NiceStep(peak * 1.1);
        var ymax = step * Math.Ceiling(peak * 1.1 / step);
        cr.FontSize(12);
        cr.LineWidth(1);
        for (double v = 0; v <= ymax + 1e-9; v += step)
        {
            var y = top + ph - v / ymax * ph;
            Grid(); cr.MoveTo(left, y); cr.LineTo(left + pw, y); cr.Stroke();
            Fg();
            var label = G(v);
            cr.MoveTo(left - 8 - cr.TextWidth(label), y + 4);
            cr.ShowText(label);
        }
        var span = t1 - t0;
        var tstep = NiceStep(span, 8);
        for (var t = Math.Ceiling(t0 / tstep) * tstep; t <= t1; t += tstep)
        {
            var x = left + (t - t0) / span * pw;
            Grid(); cr.MoveTo(x, top); cr.LineTo(x, top + ph); cr.Stroke();
            Fg();
            int s = (int)t;
            var label = span > 600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
            cr.MoveTo(x - cr.TextWidth(label) / 2, top + ph + 20);
            cr.ShowText(label);
        }
        if (data.Keyframes.Count > 0 && span < 900)
        {
            cr.Rgba(1, 0.65, 0.2, 0.25);
            foreach (var k in data.Keyframes)
                if (k >= t0 && k <= t1)
                {
                    var x = left + (k - t0) / span * pw;
                    cr.MoveTo(x, top); cr.LineTo(x, top + ph); cr.Stroke();
                }
        }
        cr.Save();
        cr.Rect(left, top, pw, ph);
        cr.Clip();
        cr.MoveTo(left + (i0 - t0) / span * pw, top + ph);
        for (int i = i0; i < i1; i++) cr.LineTo(left + (i - t0 + 0.5) / span * pw, top + ph - series[i] / ymax * ph);
        cr.LineTo(left + (i1 - t0) / span * pw, top + ph);
        cr.ClosePath();
        cr.Rgba(ar, ag, ab, 0.28);
        cr.Fill(true);
        cr.Rgba(ar, ag, ab, 0.95);
        cr.LineWidth(1.4);
        cr.Stroke();
        var my = top + ph - data.Mean / ymax * ph;
        cr.Dash(6, 4);
        cr.Rgba(0.95, 0.43, 0.3, 0.9);
        cr.MoveTo(left, my); cr.LineTo(left + pw, my); cr.Stroke();
        cr.Dash();
        cr.Restore();
        Fg();
        cr.MoveTo(8, top > 24 ? top - 6 : 14);
        cr.FontSize(11);
        cr.ShowText("Мбит/с");
        if (title)
        {
            cr.FontSize(16);
            cr.MoveTo(left, 28);
            cr.ShowText($"{System.IO.Path.GetFileName(data.Path)} — {data.StreamLabel}  ·  среднее {F2(data.Mean)}  ·  пик {F2(data.Peak)}  ·  мин {F2(data.Minimum)} Мбит/с");
        }
        if (hoverX is { } hx && hx >= left && hx <= left + pw)
        {
            int idx = (int)(t0 + (hx - left) / pw * span);
            if (idx >= 0 && idx < n)
            {
                Fg(0.6); cr.LineWidth(1); cr.MoveTo(hx, top); cr.LineTo(hx, top + ph); cr.Stroke();
                var label = $"{idx / 3600}:{idx % 3600 / 60:00}:{idx % 60:00} — {F2(series[idx])} Мбит/с";
                cr.FontSize(12);
                var w = cr.TextWidth(label);
                var bx = Math.Min(hx + 8, left + pw - w - 12);
                Bg(0.9); cr.Rect(bx - 4, top + 4, w + 8, 20); cr.Fill();
                Fg(); cr.MoveTo(bx, top + 18); cr.ShowText(label);
            }
        }
    }
}

public sealed class BitrateViewer : Script
{
    public override string Id => "bitrate-viewer";
    public override string Name => "Анализ битрейта";
    public override string Description => "Посекундный битрейт видео и аудио, ключевые кадры, статистика и интерактивный график";
    public override string Category => "Инструменты";
    public override string Icon => "utilities-system-monitor-symbolic";
    public override HashSet<string> Extensions => Exts.AllMedia;

    public override List<Field> Fields(IDictionary<string, object> values = null) =>
    [
        new Field("stream", "Поток", Combo, "Видео", "Параметры анализа").Opts("Видео", "Первое аудио"),
        new Field("save_png_export", "Сохранять копию графика в PNG", Check, false, "Параметры графиков").Hint("PNG 1920×720 в папку результатов"),
        new Field("open_chart", "Открывать окно графика после анализа", Check, true, "Параметры графиков"),
    ];

    static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    public static BitrateData Analyze(string path, string selector, double duration, CancelToken token, Action<double, string> onProgress = null)
    {
        var ffprobe = Tools.Require("ffprobe");
        var per = new Dictionary<int, long>();
        var keys = new List<double>();
        long packets = 0, bytes = 0;
        void OnLine(string line)
        {
            if (!line.StartsWith("packet|")) return;
            long size = 0;
            double pts = -1, dts = -1;
            bool key = false;
            foreach (var seg in line.Split('|').Skip(1))
            {
                var eq = seg.IndexOf('=');
                if (eq < 0) continue;
                var k = seg[..eq];
                var v = seg[(eq + 1)..];
                switch (k)
                {
                    case "size": long.TryParse(v, out size); break;
                    case "pts_time": if (!double.TryParse(v, System.Globalization.NumberStyles.Float, Inv, out pts)) pts = -1; break;
                    case "dts_time": if (!double.TryParse(v, System.Globalization.NumberStyles.Float, Inv, out dts)) dts = -1; break;
                    case "flags": key = v.Contains('K'); break;
                }
            }
            var t = pts >= 0 ? pts : dts;
            if (t < 0) return;
            int sec = (int)t;
            per[sec] = per.GetValueOrDefault(sec) + size * 8;
            packets++;
            bytes += size;
            if (key) keys.Add(t);
            if (onProgress != null && packets % 2000 == 0 && duration > 0)
                onProgress(Math.Min(95, t / duration * 95), $"Анализ пакетов: {t:0} / {duration:0} с");
        }
        var res = Proc.Run([ffprobe, "-hide_banner", "-loglevel", "quiet", "-select_streams", selector, "-show_entries",
            "packet=flags,size,pts_time,dts_time", "-print_format", "compact", path], token, onStdout: OnLine, source: "ffprobe");
        if (res.Cancelled) throw new CancelledException();
        if (per.Count == 0) throw new InvalidOperationException("Не удалось прочитать пакеты потока");
        int last = per.Keys.Max();
        return new BitrateData
        {
            Path = path, StreamLabel = selector, Duration = duration > 0 ? duration : last + 1,
            PerSecond = Enumerable.Range(0, last + 1).Select(i => Math.Round(per.GetValueOrDefault(i) / 1e6, 3)).ToList(),
            Keyframes = keys, Packets = packets, TotalBytes = bytes,
        };
    }

    public override Result Process(QueueItem item, Context ctx)
    {
        var info = ctx.ProbeItem();
        bool wantVideo = ctx.Str("stream", "Видео") == "Видео";
        var track = wantVideo ? info.Video.FirstOrDefault() : info.Audio.FirstOrDefault();
        if (track == null) return Result.Failed("❌ В файле нет нужного потока");
        bool isV = track.Type == "video";
        var data = Analyze(item.Path, $"{(isV ? "v" : "a")}:{track.TypeIndex}", info.Duration, ctx.Token, (p, m) => ctx.Progress(p, m));
        data.Codec = track.Codec;
        data.StreamLabel = $"{(isV ? "Видео" : "Аудио")} #{track.Index} ({track.Codec})";
        if (isV && data.Duration > 0)
        {
            var fps = data.Packets / data.Duration;
            data.Fps = fps is >= 1 and <= 360 ? Math.Round(fps, 3) : track.Fps;
        }
        var outputs = new List<string>();
        if (ctx.Bool("save_png_export"))
        {
            var png = ctx.OutputPath(item.Path, ".png", "_bitrate");
            if (BitrateChart.PngRenderer == null) ctx.Log("WARNING", "PNG не сохранён: отрисовка доступна только в графической сборке hktools");
            else
                try { BitrateChart.PngRenderer(data, png, 1920, 720, true); outputs.Add(png); }
                catch (Exception ex) { ctx.Log("WARNING", $"PNG не сохранён: {ex.Message}"); }
        }
        ctx.Progress(100, "Готово");
        var r = Result.Done($"📈 {item.Name}: среднее {data.Mean.ToString("0.00", Inv)}, пик {data.Peak.ToString("0.00", Inv)}, мин {data.Minimum.ToString("0.00", Inv)} Мбит/с, σ {data.Stdev.ToString("0.00", Inv)}, ключевых кадров {data.Keyframes.Count}", outputs.ToArray());
        r.Data = data;
        return r;
    }
}

/// <summary>Калькулятор таймингов в стиле Aegisub (только интерфейс, без обработки файлов).</summary>
public sealed class TimingCalculator : Script
{
    public override string Id => "timing-calculator";
    public override string Name => "Калькулятор таймингов";
    public override string Description => "Разница между двумя таймкодами для сдвига субтитров и аудио, история расчётов";
    public override string Category => "Инструменты";
    public override string Icon => "accessories-calculator-symbolic";
    public override string InputMode => "none";
    public override string[] Deps => [];
    public override Result Process(QueueItem item, Context ctx) => Result.Skipped("Только интерфейс");
}

public static class Registry
{
    public static readonly List<Script> All =
    [
        new MetadataCleanup(), new VideoEncode(), new ContainerConvert(),
        new AudioEncode(), new AudioDownmix(), new AudioSpeed(), new AudioChannels(), new AudioShift(), new AudioTransplant(),
        new MkvAssembly(), new StreamManagement(), new StreamReplacement(), new TrackExtractor(),
        new SubtitlesConvert(), new SubtitleShift(), new SpeechRecognition(),
        new MediaDownloader(),
        new BitrateViewer(), new TimingCalculator(),
    ];

    public static Script Get(string id) => All.FirstOrDefault(s => s.Id == id);
}
