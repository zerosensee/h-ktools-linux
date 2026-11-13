using System.Runtime.InteropServices;
using HKTools.Core;
using HKTools.Scripts;
using HKTools.Ui.Native;

namespace HKTools.Ui;

/// <summary>Главное окно: боковая навигация по инструментам + страницы.</summary>
internal sealed class MainWindow
{
    public readonly W Win;
    public readonly string IconPath;
    readonly W _split, _nav, _stack, _contentPage;
    readonly Dictionary<string, W> _pages = new();
    readonly Dictionary<string, object> _pageObjects = new();
    readonly Dictionary<string, (W Row, string Category, W Spinner)> _rows = new();
    readonly Dictionary<IntPtr, string> _rowIds = new();
    readonly HashSet<string> _busy = new();
    bool _forceClose;

    [DllImport(Lib.Adw)] static extern IntPtr adw_breakpoint_condition_parse([MarshalAs(UnmanagedType.LPUTF8Str)] string s);
    [DllImport(Lib.Adw)] static extern IntPtr adw_breakpoint_new(IntPtr cond);
    [DllImport(Lib.Adw)] static extern void adw_breakpoint_add_setter(IntPtr bp, IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string prop, IntPtr value);
    [DllImport(Lib.Adw)] static extern void adw_application_window_add_breakpoint(IntPtr win, IntPtr bp);
    [DllImport(Lib.Adw)] static extern void adw_breakpoint_bin_add_breakpoint(IntPtr bin, IntPtr bp);

    /// <summary>Точка перелома: при ширине ≤ condition свойство target.prop = true.</summary>
    public static void Breakpoint(W host, string condition, GObj target, string prop)
    {
        var bp = adw_breakpoint_new(adw_breakpoint_condition_parse(condition));
        var v = GValue.Alloc();
        GValue.Fill(v, GValue.Boolean, true);
        adw_breakpoint_add_setter(bp, target.H, prop, v);
        GValue.g_value_unset(v);
        Marshal.FreeHGlobal(v);
        if (host.Is("AdwApplicationWindow")) adw_application_window_add_breakpoint(host.H, bp);
        else adw_breakpoint_bin_add_breakpoint(host.H, bp);
    }

    public MainWindow(GObj app, string iconPath)
    {
        IconPath = iconPath;
        Win = new W("AdwApplicationWindow", ("application", app), ("title", AppInfo.Name), ("default-width", 1280), ("default-height", 820),
            ("icon-name", AppInfo.Id));
        Win.Size(360, 420);
        _split = new W("AdwNavigationSplitView", ("min-sidebar-width", 230.0), ("max-sidebar-width", 300.0));

        // --- боковая панель ---
        var sideTv = new W("AdwToolbarView");
        var sideHeader = G.Header(AppInfo.Name, "");
        var menu = G.Menu(("Настройки", "app.preferences"), ("Зависимости", "win.page::deps"), ("Журнал", "win.page::log"), ("О программе", "app.about"));
        sideHeader.End(new W("GtkMenuButton", ("icon-name", "open-menu-symbolic"), ("menu-model", menu), ("primary", true), ("tooltip-text", "Меню")));
        sideTv.Top(sideHeader);
        _nav = new W("GtkListBox", ("selection-mode", 1));
        _nav.Css("navigation-sidebar");
        _nav.OnP("row-activated", row => ShowPage(_rowIds.GetValueOrDefault(row, "home")));
        G.HeaderFunc(_nav, NavHeader);
        AddNav("home", "Главная", "go-home-symbolic", "");
        foreach (var cat in Categories.All)
            foreach (var s in Registry.All.Where(s => s.Category == cat))
                AddNav(s.Id, s.Name, s.Icon, cat);
        AddNav("log", "Журнал", "utilities-terminal-symbolic", "Система");
        AddNav("deps", "Зависимости", "package-x-generic-symbolic", "Система");
        sideTv.Set("content", G.Scrolled(_nav, hpolicy: 2));
        _split.Set("sidebar", new W("AdwNavigationPage", ("title", AppInfo.Name), ("child", sideTv), ("tag", "sidebar")));

        // --- содержимое ---
        _stack = new W("GtkStack", ("transition-type", 1), ("transition-duration", 120u));
        _contentPage = new W("AdwNavigationPage", ("title", "Главная"), ("child", _stack), ("tag", "content"));
        _split.Set("content", _contentPage);
        Breakpoint(Win, "max-width: 640sp", _split, "collapsed");
        Win.Set("content", _split);

        G.ActionStr(Win, "page", ShowPage);
        G.Action(Win, "add-files", () => CurrentPanel()?.AddFilesDialog());
        G.Action(Win, "start", () => CurrentPanel()?.ToggleRun());
        G.Action(Win, "open-log", () => ShowPage("log"));
        Win.OnB("close-request", OnCloseRequest);
        ShowPage("home");
    }

    public void Present() => G.gtk_window_present(Win.H);
    public void Close() => G.gtk_window_close(Win.H);
    public bool IsActive => G.gtk_window_is_active(Win.H) != 0;

    void AddNav(string id, string title, string icon, string category)
    {
        var row = new W("GtkListBoxRow");
        var box = G.Box(spacing: 10, props: [("margin-top", 2), ("margin-bottom", 2)]);
        var img = G.Image(icon);
        if (Categories.Css.TryGetValue(category, out var css)) img.Css(css, "kt-nav-icon");
        box.Add(img);
        box.Add(G.Label(title, ("xalign", 0f), ("hexpand", true), ("ellipsize", Ellipsize.End)));
        var spinner = new W("GtkSpinner", ("visible", false));
        box.Add(spinner);
        row.Set("child", box);
        _nav.Add(row);
        _rows[id] = (row, category, spinner);
        _rowIds[row.H] = id;
    }

    void NavHeader(IntPtr row, IntPtr before)
    {
        var cat = _rows.Values.FirstOrDefault(r => r.Row.H == row).Category ?? "";
        var prev = before != IntPtr.Zero ? _rows.Values.FirstOrDefault(r => r.Row.H == before).Category : null;
        if (cat != "" && cat != prev)
        {
            var lbl = G.Label(cat, ("xalign", 0f), ("margin-start", 12), ("margin-top", 12), ("margin-bottom", 2)).Css("heading", "dim-label");
            G.gtk_list_box_row_set_header(row, lbl.H);
        }
        else G.gtk_list_box_row_set_header(row, IntPtr.Zero);
    }

    object CreatePage(string id) => id switch
    {
        "home" => new HomePage(this),
        "log" => new LogPage(),
        "deps" => new DepsPage(DepsChanged),
        "timing-calculator" => new TimingPage(),
        _ => new WorkPanel(this, Registry.Get(id)),
    };

    static W RootOf(object page) => page switch
    {
        HomePage h => h.Root, LogPage l => l.Root, DepsPage d => d.Root, TimingPage t => t.Root, WorkPanel w => w.Root, _ => null,
    };

    public void ShowPage(string id)
    {
        if (!_rows.ContainsKey(id)) id = "home";
        if (!_pages.ContainsKey(id))
        {
            var obj = CreatePage(id);
            _pageObjects[id] = obj;
            _pages[id] = RootOf(obj);
            _stack.AddNamed(_pages[id], id);
        }
        _stack.Set("visible-child-name", id);
        var row = _rows[id].Row;
        if (G.gtk_list_box_get_selected_row(_nav.H) != row.H) G.gtk_list_box_select_row(_nav.H, row.H);
        var title = Registry.Get(id)?.Name ?? id switch { "home" => "Главная", "log" => "Журнал", "deps" => "Зависимости", _ => AppInfo.Name };
        _contentPage.Set("title", title);
        _split.Set("show-content", true);
    }

    public WorkPanel CurrentPanel()
    {
        var name = _stack.GetStr("visible-child-name");
        return _pageObjects.GetValueOrDefault(name) as WorkPanel;
    }

    public void SetPageBusy(string id, bool busy)
    {
        if (!_rows.TryGetValue(id, out var r)) return;
        r.Spinner.Visible = busy;
        r.Spinner.Set("spinning", busy);
        if (busy) _busy.Add(id); else _busy.Remove(id);
    }

    void DepsChanged()
    {
        if (_pageObjects.GetValueOrDefault("home") is HomePage h) h.RefreshStatus();
    }

    /// <summary>Файлы, переданные из файлового менеджера: открыть подходящий инструмент.</summary>
    public void OpenFiles(List<string> paths, bool keepCurrent = false)
    {
        paths = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (paths.Count == 0) return;
        var panel = CurrentPanel();
        if ((panel == null || panel.Script.InputMode != "files") && !(keepCurrent && panel != null))
        {
            var ext = Path.GetExtension(paths[0]).ToLowerInvariant();
            ShowPage(Exts.Subtitles.Contains(ext) ? "subtitles-convert" : Exts.VideoContainers.Contains(ext) ? "video-encode" : "audio-encode");
            panel = CurrentPanel();
        }
        panel?.AddPaths(paths);
    }

    public void ShowPreferences() => Prefs.Show(this, () =>
    {
        foreach (var p in _pageObjects.Values.OfType<WorkPanel>()) p.RefreshOutLabel();
    });

    public void ShowAbout() => Pages.About(Win);

    bool OnCloseRequest()
    {
        if (_busy.Count > 0 && !_forceClose)
        {
            G.Confirm(Win, "Идёт обработка", "Закрыть hktools и прервать текущие задачи?", () =>
            {
                foreach (var p in _pageObjects.Values.OfType<WorkPanel>()) p.Runner?.Cancel();
                _forceClose = true;
                Close();
            }, "Прервать и выйти", destructive: true);
            return true;
        }
        return false;
    }
}
