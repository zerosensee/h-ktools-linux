using HKTools.Encoders;

namespace HKTools.Scripts;

public sealed class VideoEncode : Script
{
    static readonly int[] StandardHeights = [240, 360, 480, 720, 1080, 1440, 2160, 4320];

    public override string Id => "video-encode";
    public override string Name => "Кодирование видео";
    public override string Description => "Транскодирование с NVENC / VAAPI / x265 / x264 / SVT-AV1, вшиванием надписей, автокропом и масштабом";
    public override string Category => "Видео";
    public override string Icon => "video-x-generic-symbolic";
    public override HashSet<string> Extensions => Exts.VideoContainers;

    public static int MakeEven(int v) => v < 2 ? 2 : v / 2 * 2;

    public static int SnapHeight(int raw)
    {
        int best = StandardHeights[0];
        foreach (var c in StandardHeights)
            if (Math.Abs(raw - c) < Math.Abs(raw - best) || (Math.Abs(raw - c) == Math.Abs(raw - best) && c < best)) best = c;
        return best;
    }

    /// <summary>Целевое разрешение: высота приводится к стандартному ряду, ширина — по пропорциям базы.</summary>
    public static (int W, int H, string Error) ComputeScale(int baseW, int baseH, string factor)
    {
        if (baseW <= 0 || baseH <= 0) return (0, 0, "разрешение исходного видео неизвестно");
        if (!double.TryParse(factor.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var coef) || coef <= 0)
            return (0, 0, "некорректный коэффициент");
        int th = MakeEven(SnapHeight((int)Math.Round(baseH * coef, MidpointRounding.ToEven)));
        int tw = MakeEven((int)Math.Round((double)baseW * th / baseH, MidpointRounding.ToEven));
        if (tw == baseW && th == baseH) return (0, 0, "целевое разрешение совпадает с текущим");
        return (tw, th, null);
    }

    public override List<Field> Fields(IDictionary<string, object> values = null)
    {
        var encs = Encoder.AvailableList();
        var titles = encs.Select(e => e.Title).ToList();
        const string G = "Видео:Кодирование", F = "Видео:Фильтры";
        var o = new List<Field>
        {
            new Field("encoder", "Энкодер", Combo, titles[0], G).Opts(titles).Hint("NVENC — NVIDIA, VAAPI — AMD/Intel (через Mesa / intel-media-driver)"),
            new Field("output_container", "Контейнер файла", Combo, ".mkv", G).Opts(".mkv", ".mp4"),
            new Field("force_10bit", "Принудительно 10 бит (Main10)", Check, false, G)
                .Off(new Cond("cpu_codec", "AVC / H.264"), new Cond("nvenc_codec", "AVC / H.264"), new Cond("vaapi_codec", "AVC / H.264")),
            new Field("lossless", "Режим Lossless", Check, false, "Видео:Битрейт"),
        };
        foreach (var e in encs)
            foreach (var f in e.Fields())
            {
                f.Visible.Insert(0, new Cond("encoder", e.Title));
                o.Add(f);
            }
        o.AddRange([
            new Field("autocrop_enabled", "Автоматическая обрезка чёрных полос", Expander, false, F).Hint("Letterbox / Pillarbox по нескольким точкам видео").Kids(
                new Field("autocrop_limit", "Порог чёрного (limit)", Float, 0.094, F).Range(0, 1, 0.005, 3).Hint("0.094 ≈ 24/255"),
                new Field("autocrop_round", "Кратность сторон (round)", Int, 16L, F).Range(2, 64),
                new Field("autocrop_mode", "Режим детекции", Combo, "black", F).Opts("black", "mvedges"),
                new Field("autocrop_probe_frames", "Кадров для анализа", Int, 25L, F).Range(5, 300),
                new Field("autocrop_skip_frames", "Пропуск кадров детектора (skip)", Int, 2L, F).Range(0, 1000),
                new Field("autocrop_reset_frames", "Сброс детектора (reset)", Int, 0L, F).Range(0, 1000),
                new Field("autocrop_probe_points", "Количество точек анализа", Int, 3L, F).Range(1, 10),
                new Field("autocrop_tolerance", "Порог микрообрезки (px)", Int, 16L, F).Range(0, 128).Hint("Срез меньше порога игнорируется")),
            new Field("resize_enabled", "Изменение разрешения", Expander, false, F).Hint("Расчёт от кадра после обрезки, итог — к стандартному ряду (720p, 1080p…)").Kids(
                new Field("resize_factor", "Коэффициент масштаба", Combo, "0.5", F).Opts("0.25", "0.5", "0.75", "1.5", "2.0"),
                new Field("resize_algorithm", "Алгоритм", Combo, "lanczos", F).Opts("lanczos", "bicubic", "bilinear", "spline"),
                new Field("resize_allow_upscale", "Разрешить увеличение кадра", Check, false, F)),
            new Field("audio_codec", "Кодек аудио", Combo, "copy", "Аудио").Opts("copy", "aac", "ac3", "eac3", "opus", "flac"),
            new Field("audio_bitrate", "Битрейт аудио", Combo, "320k", "Аудио").Opts("128k", "192k", "256k", "320k", "448k", "640k")
                .When(new Cond("audio_codec", new[] { "aac", "ac3", "eac3", "opus" })),
            new Field("audio_channels", "Каналы", Combo, "Original", "Аудио").Opts("Original", "1", "2", "6").When(new Cond("audio_codec", "copy", true)),
            new Field("audio_lang_priority", "Приоритет языка аудио", Keywords, Kw(("rus", true), ("jpn", false), ("eng", false)), "Аудио")
                .Hint("Первая найденная по порядку; иначе — дорожка по умолчанию"),
            new Field("burn_in_subtitles", "Вшивать найденные надписи в видео (hardsub)", Check, true, "Субтитры"),
            new Field("sub_keywords", "Поиск надписей по названию дорожки", Keywords, Kw(("Надписи", true), ("Signs", true)), "Субтитры")
                .When(new Cond("burn_in_subtitles", true)),
            new Field("sub_not_found_action", "Если надписи не найдены", Combo, "Пропускать хардсаб", "Субтитры").Opts("Пропускать хардсаб", "Спрашивать")
                .When(new Cond("burn_in_subtitles", true)),
            new Field("strip_keywords", "Удалять строки субтитров, содержащие", Keywords,
                Kw((@"{\fad(500,500)\b1\an3\fnTahoma\fs50\shad3\bord1.3\4c&H000000&\4a&H00&}", false)), "Субтитры").When(new Cond("burn_in_subtitles", true)),
            new Field("overwrite_source", "Заменить исходный файл результатом", Check, false, "Общие").Warn(Common.ReplaceWarning.Item1, Common.ReplaceWarning.Item2),
        ]);
        return o;
    }

    public static List<object> Kw(params (string W, bool A)[] items) =>
        items.Select(i => (object)new Dictionary<string, object> { ["word"] = i.W, ["active"] = i.A, ["only_part"] = false }).ToList();

    public static Track PickAudio(MediaInfo info, List<string> langs)
    {
        var audio = info.Audio;
        foreach (var lang in langs)
        {
            var nl = Probe.NormalizeLang(lang.Trim());
            foreach (var t in audio)
            {
                var nt = Probe.NormalizeLang(t.Language);
                if (nt == nl || (nt != "und" && (nt.Contains(nl) || nl.Contains(nt)))) return t;
            }
        }
        return audio.FirstOrDefault(t => t.Default || t.Forced) ?? audio.FirstOrDefault();
    }

    static Track PickSubs(QueueItem item, Context ctx, MediaInfo info)
    {
        var words = ctx.Words("sub_keywords", true);
        var subs = info.Subtitles;
        foreach (var w in words)
            foreach (var t in subs)
                if (t.Title.ToLowerInvariant().Contains(w)) return t;
        if (ctx.Str("sub_not_found_action") == "Спрашивать" && subs.Count > 0 && ctx.Ask != null)
        {
            ctx.Progress(0, "Ожидание выбора субтитров…");
            return ctx.Ask("choose_subtitle", [item.Name, subs]) as Track;
        }
        return null;
    }

    static (int W, int H, string Filter)? DetectCrop(Context ctx, string path, int w, int h, double duration)
    {
        int points = ctx.Int("autocrop_probe_points", 3);
        var offsets = duration < 5 || points <= 1
            ? new List<double> { duration > 0 ? duration / 2 : 0 }
            : Enumerable.Range(1, points).Select(i => duration * i / (points + 1)).ToList();
        int rnd = ctx.Int("autocrop_round", 16), tol = ctx.Int("autocrop_tolerance", 16);
        int maxW = 0, maxH = 0;
        for (int n = 0; n < offsets.Count; n++)
        {
            ctx.Check();
            ctx.Progress(0, $"Анализ чёрных полос (точка {n + 1}/{offsets.Count})…");
            var found = FFmpeg.DetectCrop(path, offsets[n], ctx.Int("autocrop_probe_frames", 25), ctx.Dbl("autocrop_limit", 0.094), rnd,
                ctx.Int("autocrop_skip_frames", 2), ctx.Int("autocrop_reset_frames"), ctx.Str("autocrop_mode", "black"), ctx.Token);
            if (found == null) continue;
            var (cw, ch, _, _) = found.Value;
            if (w > 0 && h > 0 && cw >= w && ch >= h) return null; // в одной из точек кадр полноэкранный — обрезать нечего
            maxW = Math.Max(maxW, cw); maxH = Math.Max(maxH, ch);
        }
        if (maxW == 0 || maxH == 0) return null;
        if (rnd > 1)
        {
            maxW = (maxW + rnd - 1) / rnd * rnd;
            maxH = (maxH + rnd - 1) / rnd * rnd;
        }
        maxW += maxW % 2; maxH += maxH % 2;
        if (w > 0) { maxW = Math.Min(maxW, w); if (w - maxW > 0 && w - maxW <= tol) maxW = w; }
        if (h > 0) { maxH = Math.Min(maxH, h); if (h - maxH > 0 && h - maxH <= tol) maxH = h; }
        if (w > 0 && h > 0 && maxW == w && maxH == h) return null;
        int x = w > maxW ? (w - maxW) / 2 / 2 * 2 : 0;
        int y = h > maxH ? (h - maxH) / 2 / 2 * 2 : 0;
        return (maxW, maxH, $"crop={maxW}:{maxH}:{x}:{y}");
    }

    public override Result Process(QueueItem item, Context ctx)
    {
        var info = ctx.ProbeItem();
        if (info.Video.Count == 0) return Result.Failed("❌ В файле нет видеодорожки");
        var container = ctx.Str("output_container", ".mkv");
        if (!container.StartsWith('.')) container = "." + container;
        var outp = ctx.OutputPath(item.Path, container);
        if (ctx.SkipIfExists(outp) is { } skip) return skip;

        var encoder = Encoder.ById(ctx.Str("encoder", "CPU")) ?? Encoder.All[0];
        if (!encoder.Available) return Result.Failed($"❌ Энкодер «{encoder.Title}» недоступен на этой системе (см. «Зависимости»)");
        var v = ctx.Values;
        var ectx = new EncContext(v.Bool("lossless"), v.Bool("force_10bit"), container, Hw.PickVaapiDevice());
        bool hwFrames = encoder.Id == "vaapi" && v.Bool("vaapi_hwdecode");
        var notes = new List<string>();
        var tmp = ctx.TempDir("hk_venc_");
        var fontsDir = Path.Combine(tmp, "fonts");
        Directory.CreateDirectory(fontsDir);

        // 1. Надписи для вшивания
        Track subTrack = null;
        string subFile = null;
        if (v.Bool("burn_in_subtitles") && !hwFrames)
        {
            subTrack = PickSubs(item, ctx, info);
            if (subTrack != null && !subTrack.IsBitmapSub)
            {
                if (info.Fonts.Count > 0)
                {
                    ctx.Progress(0, "Извлечение шрифтов…");
                    FFmpeg.DumpAttachments(item.Path, fontsDir, ctx.Token);
                }
                subFile = Path.Combine(tmp, "subs.ass");
                ctx.Progress(0, "Извлечение субтитров…");
                var r = FFmpeg.Run(["-i", item.Path, "-map", $"0:{subTrack.Index}", "-c:s", "ass", subFile], ctx.Token, source: Name);
                ctx.Check();
                if (!r.Ok || !File.Exists(subFile))
                {
                    notes.Add("не удалось извлечь субтитры — хардсаб пропущен");
                    subFile = null; subTrack = null;
                }
                else
                {
                    var strip = ctx.Words("strip_keywords");
                    if (strip.Count > 0)
                    {
                        var lines = File.ReadAllLines(subFile);
                        var kept = lines.Where(ln => !strip.Any(ln.Contains)).ToList();
                        if (kept.Count != lines.Length)
                        {
                            Subs.WriteText(subFile, string.Join("\n", kept) + "\n");
                            ctx.Log("INFO", $"Удалено строк субтитров по фильтру: {lines.Length - kept.Count}");
                        }
                    }
                }
            }
            if (subTrack != null) ctx.Log("INFO", $"Вшиваются субтитры #{subTrack.Index}: {subTrack.Describe()}");
        }
        else if (v.Bool("burn_in_subtitles") && hwFrames) notes.Add("при аппаратном декодировании VAAPI хардсаб недоступен");

        // 2. Аудио
        var audio = PickAudio(info, ctx.Words("audio_lang_priority"));

        // 3. Фильтры: хардсаб → кроп → масштаб
        var vtrack = info.Video[0];
        int w = vtrack.Width, h = vtrack.Height, curW = w, curH = h;
        var chain = new List<string>();
        if (subFile != null) chain.Add($"subtitles=filename='{FFmpeg.FilterEscape(subFile)}':fontsdir='{FFmpeg.FilterEscape(fontsDir)}'");
        var badge = new List<string>();
        if (v.Bool("autocrop_enabled") && !hwFrames && DetectCrop(ctx, item.Path, w, h, info.Duration) is { } crop)
        {
            (curW, curH) = (crop.W, crop.H);
            chain.Add(crop.Filter);
            badge.Add($"{w}x{h} ➔ {curW}x{curH}");
            ctx.Log("INFO", $"Обрезка: {w}x{h} → {curW}x{curH}");
        }
        if (v.Bool("resize_enabled"))
        {
            var (tw, th, err) = ComputeScale(curW, curH, v.Str("resize_factor", "0.5"));
            if (err != null) notes.Add($"масштаб пропущен: {err}");
            else if ((tw > curW || th > curH) && !v.Bool("resize_allow_upscale")) notes.Add($"масштаб пропущен: увеличение до {tw}x{th} запрещено");
            else
            {
                chain.Add(hwFrames ? $"scale_vaapi=w={tw}:h={th}" : $"scale={tw}:{th}:flags={v.Str("resize_algorithm", "lanczos")}");
                badge.Add(badge.Count > 0 ? $"➔ {tw}x{th}" : $"{w}x{h} ➔ {tw}x{th}");
            }
        }
        if (badge.Count > 0) item.Badge = string.Join(" ", badge);
        chain.AddRange(encoder.FilterSuffix(v, ectx));

        // 4. Команда
        var args = new List<string>(encoder.InputArgs(v, ectx)) { "-i", item.Path };
        if (subTrack != null && subTrack.IsBitmapSub)
        {
            var graph = $"[0:{vtrack.Index}][0:{subTrack.Index}]overlay" + (chain.Count > 0 ? "," + string.Join(",", chain) : "") + "[vout]";
            args.AddRange(["-filter_complex", graph, "-map", "[vout]"]);
        }
        else
        {
            if (chain.Count > 0) args.AddRange(["-vf", string.Join(",", chain)]);
            args.AddRange(["-map", $"0:{vtrack.Index}"]);
        }
        if (audio != null) args.AddRange(["-map", $"0:{audio.Index}"]);
        args.AddRange(encoder.Args(v, ectx));
        var acodec = v.Str("audio_codec", "copy");
        args.AddRange(["-c:a", acodec == "opus" ? "libopus" : acodec]);
        if (acodec != "copy")
        {
            if (acodec is "aac" or "ac3" or "eac3" or "opus") args.AddRange(["-b:a", v.Str("audio_bitrate", "320k")]);
            var ch = v.Str("audio_channels", "Original");
            if (ch == "2" && audio != null && audio.Channels > 2) args.AddRange(["-af", FFmpeg.StereoDownmixPan]);
            else if (ch is not ("Original" or "2")) args.AddRange(["-ac", ch]);
            if (acodec == "opus" && audio != null && audio.Channels > 2 && ch == "Original")
            {
                if (audio.ChannelLayout == "5.1(side)") args.AddRange(["-af", "channelmap=channel_layout=5.1"]);
                args.AddRange(["-mapping_family", "1"]);
            }
        }
        if (encoder.Tag(v, ectx) is { } tag) args.AddRange(["-tag:v", tag]);
        if (container == ".mp4") args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-map_metadata", "-1", "-map_chapters", "0", outp]);

        foreach (var n in notes) ctx.Log("WARNING", $"{item.Name}: {n}");
        ctx.Progress(0, "Кодирование видео…");
        var res = ctx.FFmpeg(args, info.Duration, "Кодирование");
        if (!(res.Ok && Common.NonEmpty(outp)))
        {
            Context.RemoveQuiet(outp);
            return Result.Failed($"❌ ОШИБКА кодирования: {item.Name}\n{res.ErrorText()}");
        }
        var result = Result.Done($"✅ ОБРАБОТАНО: {Common.Fn(outp)}");
        result.Outputs = [ctx.FinalizeSource(item.Path, outp, result)];
        return result;
    }
}
