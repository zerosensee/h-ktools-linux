using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HKTools.Scripts;

public sealed record WhisperModel(string Key, double SizeMb, string Description)
{
    public string Filename => $"ggml-{Key}.bin";
    public string Url => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{Filename}";
}

public sealed class SpeechRecognition : Script
{
    public static readonly WhisperModel[] Models =
    [
        new("tiny", 75, "Минимальная: максимальная скорость, базовое качество"),
        new("base", 142, "Быстрое распознавание чистой речи"),
        new("small", 466, "Баланс скорости и качества для диалогов"),
        new("small-q5_1", 182, "Квантованная Small, экономит память"),
        new("medium", 1530, "Высокое качество: термины, акценты, шум"),
        new("medium-q5_0", 515, "Квантованная Medium"),
        new("large-v3", 3100, "Флагман с наивысшей точностью"),
        new("large-v3-q5_0", 1080, "Квантованная Large v3"),
        new("large-v3-turbo", 1620, "До 4× быстрее Large v3 при близком качестве"),
        new("large-v3-turbo-q5_0", 574, "Квантованная Turbo — лучший выбор для слабых GPU"),
    ];
    static readonly string[] Langs = ["Авто", "Русский (ru)", "English (en)", "Deutsch (de)", "Français (fr)", "Español (es)", "Italiano (it)",
        "日本語 (ja)", "中文 (zh)", "한국어 (ko)", "Português (pt)", "Українська (uk)", "Polski (pl)", "Türkçe (tr)"];
    static readonly string[] Backends = ["Авто (GPU, если сборка поддерживает)", "Только CPU"];

    public static string ModelsDir() => Paths.WhisperModelsDir(Settings.Instance.GetStr("whisper_models_dir") is var d && d != "" ? d : null);
    public static string ModelPath(string key) => Path.Combine(ModelsDir(), $"ggml-{key}.bin");

    public static List<string> Downloaded()
    {
        var known = Models.Select(m => m.Key).ToHashSet();
        var res = Models.Where(m => File.Exists(ModelPath(m.Key))).Select(m => m.Key).ToList();
        try
        {
            res.AddRange(Directory.GetFiles(ModelsDir(), "ggml-*.bin").Select(f => Path.GetFileName(f)[5..^4]).Where(k => !known.Contains(k)).OrderBy(k => k, StringComparer.Ordinal));
        }
        catch { }
        return res;
    }

    /// <summary>Скачать модель (с докачкой через .part). Возвращает путь.</summary>
    public static string Download(string key, Action<double, string> onProgress, CancelToken token)
    {
        var model = Models.FirstOrDefault(m => m.Key == key) ?? throw new ArgumentException($"Неизвестная модель: {key}");
        var target = ModelPath(key);
        var part = target + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("hktools-linux");
        var req = new HttpRequestMessage(HttpMethod.Get, model.Url);
        if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var resp = http.Send(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        if (have > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0;
        long total = (resp.Content.Headers.ContentLength ?? 0) + have;
        using (var src = resp.Content.ReadAsStream())
        using (var fh = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create))
        {
            var buf = new byte[1 << 20];
            long done = have;
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                token?.Check();
                fh.Write(buf, 0, n);
                done += n;
                if (total > 0) onProgress?.Invoke(done * 100.0 / total, $"{done / 1e6:0} / {total / 1e6:0} МБ");
            }
        }
        File.Move(part, target, true);
        return target;
    }

    public override string Id => "speech-recognition";
    public override string Name => "Распознавание речи";
    public override string Description => "Транскрибация в SRT/VTT/TXT/LRC/JSON через whisper.cpp (CPU, CUDA, Vulkan, ROCm)";
    public override string Category => "Субтитры";
    public override string Icon => "audio-input-microphone-symbolic";
    public override HashSet<string> Extensions => Exts.AllMedia;
    public override string[] Deps => ["ffmpeg", "ffprobe", "whisper"];

    public override List<Field> Fields(IDictionary<string, object> values = null)
    {
        var have = Downloaded();
        var def = have.Contains("large-v3-turbo") ? "large-v3-turbo" : have.FirstOrDefault() ?? "large-v3-turbo";
        const string M = "Основные:Модель и язык", S = "Субтитры:Форматы экспорта", E = "Движок:Производительность", P = "Субтитры:Параметры субтитров";
        return
        [
            new Field("whisper_model", "Модель Whisper", Combo, def, M).Opts(have.Count > 0 ? have : ["(нет загруженных моделей)"])
                .Hint("Скачать модели — кнопка «Модели» в заголовке"),
            new Field("whisper_language", "Язык речи", Combo, "Авто", M).Opts(Langs).Hint("«Авто» определяет язык по первым 30 секундам"),
            new Field("whisper_translate", "Перевести на английский", Check, false, M),
            new Field("output_srt", "SubRip (.srt)", Check, true, S),
            new Field("output_vtt", "WebVTT (.vtt)", Check, false, S),
            new Field("output_txt", "Текст (.txt)", Check, true, S),
            new Field("output_lrc", "Караоке LRC (.lrc)", Check, false, S),
            new Field("output_json", "JSON (.json)", Check, false, S),
            new Field("max_segment_length", "Макс. длина строки (символы)", Int, 35L, P).Range(0, 200).Hint("35–42 — стандарт для кино и сериалов; 0 — без переноса"),
            new Field("split_on_word", "Переносить строго по словам", Check, true, P),
            new Field("whisper_max_context", "Контекст предыдущих токенов", Int, 0L, P).Range(0, 448).Hint("0 — защита от зацикливания на музыке и тишине"),
            new Field("whisper_no_speech_thold", "Порог тишины/музыки", Float, 0.65, P).Range(0, 1, 0.05),
            new Field("whisper_suppress_nst", "Подавлять неречевые галлюцинации (--suppress-nst)", Check, true, P),
            new Field("whisper_backend", "Вычислительный движок", Combo, Backends[0], E).Opts(Backends)
                .Hint("GPU зависит от пакета: whisper.cpp-cuda / -vulkan / -hip из AUR"),
            new Field("whisper_threads", "Потоков CPU", Int, (long)Math.Min(Environment.ProcessorCount, 8), E).Range(1, 64),
            new Field("whisper_flash_attn", "Flash Attention (GPU)", Check, true, E),
            new Field("whisper_beam", "Beam size", Int, 5L, E).Range(1, 16),
        ];
    }

    static readonly (string Key, string Flag, string Ext)[] Outs =
    [
        ("output_srt", "-osrt", ".srt"), ("output_vtt", "-ovtt", ".vtt"), ("output_txt", "-otxt", ".txt"), ("output_lrc", "-olrc", ".lrc"), ("output_json", "-oj", ".json"),
    ];

    public override Result Process(QueueItem item, Context ctx)
    {
        var whisper = Tools.Require("whisper");
        var mpath = ModelPath(ctx.Str("whisper_model"));
        if (!File.Exists(mpath)) return Result.Failed("❌ Модель Whisper не загружена — откройте «Модели» и скачайте её");
        var info = ctx.ProbeItem();
        if (info.Audio.Count == 0) return Result.Failed("❌ В файле нет аудиодорожки");
        var tmp = ctx.TempDir("hk_whisper_");
        var wav = Path.Combine(tmp, "audio.wav");
        ctx.Progress(0, "Подготовка аудио 16 кГц…");
        var res = ctx.FFmpeg(["-i", item.Path, "-map", $"0:{info.Audio[0].Index}", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav],
            info.Duration, "Подготовка аудио", span: 10);
        if (!res.Ok) return Result.Failed($"❌ Не удалось подготовить аудио\n{res.ErrorText()}");
        var outBase = ctx.OutputPath(item.Path, "");
        var m = Regex.Match(ctx.Str("whisper_language", "Авто"), @"\((\w+)\)");
        var cmd = new List<string> { whisper, "-m", mpath, "-f", wav, "-l", m.Success ? m.Groups[1].Value : "auto", "-of", outBase, "-pp" };
        if (ctx.Bool("whisper_translate")) cmd.Add("--translate");
        var wanted = Outs.Where(o => ctx.Bool(o.Key)).ToList();
        if (wanted.Count == 0) wanted = [Outs[0]];
        cmd.AddRange(wanted.Select(o => o.Flag));
        int ml = ctx.Int("max_segment_length", 35);
        if (ml > 0)
        {
            cmd.AddRange(["-ml", ml.ToString()]);
            if (ctx.Bool("split_on_word", true)) cmd.Add("-sow");
        }
        cmd.AddRange(["-mc", ctx.Int("whisper_max_context").ToString(), "-t", ctx.Int("whisper_threads", 4).ToString(),
            "-bs", ctx.Int("whisper_beam", 5).ToString(), "-nth", Common.F(ctx.Dbl("whisper_no_speech_thold", 0.65), "0.00")]);
        if (ctx.Bool("whisper_suppress_nst", true)) cmd.Add("-sns");
        if (ctx.Str("whisper_backend").StartsWith("Только CPU")) cmd.Add("-ng");
        else cmd.Add(ctx.Bool("whisper_flash_attn", true) ? "-fa" : "-nfa");
        void OnLine(string line)
        {
            var pm = Regex.Match(line, @"progress\s*=\s*(\d+)%");
            if (pm.Success)
            {
                int pct = int.Parse(pm.Groups[1].Value);
                ctx.Progress(10 + pct * 0.9, $"Распознавание · {pct}%");
            }
        }
        ctx.Progress(10, "Загрузка модели…");
        var r = Proc.Run(cmd, ctx.Token, onStdout: OnLine, onStderr: OnLine, source: Name);
        if (r.Cancelled) throw new CancelledException();
        var outputs = wanted.Select(o => outBase + o.Ext).Where(File.Exists).ToArray();
        if (r.Ok && outputs.Length > 0)
        {
            ctx.Progress(100, "Готово");
            return Result.Done($"✅ Распознано: {string.Join(", ", outputs.Select(Path.GetFileName))}", outputs);
        }
        var err = r.ErrorText().ToLowerInvariant();
        var hint = err.Contains("unknown argument") || err.Contains("error: unknown") ? "\nВозможно, установлена старая версия whisper.cpp — обновите пакет." : "";
        return Result.Failed($"❌ Ошибка whisper.cpp{hint}\n{r.ErrorText()}");
    }
}

public sealed record DlFormat(string Label, string Arg, bool AudioOnly);

public sealed class DlInfo
{
    public string Title = "", Uploader = "";
    public double Duration;
    public List<DlFormat> Formats = new();
    public List<string> Subtitles = new(), AutoSubtitles = new();
}

/// <summary>Загрузка медиа по ссылке через yt-dlp.</summary>
public sealed class MediaDownloader : Script
{
    static readonly Regex ProgressRe = new(@"\[download\]\s+(\d+(?:\.\d+)?)%(?:\s+of\s+~?\s*([\d.]+\w+))?(?:.*?at\s+([\d.]+\w+/s))?(?:.*?ETA\s+([\d:]+))?");
    static readonly Regex DestRe = new(@"(?:Destination:|Merging formats into|has already been downloaded|\[ExtractAudio\] Destination:|\[Merger\] Merging formats into)\s*""?(.+?)""?$");
    public static readonly DlFormat[] DefaultFormats = [new("Лучшее видео + аудио", "bv*+ba/b", false), new("Только аудио (лучшее)", "ba/b", true)];

    public override string Id => "media-downloader";
    public override string Name => "Загрузка медиа";
    public override string Description => "Скачивание видео и аудио по ссылке через yt-dlp с выбором качества и субтитров";
    public override string Category => "Сеть";
    public override string Icon => "folder-download-symbolic";
    public override string InputMode => "urls";
    public override string[] Deps => ["yt-dlp", "ffmpeg"];

    /// <summary>Метаданные ссылки: название, доступные высоты, субтитры.</summary>
    public static DlInfo FetchInfo(string url)
    {
        var (rc, outp, err) = Proc.Capture([Tools.Require("yt-dlp"), "-J", "--no-playlist", "--no-warnings", url], 90000);
        if (rc != 0 || outp.Trim() == "")
            throw new InvalidOperationException(err.Trim() != "" ? err.Trim().Split('\n')[^1] : "yt-dlp не вернул данные");
        using var doc = JsonDocument.Parse(outp);
        var root = doc.RootElement;
        string S(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
        var info = new DlInfo { Title = S("title") is { Length: > 0 } t ? t : url, Uploader = S("uploader") is { Length: > 0 } u ? u : S("channel") };
        if (root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number) info.Duration = dur.GetDouble();
        var heights = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        if (root.TryGetProperty("formats", out var fmts) && fmts.ValueKind == JsonValueKind.Array)
            foreach (var f in fmts.EnumerateArray())
                if (f.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number && h.GetDouble() > 0
                    && !(f.TryGetProperty("vcodec", out var vc) && vc.GetString() == "none"))
                    heights.Add((int)h.GetDouble());
        info.Formats.AddRange(DefaultFormats);
        foreach (var hh in heights) info.Formats.Add(new($"До {hh}p", $"bv*[height<={hh}]+ba/b[height<={hh}]/b", false));
        List<string> Keys(string k) => root.TryGetProperty(k, out var o) && o.ValueKind == JsonValueKind.Object
            ? o.EnumerateObject().Select(p => p.Name).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList() : new();
        info.Subtitles = Keys("subtitles");
        info.AutoSubtitles = Keys("automatic_captions").Except(info.Subtitles).Take(40).ToList();
        return info;
    }

    public override List<Field> Fields(IDictionary<string, object> values = null)
    {
        const string F = "Формат сохранения", S = "Субтитры";
        return
        [
            new Field("VideoContainer", "Контейнер видео", Combo, "Авто", F).Opts("Авто", "MP4", "MKV"),
            new Field("AudioFormat", "Формат аудио (режим «только аудио»)", Combo, "Исходный", F).Opts("Исходный", "MP3 (320k)", "M4A (AAC)", "FLAC", "Opus"),
            new Field("ForceOverwrite", "Перезаписывать существующие файлы", Check, false, F),
            new Field("DownloadSubtitles", "Скачивать субтитры", Check, false, S),
            new Field("EmbedSubtitles", "Встраивать субтитры в видео", Check, true, S).When(new Cond("DownloadSubtitles", true)),
            new Field("SubLangs", "Языки субтитров по умолчанию", Text, "ru,en", S).Hint("Используется, если у ссылки не выбран язык; «all» — все")
                .When(new Cond("DownloadSubtitles", true)),
            new Field("UseBrowserCookies", "Cookies из браузера", Combo, "Нет", "Дополнительно").Opts("Нет", "firefox", "chromium", "chrome", "brave", "vivaldi", "zen")
                .Hint("Для закрытых/возрастных видео (--cookies-from-browser)"),
            new Field("AdditionalArgs", "Дополнительные аргументы yt-dlp", Text, "", "Дополнительно"),
        ];
    }

    public override string RowSubtitle(QueueItem item, List<QueueItem> all)
    {
        var bits = new List<string>();
        if (item.Extra.GetValueOrDefault("meta") is DlInfo meta && meta.Uploader != "") bits.Add(meta.Uploader);
        if (item.Extra.GetValueOrDefault("format_label") is string fl && fl != "") bits.Add(fl);
        if (item.Extra.GetValueOrDefault("sub_lang") is string sl && sl != "" && sl != "none") bits.Add($"субтитры: {sl}");
        return string.Join(" · ", bits);
    }

    public static string DownloadDir()
    {
        foreach (var c in new[] { Environment.GetEnvironmentVariable("XDG_DOWNLOAD_DIR"), "~/Загрузки", "~/Downloads" })
            if (!string.IsNullOrEmpty(c) && Directory.Exists(Paths.ExpandUser(c))) return Paths.ExpandUser(c);
        return Paths.ExpandUser("~/Downloads");
    }

    public override Result Process(QueueItem item, Context ctx)
    {
        var ytdlp = Tools.Require("yt-dlp");
        var url = item.Path;
        var target = ctx.OutDir ?? DownloadDir();
        Directory.CreateDirectory(target);
        var fmt = item.Extra.GetValueOrDefault("format_arg") as string is { Length: > 0 } fa ? fa : "bv*+ba/b";
        bool audioOnly = Json.ToBool(item.Extra.GetValueOrDefault("audio_only")) || fmt is "ba" or "ba/b";
        var cmd = new List<string> { ytdlp, "--newline", "--no-playlist", "-f", fmt, "-P", target, "-o", "%(title)s.%(ext)s",
            "--http-chunk-size", "10M", "--concurrent-fragments", "4", "--retries", "25", "--fragment-retries", "25", "--no-mtime",
            ctx.Bool("ForceOverwrite") ? "--force-overwrites" : "--no-force-overwrites" };
        if (audioOnly)
            cmd.AddRange(ctx.Str("AudioFormat", "Исходный") switch
            {
                "MP3 (320k)" => ["--extract-audio", "--audio-format", "mp3", "--audio-quality", "0"],
                "M4A (AAC)" => ["--extract-audio", "--audio-format", "m4a"],
                "FLAC" => ["--extract-audio", "--audio-format", "flac"],
                "Opus" => ["--extract-audio", "--audio-format", "opus"],
                _ => Array.Empty<string>(),
            });
        else
        {
            var vc = ctx.Str("VideoContainer", "Авто");
            if (vc == "MP4") cmd.AddRange(["--merge-output-format", "mp4", "--remux-video", "mp4"]);
            else if (vc == "MKV") cmd.AddRange(["--merge-output-format", "mkv"]);
        }
        var subLang = item.Extra.GetValueOrDefault("sub_lang") as string;
        bool hasSub = !string.IsNullOrEmpty(subLang) && subLang != "none";
        if (ctx.Bool("DownloadSubtitles") || hasSub)
        {
            var langs = hasSub ? subLang : ctx.Str("SubLangs", "ru,en");
            cmd.AddRange(["--write-subs", "--write-auto-subs"]);
            cmd.AddRange(langs == "all" ? ["--all-subs"] : ["--sub-langs", langs]);
            if (ctx.Bool("EmbedSubtitles", true) && !audioOnly)
            {
                cmd.Add("--embed-subs");
                if (ctx.Str("VideoContainer", "Авто") != "MP4" && !cmd.Contains("--merge-output-format")) cmd.AddRange(["--merge-output-format", "mkv"]);
            }
        }
        if (Tools.Which("ffmpeg") is { } ffm) cmd.AddRange(["--ffmpeg-location", ffm]);
        var cookies = ctx.Str("UseBrowserCookies", "Нет");
        if (cookies != "Нет") cmd.AddRange(["--cookies-from-browser", cookies]);
        var extra = ctx.Str("AdditionalArgs").Trim();
        if (extra != "")
        {
            try { cmd.AddRange(Tools.ShellSplit(extra)); }
            catch (Exception ex) { return Result.Failed($"❌ Ошибка в дополнительных аргументах: {ex.Message}"); }
        }
        cmd.AddRange(["--", url]);
        var outputs = new List<string>();
        int phase = 0;
        void OnLine(string line)
        {
            var m = ProgressRe.Match(line);
            if (m.Success)
            {
                var pct = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (pct >= 100) phase++;
                var bits = new List<string> { Common.F(pct, "0.0") + "%" };
                if (m.Groups[2].Success) bits.Add(m.Groups[2].Value);
                if (m.Groups[3].Success) bits.Add(m.Groups[3].Value);
                if (m.Groups[4].Success) bits.Add($"ETA {m.Groups[4].Value}");
                // Видео и аудио качаются отдельно: делим шкалу
                int share = !audioOnly && fmt.Contains('+') ? 50 : 100;
                double @base = share == 50 ? Math.Min(phase, 1) * share : 0;
                ctx.Progress(Math.Min(99, @base + pct * share / 100), "Загрузка · " + string.Join(" · ", bits));
                return;
            }
            var d = DestRe.Match(line);
            if (d.Success)
            {
                var p = d.Groups[1].Value.Trim();
                lock (outputs) if (p != "" && !outputs.Contains(p)) outputs.Add(p);
            }
            if (line.Contains("[Merger]") || line.Contains("[ExtractAudio]") || line.Contains("[EmbedSubtitle]")) ctx.Progress(99, "Постобработка…");
        }
        var res = Proc.Run(cmd, ctx.Token, onStdout: OnLine, onStderr: _ => { }, source: Name);
        if (res.Cancelled) throw new CancelledException();
        var final = outputs.Select(p => Path.IsPathRooted(p) ? p : Path.Combine(target, p)).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        // после склейки оставляем только итоговый файл
        var merged = final.Where(p => !Regex.IsMatch(p, @"\.f\d+\.\w+$")).ToList();
        if (merged.Count > 0) final = merged;
        if (res.Ok)
        {
            ctx.Progress(100, "Готово");
            var name = final.Count > 0 ? Path.GetFileName(final[^1]) : (item.Extra.GetValueOrDefault("meta") as DlInfo)?.Title ?? url;
            return Result.Done($"✅ Скачано: {name}", final.TakeLast(1).ToArray());
        }
        return Result.Failed($"❌ Ошибка yt-dlp\n{res.ErrorText()}");
    }
}
