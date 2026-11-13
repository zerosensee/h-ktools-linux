using System.Text.RegularExpressions;

namespace HKTools.Scripts;

/// <summary>Общие помощники для инструментов с выбором дорожек.</summary>
public static class TrackSel
{
    public static readonly Dictionary<string, string> TypeChar = new() { ["video"] = "v", ["audio"] = "a", ["subtitle"] = "s" };

    public static string Summary(QueueItem item)
    {
        var info = item.Info;
        if (info == null) return "";
        return $"выбрано дорожек: {item.Selected.Count} из {info.MediaTracks.Count + info.Fonts.Count}";
    }

    public static string Disposition(Track t)
    {
        var flags = new List<string>();
        if (t.Default) flags.Add("default");
        if (t.Forced) flags.Add("forced");
        if (t.HearingImpaired) flags.Add("hearing_impaired");
        if (t.Commentary) flags.Add("comment");
        if (t.Original) flags.Add("original");
        return flags.Count > 0 ? string.Join("+", flags) : "0";
    }

    public static List<int> DefaultSelection(Script s, MediaInfo info) =>
        s.TrackSelection == "extract" ? info.MediaTracks.Select(t => t.Index).Concat(info.Fonts.Select(t => t.Index)).ToList() : new();
}

public sealed class StreamManagement : Script
{
    public override string Id => "stream-management";
    public override string Name => "Управление потоками";
    public override string Description => "Удаление или сохранение выбранных дорожек (видео, аудио, субтитры) в MKV и MP4 без перекодирования";
    public override string Category => "Контейнеры";
    public override string Icon => "view-list-symbolic";
    public override HashSet<string> Extensions => Exts.Union(Exts.VideoContainers, [".mka", ".m4a"]);
    public override string TrackSelection => "manage";

    public override List<Field> Fields(IDictionary<string, object> values = null)
    {
        var f = new List<Field>
        {
            new Field("mode", "Режим работы", Combo, "Удалить выбранные", "Режим").Opts("Удалить выбранные", "Сохранить только выбранные"),
            new Field("use_m4a", "Одиночную аудиодорожку сохранять в M4A", Check, false, "Аудио").Hint("Если в результате остаётся ровно одна аудиодорожка"),
        };
        f.AddRange(Common.OutputFields());
        return f;
    }

    public override string RowSubtitle(QueueItem item, List<QueueItem> all) => TrackSel.Summary(item);

    public override Result Process(QueueItem item, Context ctx)
    {
        var info = ctx.ProbeItem();
        var sel = item.Selected.ToHashSet();
        bool keepMode = ctx.Str("mode") == "Сохранить только выбранные";
        var media = info.MediaTracks;
        var kept = media.Where(t => sel.Contains(t.Index) == keepMode).ToList();
        if (sel.Count == 0) return Result.Skipped("⏭ Дорожки не выбраны — откройте «Дорожки» у файла");
        if (kept.Count == 0) return Result.Failed("❌ После операции не осталось ни одной дорожки");
        if (kept.Count == media.Count) return Result.Skipped("⏭ Нечего менять: набор дорожек совпадает с исходным");
        bool singleAudio = kept.Count == 1 && kept[0].Type == "audio";
        var ext = item.Ext;
        var codecArgs = new List<string> { "-c", "copy" };
        if (singleAudio && ctx.Bool("use_m4a"))
        {
            ext = ".m4a";
            if (kept[0].Codec is not ("aac" or "alac" or "ac3" or "eac3" or "mp3" or "opus" or "flac")) codecArgs = ["-c:a", "aac", "-b:a", "256k"];
        }
        var outp = ctx.OutputPath(item.Path, ext);
        if (ctx.SkipIfExists(outp) is { } skip) return skip;
        var args = new List<string> { "-i", item.Path };
        foreach (var t in kept) args.AddRange(["-map", $"0:{t.Index}"]);
        if (ext == ".mkv" && info.Attachments.Count > 0) args.AddRange(["-map", "0:t?"]);
        args.AddRange(codecArgs);
        args.AddRange(["-map_metadata", "0", "-map_chapters", "0"]);
        if (ext is ".mp4" or ".m4a" or ".mov") args.AddRange(["-movflags", "+faststart"]);
        args.Add(outp);
        var res = ctx.FFmpeg(args, info.Duration, "Перепаковка");
        return ctx.FinishFFmpeg(res, item.Path, outp, $"✅ Удалено дорожек: {media.Count - kept.Count}, осталось: {kept.Count} — {Common.Fn(outp)}");
    }
}

public sealed class StreamReplacement : Script
{
    public override string Id => "stream-replacement";
    public override string Name => "Замена потоков";
    public override string Description => "Замена дорожек в MKV/MP4 на внешние файлы с сохранением языка, названий и флагов";
    public override string Category => "Контейнеры";
    public override string Icon => "view-refresh-symbolic";
    public override HashSet<string> Extensions => Exts.VideoContainers;
    public override string TrackSelection => "replace";

    public override List<Field> Fields(IDictionary<string, object> values = null) => Common.OutputFields();

    public override string RowSubtitle(QueueItem item, List<QueueItem> all)
    {
        var reps = item.Replacements;
        return reps.Count == 0 ? "замены не заданы" : "замен: " + string.Join(", ", reps.OrderBy(k => k.Key).Select(kv => $"#{kv.Key} ← {Path.GetFileName(kv.Value)}"));
    }

    public override Result Process(QueueItem item, Context ctx)
    {
        var reps = item.Replacements.Where(kv => File.Exists(kv.Value)).OrderBy(kv => kv.Key).ToList();
        if (reps.Count == 0) return Result.Skipped("⏭ Замены не заданы — откройте «Дорожки» у файла");
        var info = ctx.ProbeItem();
        var outp = ctx.OutputPath(item.Path);
        if (ctx.SkipIfExists(outp) is { } skip) return skip;
        bool isMp4 = item.Ext is ".mp4" or ".mov" or ".m4v";
        var args = new List<string> { "-i", item.Path };
        var inputOf = new Dictionary<int, int>();
        int n = 1;
        foreach (var (idx, path) in reps) { args.AddRange(["-i", path]); inputOf[idx] = n++; }
        var meta = new List<string>();
        int outIdx = 0;
        foreach (var t in info.MediaTracks)
        {
            if (inputOf.TryGetValue(t.Index, out var inp))
            {
                var ch = TrackSel.TypeChar.GetValueOrDefault(t.Type, "a");
                args.AddRange(["-map", $"{inp}:{ch}:0"]);
                meta.AddRange([$"-metadata:s:{outIdx}", $"language={(t.Language != "" ? t.Language : "und")}",
                    $"-metadata:s:{outIdx}", $"title={t.Title}", $"-disposition:{outIdx}", TrackSel.Disposition(t)]);
                if (isMp4 && t.Type == "subtitle") meta.AddRange([$"-c:s:{info.Subtitles.Count(x => x.Index < t.Index)}", "mov_text"]);
            }
            else args.AddRange(["-map", $"0:{t.Index}"]);
            outIdx++;
        }
        if (item.Ext == ".mkv" && info.Attachments.Count > 0) args.AddRange(["-map", "0:t?"]);
        args.AddRange(["-c", "copy"]);
        args.AddRange(meta);
        args.AddRange(["-map_metadata", "0", "-map_chapters", "0"]);
        if (isMp4) args.AddRange(["-movflags", "+faststart"]);
        args.Add(outp);
        var res = ctx.FFmpeg(args, info.Duration, "Замена потоков");
        return ctx.FinishFFmpeg(res, item.Path, outp, $"✅ Заменено дорожек: {reps.Count} — {Common.Fn(outp)}");
    }
}

public sealed class TrackExtractor : Script
{
    static readonly Regex Sanitize = new(@"[\\/:*?""<>|\x00-\x1f]");

    public override string Id => "track-extractor";
    public override string Name => "Разборка контейнера";
    public override string Description => "Массовое извлечение видео, аудио, субтитров и шрифтов из MKV/MP4 с авто-именованием";
    public override string Category => "Контейнеры";
    public override string Icon => "document-save-symbolic";
    public override HashSet<string> Extensions => Exts.Union(Exts.VideoContainers, [".mka", ".mks"]);
    public override string TrackSelection => "extract";

    public override List<Field> Fields(IDictionary<string, object> values = null) =>
    [
        new Field("name_format", "Шаблон имени файла", Text, "{original}", "Именование")
            .Hint("Теги: {original} {id} {type} {lang} {title} {codec}. Совпадающие имена получат _trackNN"),
        new Field("create_subfolders", "Отдельная подпапка для каждого файла", Check, false, "Папки"),
        new Field("fonts_subfolder", "Шрифты — в подпапку «fonts»", Check, true, "Папки"),
    ];

    public override string RowSubtitle(QueueItem item, List<QueueItem> all) => TrackSel.Summary(item);

    public static string CleanName(string name)
    {
        name = Sanitize.Replace(name.Trim().Trim('\'', '"'), "");
        name = Regex.Replace(name, "_{2,}", "_");
        name = Regex.Replace(name, @"\.{2,}", ".");
        name = name.Trim(' ', '.', '_', '-');
        return name == "" ? "track" : name;
    }

    public static string FormatName(string template, string stem, Track t, string suffix)
    {
        var lang = t.Language != "" && t.Language != "und" ? t.Language : "";
        if (suffix != "") lang = lang != "" ? $"{lang}_{suffix}" : suffix;
        var type = t.Type switch { "video" => "video", "audio" => "audio", "subtitle" => "subs", _ => t.Type };
        var name = template.Replace("{original}", stem).Replace("{id}", t.Index.ToString()).Replace("{lang}", lang)
            .Replace("{title}", t.Title).Replace("{codec}", t.Codec).Replace("{type}", type);
        name = Regex.Replace(name, @"[_\-. ]{2,}", m => m.Value[0].ToString());
        return CleanName(name);
    }

    public override Result Process(QueueItem item, Context ctx)
    {
        var info = ctx.ProbeItem();
        var sel = item.Selected.ToHashSet();
        var tracks = info.MediaTracks.Where(t => sel.Contains(t.Index)).ToList();
        var fonts = info.Attachments.Where(t => sel.Contains(t.Index)).ToList();
        if (tracks.Count == 0 && fonts.Count == 0) return Result.Skipped("⏭ Дорожки не выбраны");
        var baseDir = ctx.TargetDir();
        if (ctx.Bool("create_subfolders")) baseDir = Path.Combine(baseDir, item.Stem);
        Directory.CreateDirectory(baseDir);
        var template = ctx.Str("name_format", "{original}");
        if (template == "") template = "{original}";
        var langs = tracks.Select(t => t.Language).Where(l => l != "" && l != "und").ToList();
        var dupLangs = langs.Where(l => langs.Count(x => x == l) > 1).ToHashSet();
        var reserved = new HashSet<string>();
        var args = new List<string> { "-i", item.Path };
        var planned = new List<string>();
        var skipped = new List<string>();
        var vobsub = new List<(Track T, string P)>();
        foreach (var t in tracks)
        {
            var ext = Probe.RawExtension(t);
            var suffix = dupLangs.Contains(t.Language) && t.Title != "" ? CleanName(t.Title).Replace(" ", "_") : "";
            var name = FormatName(template, item.Stem, t, suffix) + ext;
            var path = Path.Combine(baseDir, name);
            if (reserved.Contains(path))
            {
                path = Path.Combine(baseDir, $"{Path.GetFileNameWithoutExtension(name)}_track{t.Index:00}{Path.GetExtension(name)}");
                ctx.Log("WARNING", $"Одинаковое имя: дорожка #{t.Index} сохранена как {Path.GetFileName(path)} (добавьте {{id}} в шаблон)");
            }
            reserved.Add(path);
            if (Common.NonEmpty(path) && !ctx.Overwrite) { skipped.Add(Path.GetFileName(path)); continue; }
            if (t.Codec == "dvd_subtitle" && item.Ext == ".mkv" && Tools.Which("mkvextract") != null)
            {
                vobsub.Add((t, Path.ChangeExtension(path, ".idx")));
                continue;
            }
            var ch = TrackSel.TypeChar.GetValueOrDefault(t.Type, "a");
            string codec;
            if (t.Type == "audio" && t.Codec.StartsWith("pcm_")) codec = t.Codec is "pcm_bluray" or "pcm_dvd" || t.BitDepth > 16 ? "pcm_s24le" : "copy";
            else if (t.Type == "subtitle") codec = Probe.SubConvert.GetValueOrDefault(t.Codec, "copy");
            else codec = "copy";
            args.AddRange(["-map", $"0:{t.Index}", $"-c:{ch}", codec]);
            if (ext is ".h265" or ".h264") args.AddRange(["-f", ext == ".h265" ? "hevc" : "h264"]);
            args.Add(path);
            planned.Add(path);
        }
        var outputs = new List<string>();
        var result = Result.Done();
        if (planned.Count > 0)
        {
            var res = ctx.FFmpeg(args, info.Duration, "Извлечение дорожек", span: fonts.Count > 0 ? 90 : 100);
            foreach (var p in planned)
                if (Common.NonEmpty(p)) outputs.Add(p);
                else { Context.RemoveQuiet(p); result.Messages.Add($"❌ Не извлечено: {Path.GetFileName(p)}"); }
            if (!res.Ok) result.Messages.Add(res.ErrorText(6));
        }
        if (vobsub.Count > 0)
        {
            var cmd = new List<string> { Tools.Require("mkvextract"), item.Path, "tracks" };
            cmd.AddRange(vobsub.Select(v => $"{v.T.Index}:{v.P}"));
            var r = Proc.Run(cmd, ctx.Token, source: Name);
            outputs.AddRange(vobsub.Where(v => File.Exists(v.P)).Select(v => v.P));
            if (!r.Ok) result.Messages.Add("⚠ mkvextract завершился с ошибкой (VobSub)");
        }
        if (fonts.Count > 0)
        {
            var fontDir = ctx.Bool("fonts_subfolder", true) ? Path.Combine(baseDir, "fonts") : baseDir;
            Directory.CreateDirectory(fontDir);
            for (int n = 0; n < fonts.Count; n++)
            {
                var f = fonts[n];
                ctx.Check();
                ctx.Progress(90 + 10.0 * n / fonts.Count, $"Шрифт {f.Filename}");
                var name = CleanName(f.Filename != "" ? f.Filename : $"font_{f.Index}.ttf");
                var path = Path.Combine(fontDir, name);
                if (reserved.Contains(path)) path = Path.Combine(fontDir, $"{Path.GetFileNameWithoutExtension(name)}_{f.Index}{Path.GetExtension(name)}");
                reserved.Add(path);
                if (Common.NonEmpty(path) && !ctx.Overwrite) { skipped.Add(name); continue; }
                if (FFmpeg.DumpAttachment(item.Path, f.Index, path, ctx.Token)) outputs.Add(path);
                else result.Messages.Add($"❌ Не извлечён шрифт: {name}");
            }
        }
        if (skipped.Count > 0) result.Messages.Insert(0, $"⏭ Уже существуют, пропущены: {skipped.Count}");
        result.Outputs = outputs;
        int failed = result.Messages.Count(m => m.StartsWith("❌"));
        if (outputs.Count == 0) result.Status = skipped.Count > 0 && failed == 0 ? "skipped" : "failed";
        else if (failed > 0) result.Status = "partial";
        result.Messages.Add($"✅ Извлечено файлов: {outputs.Count} → {baseDir}");
        ctx.Progress(100, "Готово");
        return result;
    }
}
