// Юнит-тесты ядра без внешних пакетов: dotnet run --project tests/HKTools.SelfTest
using HKTools;
using HKTools.Core;
using HKTools.Encoders;
using HKTools.Media;
using HKTools.Scripts;

Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(Path.GetTempPath(), "hk-selftest-config"));
int failed = 0, passed = 0;

void Test(string name, Action body)
{
    try { body(); passed++; Console.WriteLine($"  ✓ {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"  ✗ {name}: {ex.Message}"); }
}

void Eq<T>(T expected, T actual, string what = "")
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what} ожидалось «{expected}», получено «{actual}»");
}

void True(bool cond, string what) { if (!cond) throw new Exception(what); }

const string Ass = """
[Script Info]
ScriptType: v4.00+

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,60,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,3,1,2,60,60,50,1
Style: Ital,Arial,60,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,1,0,0,100,100,0,0,1,3,1,2,60,60,50,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:01.00,0:00:02.50,Default,Alice,0,0,0,,Привет, {\i1}мир{\i0}, а, б
Dialogue: 0,0:00:03.00,0:00:04.00,Ital,Signs,0,0,0,,ВЫВЕСКА
Dialogue: 0,0:00:05.00,0:00:06.00,Default,Bob,0,0,0,,Строка\NКАПС ТУТ

[Fonts]
fontname: x.ttf

""";

Console.WriteLine($"hktools {AppInfo.Version} — самопроверка ядра");

Test("время и сдвиг", () =>
{
    Eq(62340, Subs.ParseTime("0:01:02.34"));
    Eq(62345, Subs.ParseTime("00:01:02,345"));
    Eq(62500, Subs.ParseTime("01:02.5"));
    Eq(1500, Subs.ParseShift("1500"));
    Eq(-1500, Subs.ParseShift("-0:00:01.50"));
    Eq(2500, Subs.ParseShift("2,5"));
    Eq("0:01:02.35", Subs.FmtAss(62345));
    Eq("00:01:02,345", Subs.FmtSrt(62345));
});

Test("ASS: разбор и запись", () =>
{
    var doc = Subs.ParseAss(Ass);
    Eq(3, doc.Dialogues.Count);
    Eq("Alice", doc.Dialogues[0].Actor);
    True(doc.Dialogues[0].Text.EndsWith("а, б"), "запятые в тексте");
    True(doc.Styles["Ital"].Italic, "курсивный стиль");
    var outp = Subs.ToAss(doc);
    True(outp.Contains("[Fonts]") && outp.Split("Dialogue:").Length - 1 == 3, "roundtrip");
});

Test("SRT/VTT", () =>
{
    var doc = Subs.ParseAss(Ass);
    var srt = Subs.ToSrt(doc, keepStyles: true);
    True(srt.Contains("Привет, <i>мир</i>, а, б"), "теги курсива");
    True(srt.Contains("<i>ВЫВЕСКА</i>"), "курсив из стиля");
    var vtt = Subs.ToVtt(doc);
    True(vtt.StartsWith("WEBVTT") && vtt.Contains("00:00:01.000 --> 00:00:02.500"), "vtt");
});

Test("разбор SRT", () =>
{
    var doc = Subs.ParseSrt("1\n00:00:01,000 --> 00:00:02,000\n<i>Hi</i>\nthere\n\n2\n00:00:03,000 --> 00:00:04,000\nX\n");
    Eq(2, doc.Dialogues.Count);
    Eq(@"{\i1}Hi{\i0}\Nthere", doc.Dialogues[0].Text);
});

Test("сдвиг сохраняет оформление", () =>
{
    var (text, n) = Subs.ShiftText(Ass, "ass", 1000);
    Eq(3, n);
    True(text.Contains("Dialogue: 0,0:00:02.00,0:00:03.50,Default,Alice"), "сдвиг ASS");
    True(text.Contains("[Fonts]"), "секция шрифтов");
    var (o, _) = Subs.ShiftText("1\n00:00:01,000 --> 00:00:02,000\n<font color=red>Hi</font>\n", "srt", -1500);
    True(o.Contains("00:00:00,000 --> 00:00:00,500") && o.Contains("<font color=red>"), "сдвиг SRT");
});

Test("удаление КАПСА", () =>
{
    Eq("Строка", Subs.StripCaps(@"Строка\NКАПС ТУТ"));
    Eq("", Subs.StripCaps("ВСЁ КАПСОМ"));
    Eq("Normal OK", Subs.StripCaps("Normal OK"));
});

Test("фильтры субтитров", () =>
{
    var doc = Subs.ParseAss(Ass);
    var vals = new Dictionary<string, object>
    {
        ["exclude_actors"] = KwItem.ToStore([new KwItem { Word = "Signs" }]), ["strip_caps"] = true, ["strip_formatting"] = true,
        ["text_patterns"] = KwItem.ToStore([new KwItem { Word = ", а, б", OnlyPart = true }]),
    };
    var kept = SubFilters.Apply(doc, vals).Where(s => !s.Deleted).Select(s => s.Text).ToList();
    Eq("Привет, мир|Строка", string.Join("|", kept));
    True(!SubFilters.Apply(doc, vals, new HashSet<int> { 1 }).First(s => s.Index == 1).Deleted, "ручное включение");
});

Test("переименование", () =>
{
    var cfg = new Dictionary<string, object> { ["local_rename_override"] = true, ["local_rename_search"] = @" - (\d+)", ["local_rename_replace"] = " [${num:2}-$1]" };
    Eq("Show [03-07]", Rename.Apply("Show - 07", 3, cfg));
    Eq("Show", Rename.Apply("Show", 1, new Dictionary<string, object>()));
});

Test("масштаб", () =>
{
    Eq((864, 360, (string)null), VideoEncode.ComputeScale(1920, 800, "0.5"));
    Eq((852, 480, (string)null), VideoEncode.ComputeScale(1920, 1080, "0.5"));
    True(VideoEncode.ComputeScale(1920, 1080, "1").Error != null, "совпадающее разрешение");
});

Test("сборка MKV: сопоставление", () =>
{
    True(MkvAssembly.IsCompanion("Ep01", "Ep01.dub"), "Ep01.dub");
    True(MkvAssembly.IsCompanion("Ep01", "ep01 [signs]"), "ep01 [signs]");
    True(!MkvAssembly.IsCompanion("Ep01", "Ep012"), "Ep012");
    Eq("signs", MkvAssembly.SubsRole("Ep01", new QueueItem("/x/Ep01.signs.ass")));
    Eq("full", MkvAssembly.SubsRole("Ep01", new QueueItem("/x/Ep01.ass")));
    Eq("other", MkvAssembly.SubsRole("Ep01", new QueueItem("/x/Ep01.en.ass")));
    Eq("AniLibria", MkvAssembly.InferTitle("Ep01", "Ep01.AniLibria"));
    Eq("", MkvAssembly.InferTitle("Ep01", "Ep01.rus"));
});

Test("поля настроек", () =>
{
    Eq(10L, new Field("x", "X", FieldType.Int, 5L).Range(0, 10).Coerce("12"));
    Eq(true, new Field("c", "C", FieldType.Check, false).Coerce("yes"));
    Eq("Bb", new Field("o", "O", FieldType.Combo, "A").Opts("A", "Bb").Coerce("bb"));
    var kw = KwItem.Parse(new Field("k", "K", FieldType.Keywords, new List<object>()).Coerce("rus|eng"));
    Eq("rus,eng", string.Join(",", kw.Select(k => k.Word)));
    True(new Cond("mode", new[] { "a", "b" }).Test(new Dictionary<string, object> { ["mode"] = "b" }), "Cond");
    True(new Cond("flag", true, negate: true).Test(new Dictionary<string, object> { ["flag"] = false }), "Cond negate");
});

Test("аргументы энкодеров", () =>
{
    var cpu = new CpuEncoder();
    var ctx = new EncContext(false, true, ".mp4");
    var v = new Dictionary<string, object>
    {
        ["cpu_codec"] = "HEVC / H.265", ["x265_preset"] = "slow", ["x265_rc"] = "CRF", ["x265_crf"] = 20L,
        ["x265_advanced_options"] = true, ["x265_aq_mode"] = "3", ["x265_no_sao"] = true,
    };
    var args = cpu.Args(v, ctx);
    Eq("-c:v libx265 -pix_fmt yuv420p10le", string.Join(" ", args.Take(4)));
    True(args.Contains("-x265-params") && args.Contains("aq-mode=3:no-sao=1"), "x265-params");
    Eq("hvc1", cpu.Tag(v, ctx));
    var a = new NvencEncoder().Args(new Dictionary<string, object>
    {
        ["nvenc_codec"] = "AV1", ["nvenc_rc"] = "vbr", ["v_bitrate"] = 3000L, ["auto_bitrate"] = true, ["nv_b_ref_mode"] = "each",
    }, new EncContext(false, false, ".mkv"));
    Eq("6000k", a[a.IndexOf("-maxrate") + 1]);
    Eq("middle", a[a.IndexOf("-b_ref_mode") + 1]);
    Eq("format=p010,hwupload", string.Join(",", new VaapiEncoder().FilterSuffix(new Dictionary<string, object> { ["vaapi_codec"] = "HEVC / H.265" }, new EncContext(false, true, ".mkv"))));
});

Test("раскладки каналов", () =>
{
    Eq("FL FR FC LFE SL SR", string.Join(" ", AudioChannels.LayoutChannels("5.1(side)", 6)));
    Eq("FL FR", string.Join(" ", AudioChannels.LayoutChannels("", 2)));
});

Test("имена при разборке", () =>
{
    var t = new Track { Index = 3, Type = "subtitle", Codec = "ass", Language = "rus", Title = "Надписи" };
    Eq("Ep 01_3_rus", TrackExtractor.FormatName("{original}_{id}_{lang}", "Ep 01", t, ""));
    var t2 = new Track { Index = 1, Type = "audio", Codec = "ac3", Language = "und" };
    Eq("Ep_1", TrackExtractor.FormatName("{original}_{lang}_{id}", "Ep", t2, ""));
});

Test("реестр инструментов", () =>
{
    Eq(19, Registry.All.Count);
    Eq(Registry.All.Count, Registry.All.Select(s => s.Id).Distinct().Count());
    foreach (var s in Registry.All) True(s.Fields(s.GetValues()) != null, s.Id);
});

Console.WriteLine($"\nПройдено: {passed}, ошибок: {failed}");
return failed == 0 ? 0 : 1;
