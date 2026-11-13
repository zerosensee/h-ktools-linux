using System.Text.RegularExpressions;

namespace HKTools.Scripts;

public sealed class MkvAssembly : Script
{
    const string Delims = ".-_ ([{";
    static readonly Regex TokenSplit = new(@"[._\-\s()\[\]{}]+");
    static readonly HashSet<string> Signs = ["sign", "signs", "sings", "songs", "song", "надписи", "надпись", "титры", "forced"];
    static readonly HashSet<string> Full = ["full", "полные", "полный", "complete", "main", "dialogue", "dialogs"];
    static readonly HashSet<string> Langs = ["en", "eng", "ru", "rus", "de", "ger", "deu", "fr", "fre", "fra", "es", "spa", "it", "ita", "ja", "jpn",
        "uk", "ukr", "pl", "pol", "pt", "por", "zh", "chi", "zho", "ko", "kor", "tr", "tur", "ar", "ara", "hi", "hin", "cs", "cze", "ces", "hu", "hun",
        "bg", "bul", "sr", "srp", "he", "heb", "nl", "dut", "nld", "sv", "swe"];
    public static readonly Dictionary<string, string> RoleLabel = new() { ["signs"] = "Надписи", ["full"] = "Полные", ["other"] = "Субтитры" };
    static readonly Dictionary<string, int> RoleOrder = new() { ["signs"] = 0, ["full"] = 1, ["other"] = 2 };

    public override string Id => "mkv-assembly";
    public override string Name => "Сборка MKV";
    public override string Description => "Сборка MKV/MP4 из видео, внешних аудио и субтитров с сопоставлением по имени файла";
    public override string Category => "Контейнеры";
    public override string Icon => "package-x-generic-symbolic";
    public override HashSet<string> Extensions => Exts.Union(Exts.VideoContainers, Exts.AudioAll, Exts.Subtitles);
    public override string[] Deps => ["ffmpeg", "ffprobe", "mkvmerge"];

    public static bool IsCompanion(string videoStem, string compStem)
    {
        var v = videoStem.ToLowerInvariant();
        var c = compStem.ToLowerInvariant();
        if (c == v) return true;
        return c.Length > v.Length && c.StartsWith(v, StringComparison.Ordinal) && Delims.Contains(c[v.Length]);
    }

    static bool Belongs(string videoStem, QueueItem item) =>
        item.Extra.GetValueOrDefault("mux_pinned") is string pinned && pinned != ""
            ? pinned.Equals(videoStem, StringComparison.OrdinalIgnoreCase)
            : IsCompanion(videoStem, item.Stem);

    static List<string> Remainder(string videoStem, string compStem) =>
        !IsCompanion(videoStem, compStem) || compStem.Length == videoStem.Length
            ? new()
            : TokenSplit.Split(compStem[videoStem.Length..]).Where(t => t != "").ToList();

    public static string SubsRole(string videoStem, QueueItem item)
    {
        if (item.Extra.GetValueOrDefault("mux_role") is string o && RoleLabel.ContainsKey(o)) return o;
        var tokens = Remainder(videoStem, item.Stem).Select(t => t.ToLowerInvariant()).ToList();
        if (tokens.Count == 0) return "full";
        if (tokens.Any(Signs.Contains)) return "signs";
        if (tokens.Any(Full.Contains)) return "full";
        return "other";
    }

    public static string InferTitle(string videoStem, string compStem)
    {
        var tokens = Remainder(videoStem, compStem);
        return tokens.Count == 0 || tokens.All(t => Langs.Contains(t.ToLowerInvariant())) ? "" : string.Join(" ", tokens);
    }

    public static (List<QueueItem> Audio, List<(QueueItem Item, string Role)> Subs) Companions(QueueItem video, List<QueueItem> items)
    {
        var audio = new List<QueueItem>();
        var subs = new List<(QueueItem, string)>();
        foreach (var it in items)
        {
            if (it == video || Exts.VideoContainers.Contains(it.Ext) || !Belongs(video.Stem, it)) continue;
            if (Exts.AudioAll.Contains(it.Ext)) audio.Add(it);
            else if (Exts.Subtitles.Contains(it.Ext)) subs.Add((it, SubsRole(video.Stem, it)));
        }
        audio = audio.OrderBy(i => Json.ToBool(i.Extra.GetValueOrDefault("mux_main")) ? 0 : 1).ThenBy(i => i.Path.ToLowerInvariant(), StringComparer.Ordinal).ToList();
        subs = subs.OrderBy(p => RoleOrder[p.Item2]).ThenBy(p => p.Item1.Path.ToLowerInvariant(), StringComparer.Ordinal).ToList();
        return (audio, subs);
    }

    public static QueueItem OwnerVideo(QueueItem item, List<QueueItem> items)
    {
        QueueItem best = null;
        foreach (var it in items)
            if (Exts.VideoContainers.Contains(it.Ext) && Belongs(it.Stem, item) && (best == null || it.Stem.Length > best.Stem.Length)) best = it;
        return best;
    }

    static string Sanitize(object t) => Regex.Replace(t?.ToString() ?? "", @"[\r\n\t]+", " ").Trim();

    public override List<Field> Fields(IDictionary<string, object> values = null) =>
    [
        new Field("output_container", "Формат контейнера", Combo, "MKV", "Сборка").Opts("MKV", "MP4"),
        new Field("clean_tracks", "Удалить из видео дорожки, заменяемые внешними", Check, true, "Сборка")
            .Hint("Встроенные аудио/субтитры убираются, если найдены внешние того же типа"),
        new Field("position_before_builtin", "Новые дорожки перед встроенными", Check, false, "Сборка").When(new Cond("clean_tracks", false)),
        new Field("external_lang", "Язык внешних дорожек", Text, "rus", "Сборка"),
        new Field("subs_full_title", "Заголовок полных субтитров", Text, "Субтитры", "Субтитры"),
        new Field("subs_signs_title", "Заголовок надписей", Text, "Надписи", "Субтитры"),
        new Field("audio_title_prompt", "Спрашивать заголовок аудио при добавлении", Expander, false, "Аудио")
            .Hint("Заготовки показываются в диалоге для быстрого выбора").Kids(
                new Field("audio_title_default", "Заголовок по умолчанию", Text, "", "Аудио").Hint("Применяется без вопроса, если диалог выключен"),
                new Field("audio_title_presets", "Заготовленные заголовки", Keywords, new List<object>(), "Аудио")),
    ];

    public override List<QueueItem> PrepareBatch(List<QueueItem> items, Dictionary<string, object> values) =>
        items.Where(i => Exts.VideoContainers.Contains(i.Ext)).ToList();

    public override string SkipReason(QueueItem item) => "Сопутствующий файл — собирается вместе с видео";

    public override string RowSubtitle(QueueItem item, List<QueueItem> items)
    {
        if (Exts.VideoContainers.Contains(item.Ext))
        {
            var (audio, subs) = Companions(item, items);
            if (audio.Count == 0 && subs.Count == 0) return "внешних дорожек не найдено";
            var parts = new List<string>();
            if (audio.Count > 0) parts.Add($"аудио: {audio.Count}");
            if (subs.Count > 0) parts.Add("субтитры: " + string.Join(", ", subs.Select(s => RoleLabel[s.Role])));
            return string.Join(" · ", parts);
        }
        var owner = OwnerVideo(item, items);
        if (owner == null) return "⚠ не найдено видео с таким именем";
        var label = "";
        if (Exts.Subtitles.Contains(item.Ext)) label = $" ({RoleLabel[SubsRole(owner.Stem, item)]})";
        else if (Json.ToBool(item.Extra.GetValueOrDefault("mux_main"))) label = " (основная)";
        if (item.Extra.GetValueOrDefault("mux_title") is string t && t != "") label += $" «{t}»";
        return $"→ {owner.Name}{label}";
    }

    static string EmbeddedTitle(string path)
    {
        try
        {
            var info = Probe.Run(path);
            return info.Audio.Count > 0 && info.Audio[0].Title != "" ? info.Audio[0].Title : info.Title;
        }
        catch { return ""; }
    }

    public override Result Process(QueueItem item, Context ctx)
    {
        var (audioItems, subs) = Companions(item, ctx.Batch.Items);
        bool isMp4 = ctx.Str("output_container", "MKV").ToUpperInvariant() == "MP4";
        var outp = ctx.OutputPath(item.Path, isMp4 ? ".mp4" : ".mkv");
        if (ctx.SkipIfExists(outp) is { } skip) return skip;
        bool clean = ctx.Bool("clean_tracks", true);
        var lang = ctx.Str("external_lang", "rus");
        if (lang == "") lang = "und";
        var fullTitle = ctx.Str("subs_full_title", "Субтитры");
        var signsTitle = ctx.Str("subs_signs_title", "Надписи");
        var defaultTitle = Sanitize(ctx.Str("audio_title_default"));
        if (audioItems.Count == 0 && subs.Count == 0) ctx.Log("WARNING", $"{item.Name}: внешних дорожек не найдено, будет выполнен только ремукс");
        if (isMp4) return Mp4(item, ctx, outp, audioItems, subs, clean, lang);

        var cmd = new List<string> { Tools.Require("mkvmerge"), "-o", outp, "--title", item.Stem };
        if (clean)
        {
            if (audioItems.Count > 0) cmd.Add("--no-audio");
            if (subs.Count > 0) cmd.Add("--no-subtitles");
            cmd.AddRange(["--no-global-tags", "--no-track-tags"]);
        }
        cmd.Add(item.Path);
        for (int n = 0; n < audioItems.Count; n++)
        {
            var a = audioItems[n];
            var main = n == 0 ? "yes" : "no";
            var title = Sanitize(a.Extra.GetValueOrDefault("mux_title"));
            if (title == "") title = defaultTitle;
            if (title == "") title = EmbeddedTitle(a.Path);
            if (title == "") title = InferTitle(item.Stem, a.Stem);
            cmd.AddRange(["--no-video", "--no-subtitles", "--language", $"-1:{lang}", "--default-track-flag", $"-1:{main}", "--forced-display-flag", $"-1:{main}"]);
            if (title != "") cmd.AddRange(["--track-name", $"-1:{title}"]);
            cmd.Add(a.Path);
        }
        bool signsDefault = false;
        foreach (var (s, role) in subs)
        {
            bool isDef = role == "signs" && !signsDefault;
            signsDefault |= role == "signs";
            var title = role == "signs" ? signsTitle : fullTitle;
            var d = isDef ? "yes" : "no";
            cmd.AddRange(["--language", $"-1:{lang}", "--default-track-flag", $"-1:{d}", "--forced-display-flag", $"-1:{d}"]);
            if (s.Ext is ".srt" or ".ass" or ".ssa" or ".vtt") cmd.AddRange(["--sub-charset", "-1:UTF-8"]);
            if (title != "") cmd.AddRange(["--track-name", $"-1:{title}"]);
            cmd.Add(s.Path);
        }
        if (!clean && (audioItems.Count > 0 || subs.Count > 0))
        {
            try
            {
                var tracks = Probe.MkvTracks(item.Path);
                var video = tracks.FirstOrDefault(t => t.Type == "video");
                bool hasVideo = video.Type != null;
                var order = hasVideo ? new List<string> { $"0:{video.Id}" } : new List<string>();
                var nw = Enumerable.Range(1, audioItems.Count + subs.Count).Select(i => $"{i}:0").ToList();
                var builtin = tracks.Where(t => !hasVideo || t.Id != video.Id).Select(t => $"0:{t.Id}").ToList();
                order.AddRange(ctx.Bool("position_before_builtin") ? nw.Concat(builtin) : builtin.Concat(nw));
                cmd.AddRange(["--track-order", string.Join(",", order)]);
            }
            catch (Exception ex) { ctx.Log("WARNING", $"Порядок дорожек по умолчанию: {ex.Message}"); }
        }
        var res = MkvRun.Run(ctx, cmd, "Сборка MKV");
        if (res.Ok && File.Exists(outp))
        {
            ctx.Progress(100, "Готово");
            return Result.Done($"✅ Собран MKV ({audioItems.Count} аудио, {subs.Count} субтитров): {Common.Fn(outp)}", outp);
        }
        Context.RemoveQuiet(outp);
        return Result.Failed($"❌ Ошибка сборки MKV\n{res.ErrorText()}");
    }

    Result Mp4(QueueItem item, Context ctx, string outp, List<QueueItem> audioItems, List<(QueueItem Item, string Role)> subs, bool clean, string lang)
    {
        var skipped = new List<string>();
        var audioOk = new List<QueueItem>();
        foreach (var a in audioItems)
            if (a.Ext is ".flac" or ".thd" or ".truehd" or ".dts" or ".dtshd" or ".mlp") skipped.Add($"{a.Name} (формат не для MP4)");
            else audioOk.Add(a);
        var subsOk = new List<(QueueItem Item, string Role)>();
        foreach (var s in subs)
            if (s.Item.Ext is ".srt" or ".vtt") subsOk.Add(s);
            else skipped.Add($"{s.Item.Name} (ASS/SSA/PGS не поддерживаются MP4)");
        var args = new List<string> { "-i", item.Path };
        foreach (var a in audioOk) args.AddRange(["-i", a.Path]);
        foreach (var s in subsOk) args.AddRange(["-i", s.Item.Path]);
        args.AddRange(["-map", "0:v"]);
        if (audioOk.Count > 0) for (int i = 0; i < audioOk.Count; i++) args.AddRange(["-map", $"{1 + i}:a"]);
        else if (!clean) args.AddRange(["-map", "0:a?"]);
        int start = 1 + audioOk.Count;
        for (int i = 0; i < subsOk.Count; i++) args.AddRange(["-map", $"{start + i}:s"]);
        args.AddRange(["-c", "copy"]);
        if (subsOk.Count > 0) args.AddRange(["-c:s", "mov_text"]);
        for (int i = 0; i < audioOk.Count; i++)
            args.AddRange([$"-metadata:s:a:{i}", $"language={lang}", $"-disposition:a:{i}", i == 0 ? "default" : "0"]);
        for (int i = 0; i < subsOk.Count; i++)
            args.AddRange([$"-metadata:s:s:{i}", $"language={lang}", $"-metadata:s:s:{i}", $"title={RoleLabel[subsOk[i].Role]}"]);
        args.AddRange(["-movflags", "+faststart", outp]);
        double duration = 0;
        try { duration = Probe.Run(item.Path).Duration; } catch { }
        var res = ctx.FFmpeg(args, duration, "Сборка MP4");
        if (res.Ok && File.Exists(outp))
        {
            var r = Result.Done($"✅ Собран MP4: {Common.Fn(outp)}", outp);
            if (skipped.Count > 0)
            {
                r.Status = "partial";
                r.Messages.Insert(0, "⚠ Пропущено: " + string.Join("; ", skipped));
            }
            return r;
        }
        Context.RemoveQuiet(outp);
        return Result.Failed($"❌ Ошибка сборки MP4\n{res.ErrorText()}");
    }
}
