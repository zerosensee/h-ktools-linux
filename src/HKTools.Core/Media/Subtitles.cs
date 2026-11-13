using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace HKTools.Media;

public sealed class Dialogue
{
    public int Start, End;
    public string Text = "", Style = "Default", Actor = "", Effect = "", Layer = "0", MarginL = "0", MarginR = "0", MarginV = "0";
    public bool Comment;
    public Dialogue Clone() => (Dialogue)MemberwiseClone();
    public string Plain => Subs.StripTags(Text);
}

public sealed record StyleInfo(string Name, bool Bold, bool Italic, bool Underline);

public sealed class SubDoc
{
    public string Kind = "ass";
    public List<Dialogue> Dialogues = new();
    public string Header = "";
    public Dictionary<string, StyleInfo> Styles = new();
    public List<string> EventsFormat = new();
    public string Trailer = "";

    public List<string> Actors => Dialogues.Select(d => d.Actor).Where(a => a != "").Distinct().OrderBy(a => a).ToList();
    public List<string> StyleNames => Dialogues.Select(d => d.Style).Where(a => a != "").Distinct().OrderBy(a => a).ToList();
}

/// <summary>Разбор и запись ASS/SSA, SRT, WebVTT без внешних зависимостей.</summary>
public static partial class Subs
{
    [GeneratedRegex(@"\{[^}]*\}")] public static partial Regex TagRe();
    [GeneratedRegex(@"</?[a-zA-Z][^>]*>")] private static partial Regex HtmlTagRe();
    [GeneratedRegex(@"<\d{1,2}:\d{2}(?::\d{2})?[.,]\d{3}>")] private static partial Regex VttTsRe();
    [GeneratedRegex(@"(\\N|\\n)")] private static partial Regex NlSplitRe();
    [GeneratedRegex(@"(\\N|\\n){2,}")] private static partial Regex DupNlRe();
    [GeneratedRegex(@"^(?:(\d+):)?(\d{1,2}):(\d{1,2})(?:[.,](\d{1,3}))?$")] private static partial Regex TimeRe();
    [GeneratedRegex(@"(\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}|\d{1,2}:\d{2}[,.]\d{1,3})\s*-->\s*(\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}|\d{1,2}:\d{2}[,.]\d{1,3})")]
    private static partial Regex SrtTimeLine();

    public static readonly string[] DefaultFormat = ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];

    public const string MinimalAssHeader =
        "[Script Info]\nScriptType: v4.00+\nWrapStyle: 0\nScaledBorderAndShadow: yes\nPlayResX: 1920\nPlayResY: 1080\n\n" +
        "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, " +
        "Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
        "Style: Default,Arial,64,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,3,1,2,60,60,50,1\n\n" +
        "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";

    static Subs() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }

    /// <summary>UTF-8 (с BOM/без), UTF-16, иначе CP1251 — как в Windows-версии.</summary>
    public static string ReadText(string path)
    {
        var raw = File.ReadAllBytes(path);
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) return Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
        if (raw.Length >= 2 && ((raw[0] == 0xFF && raw[1] == 0xFE) || (raw[0] == 0xFE && raw[1] == 0xFF)))
            return (raw[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(raw, 2, raw.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(raw); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1251).GetString(raw); }
    }

    // --- время ---
    public static int ParseTime(string text)
    {
        text = text.Trim();
        if (Regex.IsMatch(text, @"^-?\d+$")) return int.Parse(text);
        var m = TimeRe().Match(text);
        if (!m.Success) throw new FormatException($"Неверный формат времени: {text}");
        int ms = m.Groups[4].Success ? int.Parse(m.Groups[4].Value.PadRight(3, '0')[..3]) : 0;
        int h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
        return ((h * 60 + int.Parse(m.Groups[2].Value)) * 60 + int.Parse(m.Groups[3].Value)) * 1000 + ms;
    }

    /// <summary>Сдвиг: мс, «1,5» (секунды) или Ч:ММ:СС.сс.</summary>
    public static int ParseShift(string text)
    {
        text = (text ?? "").Trim().Replace(" ", "");
        if (text == "") return 0;
        int sign = text.StartsWith('-') ? -1 : 1;
        text = text.TrimStart('+', '-');
        if (Regex.IsMatch(text, @"^\d+$")) return sign * int.Parse(text);
        if (Regex.IsMatch(text, @"^\d+[.,]\d+$"))
            return sign * (int)Math.Round(double.Parse(text.Replace(',', '.'), FFmpeg.Inv) * 1000);
        return sign * ParseTime(text);
    }

    public static string FmtAss(int ms)
    {
        ms = Math.Max(0, ms);
        int cs = (ms + 5) / 10;
        return $"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}";
    }

    public static string FmtSrt(int ms, char sep = ',')
    {
        ms = Math.Max(0, ms);
        return $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}{sep}{ms % 1000:000}";
    }

    public static string FmtHuman(int ms)
    {
        var sign = ms < 0 ? "-" : "";
        ms = Math.Abs(ms);
        return $"{sign}{ms / 3600000}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}";
    }

    // --- чтение ---
    public static string DetectKind(string path, string text = null)
    {
        var low = path.ToLowerInvariant();
        if (low.EndsWith(".srt")) return "srt";
        if (low.EndsWith(".vtt")) return "vtt";
        if (text != null && text.TrimStart('\ufeff', ' ', '\r', '\n', '\t').StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)) return "vtt";
        return "ass";
    }

    public static SubDoc Load(string path)
    {
        var text = ReadText(path);
        return DetectKind(path, text) switch { "srt" => ParseSrt(text), "vtt" => ParseVtt(text), _ => ParseAss(text) };
    }

    public static SubDoc ParseAss(string text)
    {
        var doc = new SubDoc { Kind = "ass" };
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var header = new List<string>();
        var trailer = new List<string>();
        string section = "";
        var fmt = DefaultFormat.ToList();
        var styleFmt = new List<string>();
        bool eventsDone = false;
        foreach (var line in lines)
        {
            var st = line.Trim();
            if (st.StartsWith('[') && st.EndsWith(']'))
            {
                if (section == "[events]") eventsDone = true;
                section = st.ToLowerInvariant();
                (eventsDone ? trailer : header).Add(line);
                continue;
            }
            if (eventsDone) { trailer.Add(line); continue; }
            var low = st.ToLowerInvariant();
            if (section is "[v4+ styles]" or "[v4 styles]")
            {
                if (low.StartsWith("format:")) styleFmt = st[7..].Split(',').Select(f => f.Trim().ToLowerInvariant()).ToList();
                else if (low.StartsWith("style:") && styleFmt.Count > 0)
                {
                    var parts = st[6..].Split(',', styleFmt.Count).Select(p => p.Trim()).ToList();
                    var vals = new Dictionary<string, string>();
                    for (int i = 0; i < Math.Min(parts.Count, styleFmt.Count); i++) vals[styleFmt[i]] = parts[i];
                    bool Flag(string k) => vals.TryGetValue(k, out var v) && v != "0" && v != "";
                    var name = vals.GetValueOrDefault("name", "");
                    doc.Styles[name] = new StyleInfo(name, Flag("bold"), Flag("italic"), Flag("underline"));
                }
                header.Add(line);
                continue;
            }
            if (section == "[events]")
            {
                if (low.StartsWith("format:"))
                {
                    fmt = st[7..].Split(',').Select(f => f.Trim()).ToList();
                    doc.EventsFormat = fmt;
                    header.Add(line);
                    continue;
                }
                bool isD = low.StartsWith("dialogue:"), isC = low.StartsWith("comment:");
                if (isD || isC)
                {
                    var body = st[(st.IndexOf(':') + 1)..].TrimStart();
                    var parts = body.Split(',', fmt.Count);
                    if (parts.Length < fmt.Count) continue;
                    var vals = new Dictionary<string, string>();
                    for (int i = 0; i < fmt.Count; i++) vals[fmt[i].ToLowerInvariant()] = parts[i];
                    try
                    {
                        doc.Dialogues.Add(new Dialogue
                        {
                            Start = ParseTime(vals.GetValueOrDefault("start", "0:00:00.00")),
                            End = ParseTime(vals.GetValueOrDefault("end", "0:00:00.00")),
                            Text = vals.GetValueOrDefault("text", ""),
                            Style = vals.GetValueOrDefault("style", "Default").Trim() is var s && s != "" ? s : "Default",
                            Actor = (vals.GetValueOrDefault("name") ?? vals.GetValueOrDefault("actor") ?? "").Trim(),
                            Effect = vals.GetValueOrDefault("effect", "").Trim(),
                            Layer = (vals.GetValueOrDefault("layer") ?? vals.GetValueOrDefault("marked") ?? "0").Trim(),
                            MarginL = vals.GetValueOrDefault("marginl", "0").Trim(),
                            MarginR = vals.GetValueOrDefault("marginr", "0").Trim(),
                            MarginV = vals.GetValueOrDefault("marginv", "0").Trim(),
                            Comment = isC,
                        });
                    }
                    catch (FormatException) { }
                }
                continue;
            }
            header.Add(line);
        }
        if (doc.EventsFormat.Count == 0)
        {
            doc.EventsFormat = DefaultFormat.ToList();
            if (!header.Any(l => l.Trim().Equals("[events]", StringComparison.OrdinalIgnoreCase))) header.AddRange(["", "[Events]"]);
            header.Add("Format: " + string.Join(", ", DefaultFormat));
        }
        doc.Header = string.Join("\n", header).TrimEnd('\n') + "\n";
        doc.Trailer = string.Join("\n", trailer).Trim('\n');
        return doc;
    }

    static string HtmlToAss(string t)
    {
        t = Regex.Replace(t, @"<\s*i\s*>", "{\\i1}", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<\s*/\s*i\s*>", "{\\i0}", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<\s*b\s*>", "{\\b1}", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<\s*/\s*b\s*>", "{\\b0}", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<\s*u\s*>", "{\\u1}", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<\s*/\s*u\s*>", "{\\u0}", RegexOptions.IgnoreCase);
        t = VttTsRe().Replace(t, "");
        t = HtmlTagRe().Replace(t, "");
        return WebUtility.HtmlDecode(t);
    }

    static SubDoc ParseBlocks(string text, string kind)
    {
        var doc = new SubDoc { Kind = kind, Header = MinimalAssHeader, EventsFormat = DefaultFormat.ToList() };
        var norm = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\ufeff', '\n', ' ');
        foreach (var block in Regex.Split(norm, @"\n\s*\n"))
        {
            var lines = block.Trim('\n').Split('\n');
            if (lines.Length == 0 || (kind == "vtt" && Regex.IsMatch(lines[0], "^(WEBVTT|NOTE|STYLE|REGION)"))) continue;
            for (int i = 0; i < lines.Length; i++)
            {
                var m = SrtTimeLine().Match(lines[i]);
                if (!m.Success) continue;
                int a, b;
                try { a = ParseTime(m.Groups[1].Value); b = ParseTime(m.Groups[2].Value); }
                catch (FormatException) { break; }
                var body = lines.Skip(i + 1).ToList();
                var actor = "";
                if (kind == "vtt" && body.Count > 0)
                {
                    var vm = Regex.Match(body[0], @"<v(?:\.[^ >]*)?\s+([^>]+)>");
                    if (vm.Success) actor = vm.Groups[1].Value.Trim();
                }
                doc.Dialogues.Add(new Dialogue { Start = a, End = b, Text = string.Join("\\N", body.Select(HtmlToAss)), Actor = actor });
                break;
            }
        }
        return doc;
    }

    public static SubDoc ParseSrt(string text) => ParseBlocks(text, "srt");
    public static SubDoc ParseVtt(string text) => ParseBlocks(text, "vtt");

    // --- очистка ---
    public static string StripTags(string text)
    {
        var c = TagRe().Replace(text, "");
        c = VttTsRe().Replace(c, "");
        c = HtmlTagRe().Replace(c, "");
        c = WebUtility.HtmlDecode(c);
        return c.Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ").Trim();
    }

    public static bool IsFullCaps(string text)
    {
        var letters = StripTags(text).Where(char.IsLetter).ToList();
        return letters.Count > 1 && letters.All(char.IsUpper);
    }

    /// <summary>Удалить строки (по \N), целиком набранные капсом.</summary>
    public static string StripCaps(string text)
    {
        var parts = NlSplitRe().Split(text);
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Length; i += 2)
        {
            if (IsFullCaps(parts[i])) continue;
            sb.Append(parts[i]);
            if (i + 1 < parts.Length) sb.Append(parts[i + 1]);
        }
        var res = DupNlRe().Replace(sb.ToString(), "$1");
        while (res.StartsWith("\\N") || res.StartsWith("\\n")) res = res[2..];
        while (res.EndsWith("\\N") || res.EndsWith("\\n")) res = res[..^2];
        return res;
    }

    // --- запись ---
    static string AssLine(Dialogue d, List<string> fmt)
    {
        string V(string f) => f.ToLowerInvariant() switch
        {
            "layer" or "marked" => d.Layer, "start" => FmtAss(d.Start), "end" => FmtAss(d.End), "style" => d.Style,
            "name" or "actor" => d.Actor, "marginl" => d.MarginL, "marginr" => d.MarginR, "marginv" => d.MarginV,
            "effect" => d.Effect, "text" => d.Text.Replace("\n", "\\N"), _ => "",
        };
        return (d.Comment ? "Comment: " : "Dialogue: ") + string.Join(",", fmt.Select(V));
    }

    public static string ToAss(SubDoc doc, List<Dialogue> dialogues = null)
    {
        dialogues ??= doc.Dialogues;
        var header = doc.Kind == "ass" && doc.Header != "" ? doc.Header : MinimalAssHeader;
        var fmt = doc.Kind == "ass" && doc.EventsFormat.Count > 0 ? doc.EventsFormat : DefaultFormat.ToList();
        var outp = header.TrimEnd('\n') + "\n" + string.Join("\n", dialogues.Select(d => AssLine(d, fmt))) + "\n";
        if (doc.Kind == "ass" && doc.Trailer != "") outp += "\n" + doc.Trailer + "\n";
        return outp;
    }

    static string AssToHtml(Dialogue d, Dictionary<string, StyleInfo> styles, bool keep)
    {
        var t = d.Text;
        if (keep)
        {
            t = TagRe().Replace(t, m =>
            {
                var o = "";
                foreach (var tag in new[] { "i", "b", "u" })
                {
                    var tm = Regex.Match(m.Value, $@"\\{tag}(\d+)");
                    if (tm.Success) o += tm.Groups[1].Value != "0" ? $"<{tag}>" : $"</{tag}>";
                }
                return o;
            });
            if (styles.TryGetValue(d.Style, out var st))
            {
                if (st.Italic) t = $"<i>{t}</i>";
                if (st.Bold) t = $"<b>{t}</b>";
            }
        }
        else t = TagRe().Replace(t, "");
        t = t.Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ");
        return string.Join("\n", t.Split('\n').Select(l => l.Trim()).Where(l => l != ""));
    }

    public static string ToSrt(SubDoc doc, List<Dialogue> dialogues = null, bool keepStyles = false)
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var d in (dialogues ?? doc.Dialogues).Where(d => !d.Comment).OrderBy(d => d.Start).ThenBy(d => d.End))
        {
            var body = AssToHtml(d, doc.Styles, keepStyles);
            if (body.Trim() == "") continue;
            if (n > 0) sb.Append('\n');
            sb.Append(++n).Append('\n').Append(FmtSrt(d.Start)).Append(" --> ").Append(FmtSrt(d.End)).Append('\n').Append(body).Append('\n');
        }
        return sb.ToString();
    }

    public static string ToVtt(SubDoc doc, List<Dialogue> dialogues = null, bool keepStyles = false)
    {
        var sb = new StringBuilder("WEBVTT\n");
        foreach (var d in (dialogues ?? doc.Dialogues).Where(d => !d.Comment).OrderBy(d => d.Start).ThenBy(d => d.End))
        {
            var body = AssToHtml(d, doc.Styles, keepStyles);
            if (body.Trim() == "") continue;
            sb.Append('\n').Append(FmtSrt(d.Start, '.')).Append(" --> ").Append(FmtSrt(d.End, '.')).Append('\n')
              .Append(body.Replace("-->", "->")).Append('\n');
        }
        return sb.ToString();
    }

    public static string Render(SubDoc doc, string target, List<Dialogue> dialogues = null, bool keepStyles = false) =>
        target.ToLowerInvariant().TrimStart('.') switch
        {
            "srt" => ToSrt(doc, dialogues, keepStyles),
            "vtt" or "webvtt" => ToVtt(doc, dialogues, keepStyles),
            _ => ToAss(doc, dialogues),
        };

    /// <summary>Сдвиг таймингов прямо в тексте с сохранением оформления файла.</summary>
    public static (string Text, int Count) ShiftText(string text, string kind, int deltaMs)
    {
        int count = 0;
        if (kind is "srt" or "vtt")
        {
            char sep = kind == "srt" ? ',' : '.';
            var res = SrtTimeLine().Replace(text, m =>
            {
                count++;
                return $"{FmtSrt(Math.Max(0, ParseTime(m.Groups[1].Value) + deltaMs), sep)} --> {FmtSrt(Math.Max(0, ParseTime(m.Groups[2].Value) + deltaMs), sep)}";
            });
            return (res, count);
        }
        var fmt = DefaultFormat.Select(f => f.ToLowerInvariant()).ToList();
        bool inEvents = false;
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            var st = line.Trim();
            var low = st.ToLowerInvariant();
            if (st.StartsWith('[') && st.EndsWith(']')) inEvents = low == "[events]";
            else if (inEvents && low.StartsWith("format:")) fmt = st[7..].Split(',').Select(f => f.Trim().ToLowerInvariant()).ToList();
            else if (inEvents && (low.StartsWith("dialogue:") || low.StartsWith("comment:")))
            {
                var colon = line.IndexOf(':');
                var prefix = line[..colon];
                var body = line[(colon + 1)..];
                var lead = body[..(body.Length - body.TrimStart().Length)];
                var parts = body.TrimStart().Split(',', fmt.Count);
                try
                {
                    foreach (var key in new[] { "start", "end" })
                    {
                        var i = fmt.IndexOf(key);
                        if (i >= 0) parts[i] = FmtAss(Math.Max(0, ParseTime(parts[i]) + deltaMs));
                    }
                    lines[li] = prefix + ":" + lead + string.Join(",", parts);
                    count++;
                }
                catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException) { }
            }
        }
        return (string.Join(nl, lines), count);
    }

    public static void WriteText(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));
}
