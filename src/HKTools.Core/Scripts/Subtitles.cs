using System.Text.RegularExpressions;

namespace HKTools.Scripts;

/// <summary>Состояние реплики после фильтров. Reason: "" | actor | style | effect | regex | caps-empty | manual-ex | manual-in | empty</summary>
public sealed record LineState(int Index, Dialogue Dialogue, string Text, bool Deleted, string Reason);

public static class SubFilters
{
    /// <summary>Применить фильтры конвертации субтитров к документу (используется и в предпросмотре).</summary>
    public static List<LineState> Apply(SubDoc doc, IDictionary<string, object> values, ISet<int> manualIn = null, ISet<int> manualEx = null)
    {
        manualIn ??= new HashSet<int>();
        manualEx ??= new HashSet<int>();
        var exActors = values.Words("exclude_actors").ToHashSet();
        var exStyles = values.Words("exclude_styles").ToHashSet();
        var exEffects = values.Words("exclude_effects").ToHashSet();
        bool stripFmt = values.Bool("strip_formatting", true);
        bool stripCaps = values.Bool("strip_caps");
        var patterns = new List<(Regex Rx, bool OnlyPart)>();
        foreach (var p in KwItem.Parse(values.Get("text_patterns")))
        {
            if (!p.Active || p.Word == "") continue;
            try { patterns.Add((new Regex(p.Word), p.OnlyPart)); } catch (ArgumentException) { }
        }
        var outp = new List<LineState>();
        for (int i = 0; i < doc.Dialogues.Count; i++)
        {
            var d = doc.Dialogues[i];
            if (d.Comment) continue;
            if (Subs.StripTags(d.Text).Trim() == "") { outp.Add(new(i, d, d.Text, true, "empty")); continue; }
            var reason = "";
            if (d.Actor != "" && exActors.Contains(d.Actor)) reason = "actor";
            else if (d.Style != "" && exStyles.Contains(d.Style)) reason = "style";
            else if (d.Effect != "" && exEffects.Contains(d.Effect)) reason = "effect";
            var text = d.Text;
            bool regexDeleted = false;
            foreach (var (rx, only) in patterns)
            {
                if (!rx.IsMatch(text)) continue;
                if (only) text = rx.Replace(text, "");
                else { regexDeleted = true; break; }
            }
            if (stripCaps) text = Subs.StripCaps(text);
            if (stripFmt) text = Subs.TagRe().Replace(text, "");
            bool emptyAfter = Subs.StripTags(text).Trim() == "";
            if (manualIn.Contains(i) && emptyAfter) { text = d.Text; emptyAfter = false; }
            if (manualEx.Contains(i)) outp.Add(new(i, d, text, true, "manual-ex"));
            else if (manualIn.Contains(i)) outp.Add(new(i, d, text, false, "manual-in"));
            else if (regexDeleted) outp.Add(new(i, d, text, true, "regex"));
            else if (reason != "") outp.Add(new(i, d, text, true, reason));
            else if (emptyAfter) outp.Add(new(i, d, text, true, "caps-empty"));
            else outp.Add(new(i, d, text, false, ""));
        }
        return outp;
    }

    public static HashSet<int> IntSet(object raw) =>
        raw is System.Collections.IEnumerable e and not string ? e.Cast<object>().Select(o => (int)Json.ToLong(o)).ToHashSet() : new HashSet<int>();
}

public sealed class SubtitlesConvert : Script
{
    public static readonly HashSet<string> SubIn = [".ass", ".ssa", ".srt", ".vtt"];
    static readonly Dictionary<string, string> TargetExt = new() { ["WebVTT"] = ".vtt", ["SRT"] = ".srt", ["ASS"] = ".ass" };

    public override string Id => "subtitles-convert";
    public override string Name => "Конвертация субтитров";
    public override string Description => "ASS/SSA/SRT/VTT → WebVTT, SRT или ASS с фильтром по актёрам и стилям, очисткой тегов и предпросмотром";
    public override string Category => "Субтитры";
    public override string Icon => "font-x-generic-symbolic";
    public override HashSet<string> Extensions => SubIn;
    public override string[] Deps => [];

    public override List<Field> Fields(IDictionary<string, object> values = null) =>
    [
        new Field("target_format", "Целевой формат", Combo, "WebVTT", "Экспорт").Opts("WebVTT", "SRT", "ASS"),
        new Field("strip_formatting", "Удалять теги оформления", Check, true, "Очистка").Hint(@"Полная очистка override-тегов {\…}"),
        new Field("keep_styles", "Сохранять курсив/жирный (SRT/VTT)", Check, false, "Очистка").Hint(@"Переносит \i1, \b1 и курсив стилей ASS в <i>/<b>"),
        new Field("strip_caps", "Удалять реплики в верхнем регистре (КАПС)", Check, false, "Очистка"),
        new Field("text_patterns", "Регулярные выражения", Keywords, new List<object>(), "Очистка")
            .Hint("Строки с совпадением удаляются; с флагом — удаляется только совпадение").Flag("only_part", "только совпадение"),
        new Field("exclude_actors", "Исключённые актёры", Keywords, new List<object>(), "Фильтр").Hint("Удобнее отмечать в окне «Предпросмотр и фильтр»"),
        new Field("exclude_styles", "Исключённые стили", Keywords, new List<object>(), "Фильтр"),
        new Field("exclude_effects", "Исключённые эффекты", Keywords, new List<object>(), "Фильтр"),
        Common.DeleteOriginal(),
    ];

    public override Result Process(QueueItem item, Context ctx)
    {
        var target = ctx.Str("target_format", "WebVTT");
        var ext = TargetExt.GetValueOrDefault(target, ".vtt");
        var outp = ctx.OutputPath(item.Path, ext);
        if (ctx.SkipIfExists(outp) is { } skip) return skip;
        var doc = Subs.Load(item.Path);
        if (doc.Dialogues.Count == 0) return Result.Skipped("⏭ В файле нет реплик");
        var states = SubFilters.Apply(doc, ctx.Values, SubFilters.IntSet(item.Extra.GetValueOrDefault("manual_in")),
            SubFilters.IntSet(item.Extra.GetValueOrDefault("manual_ex")));
        var kept = states.Where(s => !s.Deleted).Select(s => { var c = s.Dialogue.Clone(); c.Text = s.Text; return c; }).ToList();
        int removed = states.Count(s => s.Deleted && s.Reason != "empty");
        Subs.WriteText(outp, Subs.Render(doc, ext, kept, ctx.Bool("keep_styles")));
        var result = Result.Done($"✅ {target}: {Common.Fn(outp)} (реплик: {kept.Count}, удалено: {removed})", outp);
        ctx.FinalizeSource(item.Path, outp, result);
        ctx.Progress(100, "Готово");
        return result;
    }
}

public sealed class SubtitleShift : Script
{
    public override string Id => "subtitle-shift";
    public override string Name => "Сдвиг субтитров";
    public override string Description => "Сдвиг тайминга ASS, SSA, SRT, VTT на заданное время с сохранением оформления";
    public override string Category => "Субтитры";
    public override string Icon => "preferences-system-time-symbolic";
    public override HashSet<string> Extensions => SubtitlesConvert.SubIn;
    public override string[] Deps => [];

    public override List<Field> Fields(IDictionary<string, object> values = null) =>
    [
        new Field("ShiftMs", "Величина сдвига (мс или Ч:ММ:СС.сс)", Text, "1000", "Настройки сдвига"),
        new Field("ShiftDirection", "Направление сдвига", Combo, "Вперед", "Настройки сдвига").Opts("Вперед", "Назад"),
    ];

    public override Result Process(QueueItem item, Context ctx)
    {
        int ms;
        try { ms = Subs.ParseShift(ctx.Str("ShiftMs", "0")); }
        catch (Exception ex) { return Result.Failed($"❌ {ex.Message}"); }
        if (ctx.Str("ShiftDirection") == "Назад") ms = -ms;
        var outp = ctx.OutputPath(item.Path);
        if (ctx.SkipIfExists(outp) is { } skip) return skip;
        var raw = Subs.ReadText(item.Path);
        var (text, n) = Subs.ShiftText(raw, Subs.DetectKind(item.Path, raw), ms);
        Subs.WriteText(outp, text);
        ctx.Progress(100, "Готово");
        return Result.Done($"✅ Сдвиг {ms:+0;-0;+0} мс ({n} реплик): {Common.Fn(outp)}", outp);
    }
}
