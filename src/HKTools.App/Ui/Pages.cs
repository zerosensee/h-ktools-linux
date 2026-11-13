using System.Runtime.InteropServices;
using HKTools.Core;
using HKTools.Media;
using HKTools.Scripts;
using HKTools.Ui.Native;

namespace HKTools.Ui;

internal static class Pages
{
    public static W ToolbarPage(W header, out W toolbar)
    {
        var root = G.VBox();
        toolbar = new W("AdwToolbarView", ("vexpand", true));
        toolbar.Top(header);
        root.Add(toolbar);
        return root;
    }

    [DllImport(Lib.Adw)] static extern void adw_about_dialog_add_credit_section(IntPtr d, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr people);
    [DllImport(Lib.Adw)] static extern void adw_about_dialog_add_legal_section(IntPtr d, [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string copyright, int license, IntPtr text);

    public static void About(W parent)
    {
        var d = new W("AdwAboutDialog", ("application-name", AppInfo.Name), ("version", AppInfo.Version), ("application-icon", AppInfo.Id),
            ("developer-name", "zerosensee"),
            ("comments", "Пакетная обработка, транскодирование и ремуксинг медиа для Linux (C# / .NET, GTK4 + libadwaita, ffmpeg, mkvtoolnix, yt-dlp, whisper.cpp).\n\n" +
                         $"Независимый форк {AppInfo.UpstreamName} (автор krnzhnr) — не официальная версия."),
            ("website", AppInfo.RepoUrl), ("issue-url", AppInfo.RepoUrl + "/issues"), ("license-type", 7)); // GTK_LICENSE_MIT_X11
        var people = Lib.Strv([$"{AppInfo.UpstreamName} (krnzhnr) {AppInfo.UpstreamUrl}"]);
        adw_about_dialog_add_credit_section(d.H, "Оригинальный проект", people);
        Lib.FreeStrv(people, 1);
        adw_about_dialog_add_legal_section(d.H, AppInfo.UpstreamName, "Copyright © 2026 krnzhnr", 7, IntPtr.Zero);
        G.adw_dialog_present(d.H, parent.H);
    }

    public static string HumanSize(long n)
    {
        double size = n;
        foreach (var unit in new[] { "Б", "КБ", "МБ", "ГБ", "ТБ" })
        {
            if (size < 1024 || unit == "ТБ") return unit == "Б" ? $"{size:0} {unit}" : $"{size.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} {unit}";
            size /= 1024;
        }
        return $"{n} Б";
    }

    public static string HumanDuration(double sec)
    {
        int s = (int)Math.Max(0, sec);
        int h = s / 3600, m = s % 3600 / 60;
        s %= 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }
}

// --- Главная ------------------------------------------------------------------
internal sealed class HomePage
{
    public readonly W Root;
    readonly MainWindow _win;
    readonly W _search, _banner, _sub;
    readonly List<(W Card, Script S)> _cards = new();
    readonly List<(W Title, W Flow, List<Script> Items)> _sections = new();

    public HomePage(MainWindow win)
    {
        _win = win;
        var header = G.Header(AppInfo.Name, "Пакетная обработка медиа");
        _search = new W("GtkSearchEntry", ("placeholder-text", "Поиск инструмента…"), ("width-chars", 24));
        _search.On("search-changed", Filter);
        header.End(_search);
        Root = Pages.ToolbarPage(header, out var tv);
        _banner = new W("AdwBanner", ("button-label", "Открыть"));
        _banner.On("button-clicked", () => win.ShowPage("deps"));
        tv.Top(_banner);

        var content = G.VBox(6, ("margin-top", 24), ("margin-bottom", 24), ("margin-start", 18), ("margin-end", 18));
        var hero = G.Box(spacing: 18, props: [("halign", Align.Start)]);
        if (win.IconPath != null) hero.Add(G.ImageFile(win.IconPath, 84));
        var txt = G.VBox(4, ("valign", Align.Center));
        txt.Add(G.Label(AppInfo.Name, ("xalign", 0f)).Css("kt-hero-title"));
        _sub = G.Label("", ("xalign", 0f), ("wrap", true)).Css("dim-label");
        txt.Add(_sub);
        hero.Add(txt);
        content.Add(hero);
        foreach (var cat in Categories.All)
        {
            var items = Registry.All.Where(s => s.Category == cat).ToList();
            if (items.Count == 0) continue;
            var title = G.Label(cat, ("xalign", 0f)).Css("kt-section-title");
            var flow = new W("GtkFlowBox", ("selection-mode", 0), ("homogeneous", true), ("max-children-per-line", 4u),
                ("min-children-per-line", 1u), ("column-spacing", 12u), ("row-spacing", 12u));
            foreach (var s in items)
            {
                var card = Card(s);
                flow.Add(card);
                _cards.Add((card, s));
            }
            content.Add(title, flow);
            _sections.Add((title, flow, items));
        }
        tv.Set("content", G.Scrolled(new W("AdwClamp", ("maximum-size", 1240), ("child", content))));
        RefreshStatus();
    }

    W Card(Script s)
    {
        var btn = G.Button(css: ["card", "kt-card"]);
        var box = G.Box(spacing: 12);
        var icon = G.Image(s.Icon, 24);
        icon.Set("valign", Align.Start);
        icon.Css("kt-card-icon", Categories.Css.GetValueOrDefault(s.Category, ""));
        box.Add(icon);
        var col = G.VBox(4, ("hexpand", true));
        col.Add(G.Label(s.Name, ("xalign", 0f), ("wrap", true)).Css("kt-card-title"));
        col.Add(G.Label(s.Description, ("xalign", 0f), ("wrap", true), ("lines", 3), ("ellipsize", Ellipsize.End), ("max-width-chars", 34)).Css("kt-card-desc"));
        box.Add(col);
        btn.Set("child", box);
        btn.On("clicked", () => _win.ShowPage(s.Id));
        var missing = s.MissingDeps();
        if (missing.Count > 0)
        {
            btn.Css("kt-unavailable");
            btn.Tooltip = "Не установлено: " + string.Join(", ", missing.Where(Tools.All.ContainsKey).Select(m => Tools.All[m].Title));
        }
        return btn;
    }

    public void RefreshStatus()
    {
        var missingRequired = Tools.All.Values.Where(t => t.Required && Tools.Which(t.Key) == null).ToList();
        if (missingRequired.Count > 0)
        {
            _banner.Set("title", "Не найден FFmpeg — большинство инструментов недоступно. Установите: sudo pacman -S ffmpeg");
            _banner.Set("revealed", true);
        }
        else
        {
            var opt = new[] { "mkvmerge", "yt-dlp", "whisper", "fdkaac" }.Where(k => Tools.Which(k) == null).Select(k => Tools.All[k].Title).ToList();
            _banner.Set("title", opt.Count > 0 ? "Часть инструментов недоступна: нет " + string.Join(", ", opt) : "");
            _banner.Set("revealed", opt.Count > 0);
        }
        int n = Registry.All.Count;
        _sub.Set("label", $"{n} инструментов · определение оборудования…");
        MainLoop.RunBg(() => Hw.Caps(), (caps, err) =>
        {
            if (err != null || caps == null) { _sub.Set("label", $"{n} инструментов · форк {AppInfo.UpstreamName}"); return; }
            var accel = new List<string>();
            if (caps.NvencOk) accel.Add("NVENC");
            if (caps.VaapiOk) accel.Add("VAAPI");
            _sub.Set("label", $"{n} инструментов · FFmpeg {(caps.FfmpegVersion != "" ? caps.FfmpegVersion : "—")} · аппаратное кодирование: " +
                              $"{(accel.Count > 0 ? string.Join(", ", accel) : "нет (CPU)")}\nНезависимый Linux-форк {AppInfo.UpstreamName} (krnzhnr) · C# / .NET {Environment.Version.Major}");
        });
    }

    static string Hay(Script s) => $"{s.Name} {s.Description} {s.Category} {s.Id}".ToLowerInvariant();

    void Filter()
    {
        var q = _search.GetStr("text").Trim().ToLowerInvariant();
        foreach (var (card, s) in _cards) card.Parent.Visible = q == "" || Hay(s).Contains(q);
        foreach (var (title, flow, items) in _sections)
        {
            bool vis = items.Any(s => q == "" || Hay(s).Contains(q));
            title.Visible = vis;
            flow.Visible = vis;
        }
    }
}

// --- Калькулятор таймингов ----------------------------------------------------
/// <summary>Разница таймкодов «до» и «после» (как в Aegisub) с историей.</summary>
internal sealed class TimingPage
{
    public readonly W Root;
    readonly W _before, _after, _result, _resultMs, _hist;
    readonly List<W> _histRows = new();
    int? _diff = 0;

    public TimingPage()
    {
        Root = Pages.ToolbarPage(G.Header("Калькулятор таймингов", "Инструменты"), out var tv);
        var page = new W("AdwPreferencesPage");
        var group = new W("AdwPreferencesGroup", ("title", "Таймкоды"),
            ("description", "Формат Ч:ММ:СС.ммм, ММ:СС.ммм или миллисекунды. Результат — сдвиг, который нужно применить."));
        _before = new W("AdwEntryRow", ("title", "До (исходное время)"), ("text", "0:00:00.000"));
        _after = new W("AdwEntryRow", ("title", "После (нужное время)"), ("text", "0:00:00.000"));
        foreach (var row in new[] { _before, _after })
        {
            row.Css("kt-time-entry");
            row.On("changed", Calc);
            var r = row;
            row.End(G.IconButton("edit-paste-symbolic", "Вставить из буфера", () => r.Paste(t =>
            {
                if (!string.IsNullOrWhiteSpace(t)) r.Set("text", t.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries)[0]);
            })));
            group.Add(row);
        }
        var swap = new W("AdwActionRow", ("title", "Поменять местами"));
        var swapBtn = G.IconButton("object-flip-vertical-symbolic", "Поменять", () =>
        {
            var a = _before.GetStr("text");
            _before.Set("text", _after.GetStr("text"));
            _after.Set("text", a);
        });
        swap.End(swapBtn);
        swap.Set("activatable-widget", swapBtn);
        group.Add(swap);
        page.Add(group);

        var res = new W("AdwPreferencesGroup", ("title", "Результат"));
        var box = G.VBox(6, ("margin-top", 12), ("margin-bottom", 12));
        _result = G.Label("+0:00:00.000").Css("kt-big-time");
        _resultMs = G.Label("0 мс").Css("dim-label");
        box.Add(_result, _resultMs);
        var btns = G.Box(spacing: 8, props: [("halign", Align.Center), ("margin-top", 6)]);
        btns.Add(G.Button("Копировать время", onClick: () => Root.Copy(_result.GetStr("label")), css: "pill"),
            G.Button("Копировать мс", onClick: () => Root.Copy((_diff ?? 0).ToString()), css: "pill"),
            G.Button("В историю", onClick: Remember, css: "pill"),
            G.Button("Сброс", onClick: () => { _before.Set("text", "0:00:00.000"); _after.Set("text", "0:00:00.000"); }, css: "pill"));
        box.Add(btns);
        res.Add(new W("GtkFrame", ("child", box)).Css("card"));
        page.Add(res);
        _hist = new W("AdwPreferencesGroup", ("title", "История"));
        var clear = G.Button("Очистить", css: "flat");
        clear.Set("valign", Align.Center);
        clear.On("clicked", () => { Settings.Instance.Set("timing_history", new List<object>()); RenderHistory(); });
        _hist.Set("header-suffix", clear);
        page.Add(_hist);
        tv.Set("content", page);
        RenderHistory();
    }

    static int? Parse(W row)
    {
        var t = row.GetStr("text").Trim().Replace(" ", "");
        if (t == "") return 0;
        try { return Subs.ParseTime(t); } catch { return null; }
    }

    void Calc()
    {
        int? a = Parse(_before), b = Parse(_after);
        _before.ToggleCss("error", a == null);
        _after.ToggleCss("error", b == null);
        if (a == null || b == null)
        {
            _diff = null;
            _result.Set("label", "—");
            _resultMs.Set("label", "неверный формат времени");
            return;
        }
        int d = b.Value - a.Value;
        _diff = d;
        _result.Set("label", (d >= 0 ? "+" : "") + Subs.FmtHuman(d));
        _resultMs.Set("label", $"{d:+0;-0;+0} мс · {(d >= 0 ? "вперёд (задержка)" : "назад")}");
    }

    List<Dictionary<string, object>> History() =>
        (Settings.Instance.Get("timing_history") as System.Collections.IEnumerable)?.Cast<object>().OfType<Dictionary<string, object>>().ToList() ?? new();

    void Remember()
    {
        if (_diff == null) return;
        var hist = History();
        hist.Insert(0, new() { ["before"] = _before.GetStr("text"), ["after"] = _after.GetStr("text"), ["diff"] = (long)_diff.Value });
        Settings.Instance.Set("timing_history", hist.Take(30).Cast<object>().ToList());
        RenderHistory();
    }

    void RenderHistory()
    {
        foreach (var r in _histRows) _hist.Remove(r);
        _histRows.Clear();
        var hist = History();
        if (hist.Count == 0)
        {
            var row = new W("AdwActionRow", ("title", "Пока пусто"), ("subtitle", "Нажмите «В историю», чтобы сохранить расчёт"));
            _hist.Add(row);
            _histRows.Add(row);
            return;
        }
        foreach (var h in hist)
        {
            int d = (int)Json.ToLong(h.GetValueOrDefault("diff"));
            var before = h.GetValueOrDefault("before")?.ToString() ?? "";
            var after = h.GetValueOrDefault("after")?.ToString() ?? "";
            var row = new W("AdwActionRow", ("title", (d >= 0 ? "+" : "") + Subs.FmtHuman(d) + $"  ({d:+0;-0;+0} мс)"),
                ("subtitle", $"{before} → {after}"), ("use-markup", false), ("activatable", true));
            row.On("activated", () => { _before.Set("text", before); _after.Set("text", after); });
            row.End(G.IconButton("edit-copy-symbolic", "Копировать мс", () => Root.Copy(d.ToString())));
            _hist.Add(row);
            _histRows.Add(row);
        }
    }
}

// --- Журнал --------------------------------------------------------------------
internal sealed class LogPage
{
    public readonly W Root;
    readonly W _level, _view, _scroller;
    readonly G.TextBuf _buf;
    static readonly Dictionary<string, string> Colors = new()
    {
        ["DEBUG"] = "#8a8a8a", ["INFO"] = null, ["SUCCESS"] = "#28CAC6", ["WARNING"] = "#F5A623", ["ERROR"] = "#EB6E4D",
    };
    static readonly Dictionary<string, int> Order = new() { ["DEBUG"] = 0, ["INFO"] = 1, ["SUCCESS"] = 2, ["WARNING"] = 3, ["ERROR"] = 4 };

    public LogPage()
    {
        var header = G.Header("Журнал", "События и ошибки");
        _level = new W("GtkDropDown", ("model", G.StringList(["Все", "Инфо и выше", "Предупреждения", "Ошибки"])), ("selected", 1u), ("tooltip-text", "Уровень"));
        _level.OnNotify("selected", Reload);
        header.Start(_level);
        header.End(G.IconButton("document-open-symbolic", "Открыть файл журнала", () => Desktop.OpenPath(Path.Combine(Paths.StateDir, "hktools.log")), flat: false));
        header.End(G.IconButton("edit-copy-symbolic", "Копировать всё", () => Root.Copy(_buf.Text), flat: false));
        header.End(G.IconButton("edit-clear-all-symbolic", "Очистить", () => { Log.Clear(); _buf.SetText(""); }, flat: false));
        Root = Pages.ToolbarPage(header, out var tv);
        _view = new W("GtkTextView", ("editable", false), ("monospace", true), ("wrap-mode", 3), ("left-margin", 12), ("right-margin", 12),
            ("top-margin", 8), ("bottom-margin", 8), ("cursor-visible", false));
        _buf = new G.TextBuf((IntPtr)_view.Get("buffer"));
        foreach (var (lvl, color) in Colors)
            if (color != null) _buf.Tag(lvl, ("foreground", color)); else _buf.Tag(lvl);
        _buf.Tag("time", ("foreground", "#8a8a8a"));
        _buf.Tag("source", ("weight", 700));
        _scroller = G.Scrolled(_view).Css("kt-log");
        tv.Set("content", _scroller);
        Reload();
        Log.AddListener(e => MainLoop.Post(() => OnEntry(e)));
    }

    int MinLevel => new[] { 0, 1, 3, 4 }[Math.Clamp(Convert.ToInt32(_level.Get("selected")), 0, 3)];

    void Append(LogEntry e)
    {
        if (Order.GetValueOrDefault(e.Level, 1) < MinLevel) return;
        _buf.Append(e.TimeStr + " ", "time");
        _buf.Append($"[{e.Source}] ", "source");
        _buf.Append(e.Message + "\n", e.Level);
        if (!string.IsNullOrEmpty(e.Details) && e.Level is "ERROR" or "WARNING" or "DEBUG" && MinLevel == 0) _buf.Append(e.Details + "\n", "time");
    }

    void OnEntry(LogEntry e)
    {
        var adj = G.VAdjustment(_scroller);
        bool atBottom = adj.GetDouble("value") >= adj.GetDouble("upper") - adj.GetDouble("page-size") - 30;
        Append(e);
        if (_buf.Lines > 6000) _buf.DeleteLines(1000);
        if (atBottom) MainLoop.Idle(() => { adj.Set("value", adj.GetDouble("upper")); return false; });
    }

    void Reload()
    {
        _buf.SetText("");
        foreach (var e in Log.All()) Append(e);
    }
}

// --- Зависимости --------------------------------------------------------------
internal sealed class DepsPage
{
    public readonly W Root;
    readonly Action _onChanged;
    readonly W _page, _installAll;
    readonly List<W> _groups = new();
    List<string> _missing = new();

    public DepsPage(Action onChanged)
    {
        _onChanged = onChanged;
        var header = G.Header("Зависимости", "Внешние утилиты и оборудование");
        header.End(G.IconButton("view-refresh-symbolic", "Перепроверить", Refresh, flat: false));
        _installAll = G.Button("Установить недостающее", onClick: () => Install(_missing), css: "suggested-action");
        header.Start(_installAll);
        Root = Pages.ToolbarPage(header, out var tv);
        _page = new W("AdwPreferencesPage");
        tv.Set("content", _page);
        Refresh();
    }

    void ClearGroups()
    {
        foreach (var g in _groups) _page.Remove(g);
        _groups.Clear();
    }

    void AddGroup(W g) { _page.Add(g); _groups.Add(g); }

    public void Refresh()
    {
        ClearGroups();
        AddGroup(new W("AdwPreferencesGroup", ("title", "Проверка…")));
        MainLoop.RunBg(() => (Tools.All.Keys.Select(Tools.ProbeVersion).ToList(), Hw.Caps(refresh: true)), Render);
    }

    void Render((List<ToolStatus> Statuses, HwCaps Caps) res, Exception err)
    {
        ClearGroups();
        if (err != null)
        {
            AddGroup(new W("AdwPreferencesGroup", ("title", "Ошибка проверки"), ("description", err.Message)));
            return;
        }
        var (statuses, caps) = res;
        _missing = statuses.Where(s => !s.Found).Select(s => s.Tool.Key).ToList();
        var req = new W("AdwPreferencesGroup", ("title", "Утилиты"),
            ("description", "Пакеты Arch Linux / CachyOS / Ryoku. AUR-пакеты ставятся через paru или yay, если они установлены."));
        foreach (var st in statuses)
        {
            var t = st.Tool;
            var row = new W("AdwActionRow", ("title", t.Title), ("use-markup", false),
                ("subtitle", (st.Found ? st.Version + " · " : "не найдено · ") + t.Purpose), ("subtitle-lines", 2));
            row.Start(new W("GtkImage", ("icon-name", st.Found ? "emblem-ok-symbolic" : t.Required ? "dialog-error-symbolic" : "dialog-warning-symbolic"))
                .Css(st.Found ? "success" : t.Required ? "error" : "warning"));
            row.End(G.Label(t.Package + (t.Aur ? " (AUR)" : "")).Css("dim-label", "caption"));
            if (!st.Found)
            {
                var b = G.Button("Установить", onClick: () => Install([t.Key]), css: "pill");
                b.Set("valign", Align.Center);
                row.End(b);
                if (t.Aur && t.AurVariants != null) row.Tooltip = "Варианты сборки: " + string.Join(", ", t.AurVariants);
            }
            else row.Tooltip = st.Path;
            req.Add(row);
        }
        AddGroup(req);
        _installAll.Visible = _missing.Count > 0;

        var hw = new W("AdwPreferencesGroup", ("title", "Оборудование"), ("description", "Аппаратное кодирование проверяется тестовым кодированием через ffmpeg"));
        string On(Dictionary<string, bool> d) => string.Join(", ", d.Where(kv => kv.Value).Select(kv => kv.Key));
        var rows = new (string Title, string Sub, bool? Ok)[]
        {
            ("Видеокарты", caps.Gpus.Count > 0 ? string.Join(", ", caps.Gpus) : "render-устройства не найдены", null),
            ("NVENC (NVIDIA)", On(caps.Nvenc) is { Length: > 0 } nv ? nv : "недоступно — нужен драйвер nvidia и ffmpeg с nvenc", caps.NvencOk),
            ($"VAAPI ({(caps.VaapiDevice != "" ? caps.VaapiDevice : "—")})", On(caps.Vaapi) is { Length: > 0 } va ? va : "недоступно — AMD: mesa; Intel: intel-media-driver", caps.VaapiOk),
            ("Энкодеры CPU", string.Join(", ", new[] { "libx265", "libx264", "libsvtav1" }.Where(caps.HasEncoder)) is { Length: > 0 } cpu ? cpu : "—", null),
            ("Фильтр rubberband", caps.Filters.Contains("rubberband") ? "есть" : "нет (будет использован atempo)", null),
        };
        foreach (var (title, sub, ok) in rows)
        {
            var row = new W("AdwActionRow", ("title", title), ("subtitle", sub), ("use-markup", false));
            if (ok is { } o) row.Start(new W("GtkImage", ("icon-name", o ? "emblem-ok-symbolic" : "window-close-symbolic")).Css(o ? "success" : "dim-label"));
            hw.Add(row);
        }
        AddGroup(hw);
        _onChanged();
    }

    void Install(List<string> keys)
    {
        if (keys.Count == 0) return;
        var cmd = Tools.InstallCommand(keys);
        if (!Tools.RunInTerminal(cmd))
        {
            Root.Copy(cmd);
            G.Info(Root, "Терминал не найден", $"Команда скопирована в буфер обмена:\n\n{cmd}");
        }
        else Log.Info($"Запущена установка: {cmd}", "Зависимости");
    }
}
