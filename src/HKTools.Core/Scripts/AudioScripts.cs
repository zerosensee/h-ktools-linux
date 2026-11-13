using System.Text.RegularExpressions;

namespace HKTools.Scripts;

public sealed class AudioEncode : Script
{
    // формат → (расширение, кодек ffmpeg)
    public static readonly Dictionary<string, (string Ext, string Codec)> Formats = new()
    {
        ["AAC (FDK-AAC)"] = (".m4a", "fdkaac"), ["QAAC (Wine)"] = (".m4a", "qaac"), ["AAC (FFmpeg)"] = (".m4a", "aac"),
        ["FLAC"] = (".flac", "flac"), ["WAV"] = (".wav", "pcm_s24le"), ["AC3"] = (".ac3", "ac3"), ["EAC3"] = (".eac3", "eac3"),
        ["MP3"] = (".mp3", "libmp3lame"), ["OPUS"] = (".opus", "libopus"), ["OGG"] = (".ogg", "libvorbis"), ["DTS"] = (".dts", "dca"),
        ["WavPack"] = (".wv", "wavpack"), ["ALAC"] = (".m4a", "alac"), ["WMA"] = (".wma", "wmav2"), ["AIFF"] = (".aiff", "pcm_s16be"),
        ["ADPCM"] = (".wav", "adpcm_ima_wav"),
    };
    // порядок для UI
    static readonly string[] FormatOrder = ["AAC (FDK-AAC)", "QAAC (Wine)", "AAC (FFmpeg)", "FLAC", "WAV", "AC3", "EAC3", "MP3", "OPUS", "OGG", "DTS", "WavPack", "ALAC", "WMA", "AIFF", "ADPCM"];
    static readonly Dictionary<string, string> WavDepths = new()
    {
        ["16-bit"] = "pcm_s16le", ["24-bit"] = "pcm_s24le", ["32-bit"] = "pcm_s32le", ["32-bit Float"] = "pcm_f32le",
    };
    static readonly string[] BitrateFormats = ["AAC (FFmpeg)", "MP3", "OGG", "AC3", "EAC3", "DTS", "WMA", "OPUS", "ADPCM"];

    public override string Id => "audio-encode";
    public override string Name => "Кодирование аудио";
    public override string Description => "Перекодирование в FDK-AAC, FLAC, WAV, AC3/E-AC3, Opus, MP3 и др. (QAAC — через Wine)";
    public override string Category => "Аудио";
    public override string Icon => "audio-x-generic-symbolic";
    public override HashSet<string> Extensions => Exts.AllMedia;

    public override List<Field> Fields(IDictionary<string, object> values = null)
    {
        const string E = "Экспорт", P = "Экспорт:Параметры кодирования";
        var fdk = new Cond("target_format", "AAC (FDK-AAC)");
        var qaac = new Cond("target_format", "QAAC (Wine)");
        return
        [
            new Field("target_format", "Целевой формат", Combo, "AAC (FDK-AAC)", E).Opts(FormatOrder),
            new Field("use_m4a_container", "Упаковать AAC в контейнер M4A", Check, true, E)
                .When(new Cond("target_format", new[] { "AAC (FDK-AAC)", "QAAC (Wine)", "AAC (FFmpeg)" }))
                .Hint("Без контейнера (ADTS) плееры и Telegram могут показывать неверную длительность")
                .Warn("Внимание!", "Без контейнера M4A плееры, файловые менеджеры и Telegram могут показывать неправильную (огромную) длительность. Сам звук при этом в порядке."),
            new Field("fdk_mode", "Режим FDK-AAC", Combo, "VBR (-m)", P).Opts("VBR (-m)", "CBR (-b)", "HE-AAC (-p 5)", "HE-AAC v2 (-p 29)").When(fdk),
            new Field("fdk_vbr", "Качество VBR (1–5)", Combo, "5", P).Opts("1", "2", "3", "4", "5").Hint("5 ≈ 224+ кбит/с стерео, 4 ≈ 128–160")
                .When(fdk, new Cond("fdk_mode", "VBR (-m)")),
            new Field("fdk_bitrate", "Битрейт (кбит/с)", Combo, "192", P).Opts("32", "48", "64", "80", "96", "128", "160", "192", "224", "256", "320")
                .When(fdk, new Cond("fdk_mode", "VBR (-m)", true)),
            new Field("fdk_afterburner", "Afterburner (лучше качество, медленнее)", Check, true, P).When(fdk),
            new Field("qaac_mode", "Режим кодирования QAAC", Combo, "True VBR (-V)", P)
                .Opts("True VBR (-V)", "Constrained VBR (-v)", "ABR (-a)", "CBR (-c)", "HE AAC (--he)").When(qaac)
                .Hint("Нужен qaac64.exe + Wine: путь задаётся в Настройках"),
            new Field("qaac_quality", "Качество True VBR [0–127]", Combo, "127", P).Opts("0", "16", "32", "48", "64", "80", "90", "96", "112", "127")
                .When(qaac, new Cond("qaac_mode", "True VBR (-V)")),
            new Field("qaac_bitrate", "Битрейт AAC (кбит/с)", Combo, "192", P).Opts("0", "64", "96", "128", "160", "192", "224", "256", "320")
                .Hint("0 — максимальный доступный").When(qaac, new Cond("qaac_mode", "True VBR (-V)", true)),
            new Field("qaac_advanced", "Дополнительные параметры QAAC", Expander, null, P).NoToggle().When(qaac).Kids(
                new Field("qaac_no_delay", "Компенсировать задержку энкодера (--no-delay)", Check, false, P),
                new Field("qaac_limiter", "Смарт-лимитер (--limiter)", Check, false, P)),
            new Field("bitrate", "Битрейт (кбит/с)", Combo, "320k", P).Opts("64k", "96k", "128k", "160k", "192k", "224k", "256k", "320k", "448k", "640k")
                .When(new Cond("target_format", BitrateFormats)).Hint("Для OGG битрейт ограничивается по числу каналов"),
            new Field("compression", "Уровень сжатия (FLAC 0–12, WavPack 0–8)", Combo, "5", P).Opts(Enumerable.Range(0, 13).Select(i => i.ToString()))
                .When(new Cond("target_format", new[] { "FLAC", "WavPack" })),
            new Field("wav_bit_depth", "Битность WAV", Combo, "24-bit", P).Opts(WavDepths.Keys).When(new Cond("target_format", "WAV")),
            new Field("audio_track", "Аудиодорожка", Combo, "Первая", "Общие").Opts("Первая", "Все (отдельные файлы)"),
            Common.DeleteOriginal(),
        ];
    }

    public override Result Process(QueueItem item, Context ctx)
    {
        var info = ctx.ProbeItem();
        var tracks = info.Audio;
        if (tracks.Count == 0) return Result.Failed("❌ В файле нет аудиодорожек");
        if (ctx.Str("audio_track") != "Все (отдельные файлы)") tracks = tracks.Take(1).ToList();
        var fmt = ctx.Str("target_format", "AAC (FDK-AAC)");
        var (ext, codec) = Formats.GetValueOrDefault(fmt, (".m4a", "aac"));
        if (codec is "fdkaac" or "qaac" or "aac" && !ctx.Bool("use_m4a_container", true)) ext = ".aac";
        var result = Result.Done();
        for (int n = 0; n < tracks.Count; n++)
        {
            var track = tracks[n];
            var suffix = tracks.Count > 1 ? $"_a{track.TypeIndex}" : "";
            var outp = ctx.OutputPath(item.Path, ext, suffix);
            if (ctx.SkipIfExists(outp) is { } skip) { result.Messages.AddRange(skip.Messages); continue; }
            double @base = n * 100.0 / tracks.Count, span = 100.0 / tracks.Count;
            bool ok; string err;
            if (codec == "fdkaac")
                (ok, err) = Pipe(ctx, item.Path, track.Index, outp, FdkCmd(ctx, outp), info.Duration, @base, span);
            else if (codec == "qaac")
            {
                var q = Tools.QaacCommand();
                if (q == null || q.Count == 0)
                    return Result.Failed("❌ QAAC не настроен: укажите команду (например «wine ~/qaac/qaac64.exe») в Настройки → Внешние утилиты, либо выберите «AAC (FDK-AAC)»");
                (ok, err) = Pipe(ctx, item.Path, track.Index, outp, QaacCmd(ctx, q, outp), info.Duration, @base, span, Path.GetDirectoryName(outp));
            }
            else
            {
                var args = new List<string> { "-i", item.Path, "-map", $"0:{track.Index}", "-vn", "-sn", "-map_metadata", "-1" };
                args.AddRange(FfmpegCodecArgs(ctx.Values, fmt, codec, track.Channels, track.ChannelLayout));
                args.Add(outp);
                var res = ctx.FFmpeg(args, info.Duration, $"Кодирование {fmt}", @base: @base, span: span);
                (ok, err) = (res.Ok, res.ErrorText());
            }
            if (ok && Common.NonEmpty(outp))
            {
                result.Outputs.Add(outp);
                result.Messages.Add($"✅ {fmt}: {Common.Fn(outp)}");
            }
            else
            {
                Context.RemoveQuiet(outp);
                result.Status = result.Outputs.Count == 0 ? "failed" : "partial";
                result.Messages.Add($"❌ Ошибка кодирования дорожки #{track.Index}\n{err}");
            }
        }
        if (result.Outputs.Count == 0 && result.Status == "done") result.Status = "skipped";
        if (result.Outputs.Count > 0 && result.Status == "done") ctx.FinalizeSource(item.Path, result.Outputs[0], result);
        return result;
    }

    public static List<string> FfmpegCodecArgs(IDictionary<string, object> v, string fmt, string codec, int channels, string layout = "")
    {
        var pre = new List<string>();
        if (fmt == "OPUS" && channels > 2)
        {
            // libopus принимает только «тыловые» раскладки Vorbis: 5.1(side) → 5.1 без смешивания каналов
            if (layout == "5.1(side)") pre.AddRange(["-af", "channelmap=channel_layout=5.1"]);
            pre.AddRange(["-mapping_family", "1"]);
        }
        if (fmt == "WMA" && channels > 2) pre.AddRange(["-af", FFmpeg.StereoDownmixPan]);
        pre.AddRange(CodecOnly(v, fmt, codec, channels));
        return pre;
    }

    static int Kbps(string br) => int.TryParse(br.TrimEnd('k'), out var k) ? k : 320;

    static List<string> CodecOnly(IDictionary<string, object> v, string fmt, string codec, int channels)
    {
        if (fmt == "WAV") return ["-c:a", WavDepths.GetValueOrDefault(v.Str("wav_bit_depth", "24-bit"), "pcm_s24le")];
        var a = new List<string> { "-c:a", codec };
        var br = v.Str("bitrate", "320k");
        if (fmt is "FLAC" or "WavPack")
        {
            int level = v.Int("compression", 5);
            a.AddRange(["-compression_level", (fmt == "WavPack" ? Math.Min(level, 8) : level).ToString()]);
        }
        else if (BitrateFormats.Contains(fmt))
        {
            if (fmt == "OGG") br = $"{Math.Min(Kbps(br), channels == 1 ? 224 : channels == 2 ? 500 : 1000)}k";
            if (fmt == "DTS" && channels > 2 && Kbps(br) < 768) br = "768k"; // кодер dca не принимает меньший битрейт для многоканала
            a.AddRange(["-b:a", br]);
        }
        if (fmt == "DTS") a.AddRange(["-strict", "-2"]);
        if (fmt == "AAC (FFmpeg)" && v.Bool("use_m4a_container", true)) a.AddRange(["-movflags", "+faststart"]);
        return a;
    }

    static List<string> FdkCmd(Context ctx, string outp)
    {
        var cmd = new List<string> { Tools.Require("fdkaac"), "--ignorelength", "-S" };
        var mode = ctx.Str("fdk_mode", "VBR (-m)");
        if (mode.StartsWith("VBR")) cmd.AddRange(["-m", ctx.Str("fdk_vbr", "5")]);
        else
        {
            cmd.AddRange(["-b", ctx.Str("fdk_bitrate", "192")]);
            if (mode.Contains("29")) cmd.AddRange(["-p", "29"]);
            else if (mode.Contains("-p 5")) cmd.AddRange(["-p", "5"]);
        }
        cmd.AddRange(["-a", ctx.Bool("fdk_afterburner", true) ? "1" : "0"]);
        if (outp.EndsWith(".aac")) cmd.AddRange(["-f", "2"]); // ADTS
        cmd.AddRange(["-o", outp, "-"]);
        return cmd;
    }

    static List<string> QaacCmd(Context ctx, List<string> qaac, string outp)
    {
        var mode = ctx.Str("qaac_mode", "True VBR (-V)");
        var cmd = new List<string>(qaac) { "--ignorelength", "--threading" };
        var br = ctx.Str("qaac_bitrate", "192");
        if (mode.StartsWith("True VBR")) cmd.AddRange(["-V", ctx.Str("qaac_quality", "127")]);
        else if (mode.StartsWith("Constrained")) cmd.AddRange(["-v", br]);
        else if (mode.StartsWith("ABR")) cmd.AddRange(["-a", br]);
        else if (mode.StartsWith("CBR")) cmd.AddRange(["-c", br]);
        else cmd.AddRange(["--he", "-v", br]);
        if (ctx.Bool("qaac_no_delay")) cmd.Add("--no-delay");
        if (ctx.Bool("qaac_limiter")) cmd.Add("--limiter");
        if (outp.EndsWith(".aac")) cmd.Add("--adts");
        cmd.AddRange(["-o", Path.GetFileName(outp), "-"]);
        return cmd;
    }

    (bool, string) Pipe(Context ctx, string src, int stream, string outp, List<string> consumer, double duration, double @base, double span, string cwd = null)
    {
        var producer = new List<string> { FFmpeg.Bin, "-hide_banner", "-nostdin", "-loglevel", "error", "-progress", "pipe:2", "-nostats",
            "-i", src, "-map", $"0:{stream}", "-vn", "-sn", "-c:a", "pcm_s24le", "-f", "wav", "-" };
        if (File.Exists(outp) && ctx.Overwrite) File.Delete(outp);
        void OnErr(string line)
        {
            if (!line.StartsWith("out_time_us=") || duration <= 0) return;
            if (!long.TryParse(line.AsSpan(12), out var us)) return;
            var pct = Math.Min(100, us / 1e6 / duration * 100);
            ctx.Progress(@base + pct * span / 100, $"Кодирование AAC · {Common.F(pct, "0.0")}%");
        }
        var res = Proc.Pipeline(producer, consumer, ctx.Token, OnErr, cwd, Name);
        if (res.Cancelled) throw new CancelledException();
        var errors = res.StderrTail.Where(l => !Regex.IsMatch(l, @"^\s*\w+=")).ToList();
        return (res.Ok, string.Join("\n", errors.TakeLast(10)));
    }
}

public sealed class AudioDownmix : Script
{
    static readonly string[] Modes = ["FFmpeg (коэффициенты HandBrake)", "FFmpeg + нормализация EBU R128", "Dolby Pro Logic II (матричное)"];
    static readonly Dictionary<string, (string Ext, string Codec)> Out = new()
    {
        ["E-AC3"] = (".eac3", "eac3"), ["AC3"] = (".ac3", "ac3"), ["AAC"] = (".m4a", "aac"), ["FLAC"] = (".flac", "flac"), ["Opus"] = (".opus", "libopus"),
    };

    public override string Id => "audio-downmix";
    public override string Name => "Даунмикс в Stereo";
    public override string Description => "Сведение 5.1/7.1 в стерео 2.0 (DDP/DD/AAC/FLAC) — HandBrake-матрица, EBU R128 или Pro Logic II";
    public override string Category => "Аудио";
    public override string Icon => "audio-speakers-symbolic";
    public override HashSet<string> Extensions => Exts.AllMedia;

    public override List<Field> Fields(IDictionary<string, object> values = null)
    {
        const string G = "Параметры сведения";
        return
        [
            new Field("DownmixMode", "Режим сведения", Combo, Modes[0], G).Opts(Modes).Hint("В Linux вместо Dolby Encoding Engine — точная матрица ffmpeg"),
            new Field("OutputFormat", "Формат вывода", Combo, "E-AC3", G).Opts("E-AC3", "AC3", "AAC", "FLAC", "Opus"),
            new Field("Bitrate", "Битрейт (кбит/с)", Combo, "256", G).Opts("128", "192", "224", "256", "320", "384", "448", "640")
                .When(new Cond("OutputFormat", new[] { "E-AC3", "AC3", "AAC", "Opus" })),
            new Field("Suffix", "Суффикс файла", Text, "_stereo", "Общие"),
            Common.DeleteOriginal("Общие", "DeleteOriginal"),
        ];
    }

    public override Result Process(QueueItem item, Context ctx)
    {
        var info = ctx.ProbeItem();
        if (info.Audio.Count == 0) return Result.Failed("❌ В файле нет аудиодорожки");
        var track = info.Audio[0];
        var fmt = ctx.Str("OutputFormat", "E-AC3");
        var (ext, codec) = Out.GetValueOrDefault(fmt, (".eac3", "eac3"));
        var outp = ctx.OutputPath(item.Path, ext, ctx.Str("Suffix", "_stereo"));
        if (ctx.SkipIfExists(outp) is { } skip) return skip;
        var mode = ctx.Str("DownmixMode", Modes[0]);
        if (track.Channels <= 2) ctx.Log("WARNING", $"{item.Name}: дорожка уже {track.Channels}-канальная — будет просто перекодирована");
        var af = new List<string>();
        var extra = new List<string>();
        if (mode.StartsWith("Dolby")) { af.Add("aresample=matrix_encoding=dplii"); extra.AddRange(["-ac", "2"]); }
        else if (track.Channels > 2) af.Add(FFmpeg.StereoDownmixPan);
        if (mode.Contains("R128"))
        {
            af.Add("loudnorm=I=-23:TP=-1:LRA=11");
            extra.AddRange(["-ar", (track.SampleRate > 0 ? track.SampleRate : 48000).ToString()]);
        }
        var args = new List<string> { "-i", item.Path, "-map", $"0:{track.Index}", "-vn", "-sn", "-map_metadata", "-1" };
        if (af.Count > 0) args.AddRange(["-af", string.Join(",", af)]);
        args.AddRange(extra);
        args.AddRange(["-c:a", codec]);
        if (codec != "flac") args.AddRange(["-b:a", $"{ctx.Str("Bitrate", "256")}k"]);
        if (ext == ".m4a") args.AddRange(["-movflags", "+faststart"]);
        args.Add(outp);
        var res = ctx.FFmpeg(args, info.Duration, "Сведение в стерео");
        return ctx.FinishFFmpeg(res, item.Path, outp, $"✅ Стерео: {Common.Fn(outp)}");
    }
}

public sealed class AudioSpeed : Script
{
    const double Ntsc = 24000.0 / 1001;
    static readonly (string Name, double Src, double Dst)[] Modes =
    [
        ("Slowdown (25.000 → 23.976)", 25, Ntsc), ("Speedup (23.976 → 25.000)", Ntsc, 25), ("Custom (24.000 → 23.976)", 24, Ntsc),
        ("Custom (23.976 → 24.000)", Ntsc, 24), ("Custom (25.000 → 24.000)", 25, 24), ("Custom (29.970 → 23.976)", 30000.0 / 1001, Ntsc),
    ];

    public override string Id => "audio-speed";
    public override string Name => "Изменение скорости аудио";
    public override string Description => "PAL ↔ NTSC: изменение скорости как в eac3to (с тоном) или с сохранением тона (rubberband)";
    public override string Category => "Аудио";
    public override string Icon => "media-seek-forward-symbolic";
    public override HashSet<string> Extensions => Exts.AllMedia;

    public override List<Field> Fields(IDictionary<string, object> values = null)
    {
        const string G = "Настройки скорости";
        return
        [
            new Field("SpeedMode", "Режим преобразования", Combo, Modes[0].Name, G).Opts(Modes.Select(m => m.Name)),
            new Field("KeepPitch", "Сохранять тон (time-stretch)", Check, false, G).Hint("Выключено — как eac3to: скорость и тон меняются вместе (ресэмплинг)"),
            new Field("OutputFormat", "Формат вывода", Combo, "FLAC", G).Opts("FLAC", "WAV"),
            Common.DeleteOriginal("Общие", "DeleteOriginal"),
        ];
    }

    public override Result Process(QueueItem item, Context ctx)
    {
        var info = ctx.ProbeItem();
        if (info.Audio.Count == 0) return Result.Failed("❌ В файле нет аудиодорожки");
        var track = info.Audio[0];
        var mode = Modes.FirstOrDefault(m => m.Name == ctx.Str("SpeedMode"));
        if (mode.Name == null) mode = Modes[0];
        double factor = mode.Dst / mode.Src; // >1 — ускорение
        var fmt = ctx.Str("OutputFormat", "FLAC");
        var outp = ctx.OutputPath(item.Path, fmt == "FLAC" ? ".flac" : ".wav");
        if (ctx.SkipIfExists(outp) is { } skip) return skip;
        int sr = track.SampleRate > 0 ? track.SampleRate : 48000;
        string flt = ctx.Bool("KeepPitch")
            ? (Hw.HasFilter("rubberband") ? "rubberband=tempo=" : "atempo=") + Common.F(factor, "0.0000000000")
            : $"asetrate={Common.F(sr * factor, "0.000000")},aresample={sr}" + (Hw.HasFilter("aresample") ? ":resampler=soxr" : "");
        var codec = fmt == "FLAC" ? "flac" : ((track.BitDepth > 0 ? track.BitDepth : 24) > 16 ? "pcm_s24le" : "pcm_s16le");
        var args = new List<string> { "-i", item.Path, "-map", $"0:{track.Index}", "-vn", "-sn", "-af", flt, "-c:a", codec, "-map_metadata", "-1", outp };
        var res = ctx.FFmpeg(args, info.Duration > 0 ? info.Duration / factor : 0, $"Скорость ×{Common.F(factor, "0.00000")}");
        return ctx.FinishFFmpeg(res, item.Path, outp, $"✅ {Common.F(mode.Src, "0.000")} → {Common.F(mode.Dst, "0.000")} fps: {Common.Fn(outp)}");
    }
}
