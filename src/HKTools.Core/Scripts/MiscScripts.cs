namespace HKTools.Scripts;

public sealed class AudioChannels : Script
{
    // имена каналов ffmpeg → суффиксы как у eac3to
    static readonly Dictionary<string, string> Suffix = new()
    {
        ["FL"] = "L", ["FR"] = "R", ["FC"] = "C", ["LFE"] = "LFE", ["SL"] = "SL", ["SR"] = "SR", ["BL"] = "BL", ["BR"] = "BR", ["BC"] = "BC",
        ["FLC"] = "FLC", ["FRC"] = "FRC", ["TC"] = "TC", ["LFE2"] = "LFE2", ["WL"] = "WL", ["WR"] = "WR",
    };
    static readonly (string L, string R, string Pair)[] Pairs = [("L", "R", "LR"), ("SL", "SR", "SLSR"), ("BL", "BR", "BLBR")];
    static readonly Dictionary<string, string[]> Known = new()
    {
        ["mono"] = ["FC"], ["stereo"] = ["FL", "FR"], ["2.1"] = ["FL", "FR", "LFE"], ["3.0"] = ["FL", "FR", "FC"],
        ["quad"] = ["FL", "FR", "BL", "BR"], ["quad(side)"] = ["FL", "FR", "SL", "SR"], ["4.0"] = ["FL", "FR", "FC", "BC"],
        ["5.0"] = ["FL", "FR", "FC", "BL", "BR"], ["5.0(side)"] = ["FL", "FR", "FC", "SL", "SR"], ["5.1"] = ["FL", "FR", "FC", "LFE", "BL", "BR"],
        ["5.1(side)"] = ["FL", "FR", "FC", "LFE", "SL", "SR"], ["6.1"] = ["FL", "FR", "FC", "LFE", "BC", "SL", "SR"],
        ["7.1"] = ["FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR"], ["7.1(wide)"] = ["FL", "FR", "FC", "LFE", "BL", "BR", "FLC", "FRC"],
    };

    public static string[] LayoutChannels(string layout, int channels)
    {
        if (Known.TryGetValue(layout ?? "", out var k)) return k;
        if (layout?.Contains('+') == true) return layout.Split('+');
        var def = channels switch { 1 => "mono", 2 => "stereo", 3 => "2.1", 4 => "quad", 5 => "5.0", 6 => "5.1", 7 => "6.1", 8 => "7.1", _ => "" };
        return Known.TryGetValue(def, out k) ? k : Enumerable.Range(0, channels).Select(i => $"c{i}").ToArray();
    }

    public override string Id => "audio-channels";
    public override string Name => "Разделение каналов";
    public override string Description => "Разделение многоканального аудио на моно-WAV (L, R, C, LFE, SL, SR…) со склейкой стереопар";
    public override string Category => "Аудио";
    public override string Icon => "audio-card-symbolic";
    public override HashSet<string> Extensions => Exts.AllMedia;

    public override List<Field> Fields(IDictionary<string, object> values = null) =>
    [
        new Field("MergeStereo", "Склеивать каналы в стереопары (LR, SLSR, BLBR)", Check, true, "Параметры разделения"),
        Common.DeleteOriginal("Общие", "DeleteOriginal"),
    ];

    public override Result Process(QueueItem item, Context ctx)
    {
        var info = ctx.ProbeItem();
        if (info.Audio.Count == 0) return Result.Failed("❌ В файле нет аудиодорожки");
        var track = info.Audio[0];
        if (track.Channels < 2) return Result.Skipped("⏭ Дорожка моно — разделять нечего");
        var chans = LayoutChannels(track.ChannelLayout, track.Channels);
        var layout = track.ChannelLayout != "" && !track.ChannelLayout.Contains('+') ? track.ChannelLayout : null;
        var first = ctx.OutputPath(item.Path, ".wav", ".L");
        var bas = first[..^".L.wav".Length];
        string Suf(string c) => Suffix.GetValueOrDefault(c, c);
        var outputs = new Dictionary<string, string>();
        foreach (var c in chans) outputs[Suf(c)] = $"{bas}.{Suf(c)}.wav";
        if (outputs.Values.All(File.Exists) && !ctx.Overwrite) return Result.Skipped("⏭ ПРОПУСК: моно-каналы уже существуют");
        var codec = (track.BitDepth > 0 ? track.BitDepth : 24) > 16 ? "pcm_s24le" : "pcm_s16le";
        var split = $"[0:{track.Index}]channelsplit" + (layout != null ? $"=channel_layout={layout}" : "");
        var args = new List<string> { "-i", item.Path, "-filter_complex", split + string.Concat(chans.Select(c => $"[{Suf(c)}]")) };
        foreach (var c in chans) args.AddRange(["-map", $"[{Suf(c)}]", "-c:a", codec, outputs[Suf(c)]]);
        bool merge = ctx.Bool("MergeStereo");
        var res = ctx.FFmpeg(args, info.Duration, "Разделение каналов", span: merge ? 70 : 100);
        if (!res.Ok)
        {
            foreach (var p in outputs.Values) Context.RemoveQuiet(p);
            return Result.Failed($"❌ Ошибка разделения каналов\n{res.ErrorText()}");
        }
        var result = Result.Done($"✅ Разделено на {outputs.Count} канал(а/ов): {Path.GetFileName(bas)}", outputs.Values.ToArray());
        if (merge)
            foreach (var (l, r, pair) in Pairs)
            {
                if (!outputs.ContainsKey(l) || !outputs.ContainsKey(r)) continue;
                var target = $"{bas}.{pair}.wav";
                ctx.Progress(75, $"Склейка {pair}…");
                var rr = FFmpeg.Run(["-i", outputs[l], "-i", outputs[r], "-filter_complex", "[0:a][1:a]join=inputs=2:channel_layout=stereo:map=0.0-FL|1.0-FR[a]",
                    "-map", "[a]", "-c:a", codec, target], ctx.Token, source: Name);
                ctx.Check();
                if (rr.Ok) result.Outputs.Add(target);
                else { result.Status = "partial"; result.Messages.Add($"⚠ Не удалось склеить {pair}"); }
            }
        ctx.FinalizeSource(item.Path, result.Outputs[0], result);
        ctx.Progress(100, "Готово");
        return result;
    }
}

public static class MkvRun
{
    public static int? AudioTid(string path) => Probe.MkvTracks(path).Where(t => t.Type == "audio").Select(t => (int?)t.Id).FirstOrDefault();

    /// <summary>Запуск mkvmerge с разбором прогресса. 0 — ок, 1 — предупреждения (файл создан), 2 — ошибка.</summary>
    public static ProcResult Run(Context ctx, List<string> cmd, string label, double @base = 0, double span = 100)
    {
        void OnLine(string line)
        {
            if (!line.Contains('%') || !line.Contains(':')) return;
            var s = line[(line.LastIndexOf(':') + 1)..].Trim().TrimEnd('%');
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                ctx.Progress(@base + pct * span / 100, $"{label} · {pct:0}%");
        }
        var res = Proc.Run(cmd, ctx.Token, onStdout: OnLine, splitCr: true, captureStdout: true, source: ctx.Script.Name);
        if (res.Cancelled) throw new CancelledException();
        // mkvmerge пишет ошибки и предупреждения в stdout
        var msgs = res.Stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !l.Contains('%') && l.Trim() != "").ToList();
        res.StderrTail = msgs.TakeLast(15).Concat(res.StderrTail).ToList();
        foreach (var ln in msgs)
            if (ln.StartsWith("warning", StringComparison.OrdinalIgnoreCase) || ln.StartsWith("предупреждение", StringComparison.OrdinalIgnoreCase))
                ctx.Log("WARNING", ln);
        if (res.ExitCode == 1) res.ExitCode = 0;
        return res;
    }
}

public sealed class AudioShift : Script
{
    static readonly string[] Modes = ["MKA без перекодирования (mkvmerge)", "FLAC (FFmpeg Lossless)", "WAV (FFmpeg PCM)"];

    public static string ShiftFilter(int ms) =>
        ms >= 0 ? $"adelay={ms}:all=1" : $"atrim=start={Common.F(Math.Abs(ms) / 1000.0, "0.000")},asetpts=PTS-STARTPTS";

    public override string Id => "audio-shift";
    public override string Name => "Сдвиг аудио";
    public override string Description => "Сдвиг (задержка) аудиодорожки: без перекодирования через mkvmerge или в FLAC/WAV";
    public override string Category => "Аудио";
    public override string Icon => "media-skip-forward-symbolic";
    public override HashSet<string> Extensions => Exts.AllMedia;
    public override string[] Deps => ["ffmpeg", "ffprobe", "mkvmerge"];

    public override List<Field> Fields(IDictionary<string, object> values = null) =>
    [
        new Field("ShiftMs", "Величина сдвига (мс)", Int, 1000L, "Настройки сдвига").Range(0, 36_000_000, 10),
        new Field("ShiftDirection", "Направление сдвига", Combo, "Вперед", "Настройки сдвига").Opts("Вперед", "Назад")
            .Hint("Вперёд — звук позже (задержка), назад — раньше"),
        new Field("OutputFormat", "Формат и режим вывода", Combo, Modes[0], "Настройки экспорта").Opts(Modes)
            .Hint("mkvmerge сохраняет исходный кодек бит-в-бит (замена eac3to)"),
    ];

    public override Result Process(QueueItem item, Context ctx)
    {
        int ms = ctx.Int("ShiftMs");
        if (ctx.Str("ShiftDirection") == "Назад") ms = -ms;
        var mode = ctx.Str("OutputFormat", Modes[0]);
        var info = ctx.ProbeItem();
        if (info.Audio.Count == 0) return Result.Failed("❌ В файле нет аудиодорожки");
        var sign = ms.ToString("+0;-0;+0");
        string outp;
        ProcResult res;
        if (mode.StartsWith("MKA"))
        {
            var mkvmerge = Tools.Require("mkvmerge");
            var tid = MkvRun.AudioTid(item.Path);
            if (tid == null) return Result.Failed("❌ mkvmerge не нашёл аудиодорожку");
            outp = ctx.OutputPath(item.Path, ".mka", $"_shift{sign}ms");
            if (ctx.SkipIfExists(outp) is { } skip) return skip;
            res = MkvRun.Run(ctx, [mkvmerge, "-o", outp, "--no-video", "--no-subtitles", "--no-chapters", "--no-attachments",
                "--audio-tracks", tid.ToString(), "--sync", $"{tid}:{ms}", item.Path], "Сдвиг (mkvmerge)");
        }
        else
        {
            bool flac = mode.StartsWith("FLAC");
            outp = ctx.OutputPath(item.Path, flac ? ".flac" : ".wav", $"_shift{sign}ms");
            if (ctx.SkipIfExists(outp) is { } skip) return skip;
            var track = info.Audio[0];
            var codec = flac ? "flac" : ((track.BitDepth > 0 ? track.BitDepth : 24) > 16 ? "pcm_s24le" : "pcm_s16le");
            res = ctx.FFmpeg(["-i", item.Path, "-map", $"0:{track.Index}", "-vn", "-sn", "-af", ShiftFilter(ms), "-c:a", codec, outp],
                info.Duration + Math.Max(0, ms) / 1000.0, "Сдвиг аудио");
        }
        if (res.Ok && File.Exists(outp))
        {
            ctx.Progress(100, "Готово");
            return Result.Done($"✅ Сдвиг {sign} мс: {Common.Fn(outp)}", outp);
        }
        Context.RemoveQuiet(outp);
        return Result.Failed($"❌ Ошибка сдвига\n{res.ErrorText()}");
    }
}

/// <summary>Пересадка аудиодорожки из файла-донора в видео с синхронизацией по осциллограмме.</summary>
public sealed class AudioTransplant : Script
{
    const double AacPrimingMs = 1024.0 / 48000 * 1000; // 21.333 мс

    public override string Id => "audio-transplant";
    public override string Name => "Пересадка аудио";
    public override string Description => "Пересадка дорожки из другого файла в видео со сдвигом по осциллограмме (без перекодирования)";
    public override string Category => "Аудио";
    public override string Icon => "insert-object-symbolic";
    public override HashSet<string> Extensions => Exts.VideoContainers;
    public override string[] Deps => ["ffmpeg", "ffprobe", "mkvmerge"];

    public override List<Field> Fields(IDictionary<string, object> values = null)
    {
        const string G = "Пересадка";
        return
        [
            new Field("transplant_source", "Файл-донор аудио", Text, "", G).Ph("/путь/к/озвучке.mka").Pick("file"),
            new Field("transplant_source_track", "Аудиодорожка донора (№ с 0)", Int, 0L, G).Range(0, 99),
            new Field("transplant_shift_ms", "Сдвиг (мс)", Int, 0L, G).Range(-3_600_000, 3_600_000, 1).Hint("Положительный — звук позже"),
            new Field("transplant_aac_comp", "Компенсировать задержку AAC (−21,33 мс)", Check, false, G).Hint("Только если донор — AAC с неучтённым priming-сдвигом"),
            new Field("transplant_title", "Название дорожки", Text, "", G),
            new Field("transplant_lang", "Язык дорожки (ISO 639-2)", Text, "rus", G),
            new Field("transplant_subs", "Внешние субтитры (надписи), необязательно", Text, "", G).Pick("file"),
            new Field("transplant_subs_title", "Название субтитров", Text, "Надписи", G),
        ];
    }

    public override Result Process(QueueItem item, Context ctx)
    {
        var src = Paths.ExpandUser(ctx.Str("transplant_source").Trim());
        if (src == "" || !File.Exists(src)) return Result.Failed("❌ Не выбран файл-донор аудио");
        var mkvmerge = Tools.Require("mkvmerge");
        var audioIds = Probe.MkvTracks(src).Where(t => t.Type == "audio").Select(t => t.Id).ToList();
        int idx = ctx.Int("transplant_source_track");
        if (idx >= audioIds.Count) return Result.Failed($"❌ В доноре нет аудиодорожки №{idx}");
        int tid = audioIds[idx];
        double shift = ctx.Dbl("transplant_shift_ms");
        if (ctx.Bool("transplant_aac_comp"))
        {
            string codec = "";
            try { codec = Probe.Run(src).Audio[idx].Codec; } catch { }
            if (codec == "aac") shift -= AacPrimingMs;
        }
        int shiftMs = (int)Math.Round(shift, MidpointRounding.ToEven);
        var outp = ctx.OutputPath(item.Path, ".mkv");
        if (ctx.SkipIfExists(outp) is { } skip) return skip;
        var dest = Probe.MkvTracks(item.Path);
        var lang = ctx.Str("transplant_lang", "rus");
        if (lang == "") lang = "und";
        var title = ctx.Str("transplant_title").Trim();
        var cmd = new List<string> { mkvmerge, "-o", outp,
            "--audio-tracks", tid.ToString(), "--no-video", "--no-subtitles", "--no-chapters", "--no-attachments", "--no-global-tags",
            "--sync", $"{tid}:{shiftMs}", "--language", $"{tid}:{lang}", "--default-track-flag", $"{tid}:yes", "--forced-display-flag", $"{tid}:yes" };
        if (title != "") cmd.AddRange(["--track-name", $"{tid}:{title}"]);
        cmd.Add(src);
        int next = 1;
        int? subsInput = null;
        var subs = Paths.ExpandUser(ctx.Str("transplant_subs").Trim());
        if (subs != "" && File.Exists(subs))
        {
            subsInput = next++;
            cmd.AddRange(["--language", $"0:{lang}", "--track-name", $"0:{ctx.Str("transplant_subs_title", "Надписи")}",
                "--default-track-flag", "0:yes", "--forced-display-flag", "0:yes", "--sub-charset", "0:UTF-8", subs]);
        }
        int destInput = next;
        int? videoTid = null;
        foreach (var t in dest)
        {
            if (t.Type is "audio" or "subtitles") cmd.AddRange(["--default-track-flag", $"{t.Id}:no", "--forced-display-flag", $"{t.Id}:no"]);
            else if (t.Type == "video" && videoTid == null) videoTid = t.Id;
        }
        cmd.Add(item.Path);
        var order = new List<string>();
        if (videoTid != null) order.Add($"{destInput}:{videoTid}");
        order.Add($"0:{tid}");
        if (subsInput != null) order.Add($"{subsInput}:0");
        order.AddRange(dest.Where(t => t.Id != videoTid).Select(t => $"{destInput}:{t.Id}"));
        cmd.AddRange(["--track-order", string.Join(",", order)]);
        ctx.Log("INFO", $"Сдвиг пересаживаемой дорожки: {shiftMs:+0;-0;+0} мс");
        var res = MkvRun.Run(ctx, cmd, "Пересадка (mkvmerge)");
        if (res.Ok && File.Exists(outp))
        {
            ctx.Progress(100, "Готово");
            return Result.Done($"✅ Аудио пересажено ({shiftMs:+0;-0;+0} мс): {Common.Fn(outp)}", outp);
        }
        Context.RemoveQuiet(outp);
        return Result.Failed($"❌ Ошибка mkvmerge\n{res.ErrorText()}");
    }
}

public sealed class ContainerConvert : Script
{
    static readonly Dictionary<string, string> Formats = new()
    {
        ["MP4"] = ".mp4", ["MKV"] = ".mkv", ["MOV"] = ".mov", ["WEBM"] = ".webm", ["AVI"] = ".avi", ["TS"] = ".ts",
    };
    static readonly HashSet<string> TextSubs = ["subrip", "ass", "ssa", "webvtt", "mov_text", "text"];
    static readonly HashSet<string> WebmVideo = ["vp8", "vp9", "av1"];
    static readonly HashSet<string> WebmAudio = ["opus", "vorbis"];

    public override string Id => "container-convert";
    public override string Name => "Конвертация контейнера";
    public override string Description => "Перемещение видео- и аудиодорожек в другой контейнер без перекодирования (-c copy)";
    public override string Category => "Видео";
    public override string Icon => "emblem-synchronizing-symbolic";
    public override HashSet<string> Extensions => Exts.AllMedia;

    public override List<Field> Fields(IDictionary<string, object> values = null) =>
    [
        new Field("target_format", "Целевой формат", Combo, "MP4", "Общие").Opts(Formats.Keys),
        Common.DeleteOriginal(),
    ];

    public override Result Process(QueueItem item, Context ctx)
    {
        var fmt = ctx.Str("target_format", "MP4");
        var outp = ctx.OutputPath(item.Path, Formats.GetValueOrDefault(fmt, ".mp4"));
        if (ctx.SkipIfExists(outp) is { } skip) return skip;
        var info = ctx.ProbeItem();
        var maps = new List<string>();
        var codecArgs = new List<string>();
        var notes = new List<string>();
        bool experimental = false;
        int subOut = 0;
        foreach (var t in info.MediaTracks)
        {
            string convertSub = null;
            if (fmt is "MP4" or "MOV")
            {
                if (t.Type == "subtitle")
                {
                    if (!TextSubs.Contains(t.Codec)) { notes.Add($"#{t.Index} {t.Codec} (нужен текстовый формат)"); continue; }
                    convertSub = "mov_text";
                }
                if (t.Type == "audio" && t.Codec is "truehd" or "mlp") experimental = true;
            }
            else if (fmt == "WEBM")
            {
                if ((t.Type == "video" && !WebmVideo.Contains(t.Codec)) || (t.Type == "audio" && !WebmAudio.Contains(t.Codec)))
                { notes.Add($"#{t.Index} {t.Codec}"); continue; }
                if (t.Type == "subtitle")
                {
                    if (!TextSubs.Contains(t.Codec)) { notes.Add($"#{t.Index} {t.Codec}"); continue; }
                    convertSub = t.Codec == "webvtt" ? null : "webvtt";
                }
            }
            else if (fmt == "AVI" && t.Type == "subtitle") { notes.Add($"#{t.Index} субтитры"); continue; }
            else if (fmt == "TS" && t.Type == "subtitle" && TextSubs.Contains(t.Codec)) { notes.Add($"#{t.Index} текстовые субтитры"); continue; }
            maps.AddRange(["-map", $"0:{t.Index}"]);
            if (t.Type == "subtitle")
            {
                if (convertSub != null) codecArgs.AddRange([$"-c:s:{subOut}", convertSub]);
                subOut++;
            }
        }
        if (maps.Count == 0) return Result.Failed("❌ Нет дорожек, совместимых с выбранным контейнером");
        var args = new List<string> { "-i", item.Path };
        args.AddRange(maps);
        if (fmt == "MKV" && info.Attachments.Count > 0) args.AddRange(["-map", "0:t?"]);
        args.AddRange(["-c", "copy"]);
        args.AddRange(codecArgs);
        args.AddRange(["-map_metadata", "0", "-map_chapters", "0"]);
        if (fmt is "MP4" or "MOV")
        {
            args.AddRange(["-movflags", "+faststart"]);
            if (info.Video.Count > 0 && info.Video[0].Codec == "hevc") args.AddRange(["-tag:v", "hvc1"]);
        }
        if (experimental) args.AddRange(["-strict", "-2"]);
        args.Add(outp);
        foreach (var n in notes) ctx.Log("WARNING", $"{item.Name}: дорожка {n} не поддерживается {fmt} — пропущена");
        var res = ctx.FFmpeg(args, info.Duration, $"Ремукс в {fmt}");
        var result = ctx.FinishFFmpeg(res, item.Path, outp, $"✅ Сконвертирован в {fmt}: {Common.Fn(outp)}");
        if (notes.Count > 0 && result.Status == "done")
        {
            result.Status = "partial";
            result.Messages.Insert(0, "⚠ Пропущены несовместимые дорожки: " + string.Join("; ", notes));
        }
        return result;
    }
}

public sealed class MetadataCleanup : Script
{
    public override string Id => "metadata-cleanup";
    public override string Name => "Очистка метаданных";
    public override string Description => "Удаление глобальных тегов, служебных заголовков и названий дорожек без перекодирования";
    public override string Category => "Видео";
    public override string Icon => "edit-clear-all-symbolic";
    public override HashSet<string> Extensions => Exts.AllMedia;

    public override List<Field> Fields(IDictionary<string, object> values = null) => Common.OutputFields();

    public override Result Process(QueueItem item, Context ctx)
    {
        var outp = ctx.OutputPath(item.Path);
        if (ctx.SkipIfExists(outp) is { } skip) return skip;
        var info = ctx.ProbeItem();
        ctx.Progress(0, "Очистка метаданных…");
        var args = new List<string> { "-i", item.Path, "-map", "0", "-map_metadata", "-1", "-map_metadata:s", "-1", "-map_chapters", "-1", "-c", "copy" };
        // Вложениям (шрифтам) MKV обязательны имя файла и MIME-тип — возвращаем только их
        foreach (var t in info.Attachments)
            args.AddRange([$"-metadata:s:{t.Index}", $"filename={(t.Filename != "" ? t.Filename : $"attachment_{t.Index}.ttf")}",
                $"-metadata:s:{t.Index}", $"mimetype={(t.Mimetype != "" ? t.Mimetype : "application/x-truetype-font")}"]);
        var low = outp.ToLowerInvariant();
        if (low.EndsWith(".mp4") || low.EndsWith(".mov") || low.EndsWith(".m4a")) args.AddRange(["-movflags", "+faststart"]);
        args.Add(outp);
        var res = ctx.FFmpeg(args, info.Duration, "Очистка метаданных");
        return ctx.FinishFFmpeg(res, item.Path, outp, $"✅ Очищены метаданные: {Common.Fn(outp)}");
    }
}
