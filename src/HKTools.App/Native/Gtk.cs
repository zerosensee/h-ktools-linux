using System.Runtime.InteropServices;

namespace HKTools.Ui.Native;

internal static class Align { public const int Fill = 0, Start = 1, End = 2, Center = 3; }
internal static class Orient { public const int H = 0, V = 1; }
internal static class Ellipsize { public const int None = 0, Start = 1, Middle = 2, End = 3; }

/// <summary>Фабрики виджетов и сервисные функции GTK/libadwaita.</summary>
internal static class G
{
    // --- конструкторы-сокращения ---
    public static W Box(int orient = Orient.H, int spacing = 0, params (string, object)[] props) =>
        new W("GtkBox", [("orientation", orient), ("spacing", spacing), .. props]);
    public static W VBox(int spacing = 0, params (string, object)[] props) => Box(Orient.V, spacing, props);

    public static W Label(string text = "", params (string, object)[] props) => new W("GtkLabel", [("label", text ?? ""), .. props]);

    public static W Button(string label = null, string icon = null, string tooltip = null, Action onClick = null, params string[] css)
    {
        var props = new List<(string, object)>();
        if (label != null) props.Add(("label", label));
        if (icon != null) props.Add(("icon-name", icon));
        if (tooltip != null) props.Add(("tooltip-text", tooltip));
        var b = new W("GtkButton", props.ToArray());
        b.Css(css);
        if (onClick != null) b.On("clicked", onClick);
        return b;
    }

    public static W IconButton(string icon, string tooltip, Action onClick = null, bool flat = true)
    {
        var b = Button(icon: icon, tooltip: tooltip, onClick: onClick);
        b.Set("valign", Align.Center);
        if (flat) b.Css("flat");
        return b;
    }

    public static W Scrolled(W child, bool vexpand = true, int hpolicy = 1) =>
        new W("GtkScrolledWindow", ("child", child), ("vexpand", vexpand), ("hscrollbar-policy", hpolicy));

    public static W Header(string title = null, string subtitle = "")
    {
        var h = new W("AdwHeaderBar");
        if (title != null) h.Set("title-widget", new W("AdwWindowTitle", ("title", title), ("subtitle", subtitle ?? "")));
        return h;
    }

    // --- иконки с запасными именами ---
    static readonly Dictionary<string, string[]> IconFallbacks = new()
    {
        ["utilities-system-monitor-symbolic"] = ["org.gnome.SystemMonitor-symbolic", "power-profile-performance-symbolic", "view-paged-symbolic"],
        ["audio-card-symbolic"] = ["audio-x-generic-symbolic"],
        ["preferences-system-time-symbolic"] = ["alarm-symbolic", "document-open-recent-symbolic"],
        ["accessories-calculator-symbolic"] = ["org.gnome.Calculator-symbolic", "input-dialpad-symbolic"],
        ["insert-object-symbolic"] = ["list-add-symbolic"],
    };

    [DllImport(Lib.Gio)] static extern IntPtr g_themed_icon_new_from_names(IntPtr names, int len);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_image_new_from_gicon(IntPtr icon);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_image_new_from_file([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    public static W Image(string icon, int size = 0)
    {
        var names = new List<string> { icon };
        if (IconFallbacks.TryGetValue(icon, out var fb)) names.AddRange(fb);
        names.AddRange(["applications-multimedia-symbolic", "image-missing"]);
        var arr = Lib.Strv(names);
        var gicon = g_themed_icon_new_from_names(arr, names.Count);
        Lib.FreeStrv(arr, names.Count);
        var img = new W(gtk_image_new_from_gicon(gicon));
        GObj.g_object_unref(gicon);
        if (size > 0) img.Set("pixel-size", size);
        return img;
    }

    public static W ImageFile(string path, int size = 0)
    {
        var img = new W(gtk_image_new_from_file(path));
        if (size > 0) img.Set("pixel-size", size);
        return img;
    }

    [DllImport(Lib.Gtk)] static extern IntPtr gtk_string_list_new(IntPtr strings);
    public static GObj StringList(IEnumerable<string> items)
    {
        var l = items.ToList();
        var arr = Lib.Strv(l);
        var sl = new GObj(gtk_string_list_new(arr));
        Lib.FreeStrv(arr, l.Count);
        return sl;
    }

    [DllImport(Lib.Adw)] static extern IntPtr adw_spin_row_new_with_range(double min, double max, double step);
    public static W SpinRow(double min, double max, double step) => new(adw_spin_row_new_with_range(min, max, step));

    // --- стили, тема ---
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_css_provider_new();
    [DllImport(Lib.Gtk)] static extern void gtk_css_provider_load_from_data(IntPtr p, [MarshalAs(UnmanagedType.LPUTF8Str)] string data, nint len);
    [DllImport(Lib.Gtk)] static extern IntPtr gdk_display_get_default();
    [DllImport(Lib.Gtk)] static extern void gtk_style_context_add_provider_for_display(IntPtr d, IntPtr p, uint prio);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_icon_theme_get_for_display(IntPtr d);
    [DllImport(Lib.Gtk)] static extern void gtk_icon_theme_add_search_path(IntPtr t, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Lib.Adw)] static extern IntPtr adw_style_manager_get_default();
    [DllImport(Lib.Gtk)] public static extern uint gtk_get_major_version();
    [DllImport(Lib.Gtk)] public static extern uint gtk_get_minor_version();
    [DllImport(Lib.Adw)] public static extern uint adw_get_major_version();
    [DllImport(Lib.Adw)] public static extern uint adw_get_minor_version();

    public static void LoadCss(string css)
    {
        var p = gtk_css_provider_new();
        gtk_css_provider_load_from_data(p, css, -1);
        gtk_style_context_add_provider_for_display(gdk_display_get_default(), p, 600);
    }

    public static void AddIconPath(string path) => gtk_icon_theme_add_search_path(gtk_icon_theme_get_for_display(gdk_display_get_default()), path);

    /// <summary>0 — как в системе, 1 — светлая, 4 — тёмная.</summary>
    public static void ColorScheme(int scheme) => new GObj(adw_style_manager_get_default()).Set("color-scheme", scheme);
    public static bool Dark => new GObj(adw_style_manager_get_default()).GetBool("dark");

    // --- окна и диалоги ---
    [DllImport(Lib.Gtk)] public static extern void gtk_window_present(IntPtr w);
    [DllImport(Lib.Gtk)] public static extern void gtk_window_close(IntPtr w);
    [DllImport(Lib.Gtk)] public static extern void gtk_window_destroy(IntPtr w);
    [DllImport(Lib.Gtk)] public static extern int gtk_window_is_active(IntPtr w);
    [DllImport(Lib.Adw)] public static extern void adw_dialog_present(IntPtr d, IntPtr parent);
    [DllImport(Lib.Adw)] public static extern int adw_dialog_close(IntPtr d);
    [DllImport(Lib.Adw)] static extern IntPtr adw_alert_dialog_new([MarshalAs(UnmanagedType.LPUTF8Str)] string heading, [MarshalAs(UnmanagedType.LPUTF8Str)] string body);
    [DllImport(Lib.Adw)] static extern void adw_alert_dialog_add_response(IntPtr d, [MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string label);
    [DllImport(Lib.Adw)] static extern void adw_alert_dialog_set_response_appearance(IntPtr d, [MarshalAs(UnmanagedType.LPUTF8Str)] string id, int appearance);
    [DllImport(Lib.Adw)] static extern void adw_alert_dialog_set_default_response(IntPtr d, [MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    [DllImport(Lib.Adw)] static extern void adw_alert_dialog_set_close_response(IntPtr d, [MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    [DllImport(Lib.Adw)] static extern void adw_alert_dialog_set_extra_child(IntPtr d, IntPtr child);

    /// <summary>Диалог-вопрос: responses — (id, подпись, 0/1 suggested/2 destructive).</summary>
    public static W Alert(W parent, string heading, string body, Action<string> onResponse, W extra = null, params (string Id, string Label, int Look)[] responses)
    {
        var d = new W(adw_alert_dialog_new(heading, body));
        foreach (var (id, label, look) in responses)
        {
            adw_alert_dialog_add_response(d.H, id, label);
            if (look != 0) adw_alert_dialog_set_response_appearance(d.H, id, look);
        }
        if (responses.Length > 0)
        {
            adw_alert_dialog_set_default_response(d.H, responses[^1].Id);
            adw_alert_dialog_set_close_response(d.H, responses[0].Id);
        }
        if (extra != null) adw_alert_dialog_set_extra_child(d.H, extra.H);
        d.OnP("response", p => onResponse?.Invoke(Lib.Str(p)));
        adw_dialog_present(d.H, parent?.H ?? IntPtr.Zero);
        return d;
    }

    public static void Confirm(W parent, string heading, string body, Action onYes, string yes = "Продолжить", bool destructive = false, Action onNo = null) =>
        Alert(parent, heading, body, r => { if (r == "ok") onYes?.Invoke(); else onNo?.Invoke(); }, null,
            ("cancel", "Отмена", 0), ("ok", yes, destructive ? 2 : 1));

    public static void Info(W parent, string heading, string body) => Alert(parent, heading, body, null, null, ("ok", "OK", 0));

    [DllImport(Lib.Adw)] static extern IntPtr adw_toast_new([MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport(Lib.Adw)] static extern void adw_toast_overlay_add_toast(IntPtr o, IntPtr t);

    public static void Toast(W overlay, string title, string button = null, Action onButton = null, int timeout = 3)
    {
        var t = new GObj(adw_toast_new(title));
        t.Set("timeout", (uint)timeout);
        if (button != null)
        {
            t.Set("button-label", button);
            if (onButton != null) t.On("button-clicked", onButton);
        }
        adw_toast_overlay_add_toast(overlay.H, t.H);
    }

    // --- выбор файлов (GtkFileDialog, GTK ≥ 4.10) ---
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_file_dialog_new();
    [DllImport(Lib.Gtk)] static extern void gtk_file_dialog_open(IntPtr d, IntPtr parent, IntPtr c, IntPtr cb, IntPtr data);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_file_dialog_open_finish(IntPtr d, IntPtr res, IntPtr err);
    [DllImport(Lib.Gtk)] static extern void gtk_file_dialog_open_multiple(IntPtr d, IntPtr parent, IntPtr c, IntPtr cb, IntPtr data);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_file_dialog_open_multiple_finish(IntPtr d, IntPtr res, IntPtr err);
    [DllImport(Lib.Gtk)] static extern void gtk_file_dialog_select_folder(IntPtr d, IntPtr parent, IntPtr c, IntPtr cb, IntPtr data);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_file_dialog_select_folder_finish(IntPtr d, IntPtr res, IntPtr err);
    [DllImport(Lib.Gtk)] static extern void gtk_file_dialog_select_multiple_folders(IntPtr d, IntPtr parent, IntPtr c, IntPtr cb, IntPtr data);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_file_dialog_select_multiple_folders_finish(IntPtr d, IntPtr res, IntPtr err);
    [DllImport(Lib.Gtk)] static extern void gtk_file_dialog_save(IntPtr d, IntPtr parent, IntPtr c, IntPtr cb, IntPtr data);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_file_dialog_save_finish(IntPtr d, IntPtr res, IntPtr err);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_file_filter_new();
    [DllImport(Lib.Gtk)] static extern void gtk_file_filter_add_pattern(IntPtr f, [MarshalAs(UnmanagedType.LPUTF8Str)] string p);
    [DllImport(Lib.Gio)] static extern IntPtr g_list_store_new(nuint itemType);
    [DllImport(Lib.Gio)] static extern void g_list_store_append(IntPtr s, IntPtr item);
    [DllImport(Lib.Gio)] public static extern uint g_list_model_get_n_items(IntPtr m);
    [DllImport(Lib.Gio)] public static extern IntPtr g_list_model_get_item(IntPtr m, uint i);
    [DllImport(Lib.Gio)] static extern IntPtr g_file_new_for_path([MarshalAs(UnmanagedType.LPUTF8Str)] string p);
    [DllImport(Lib.Gio)] static extern IntPtr g_file_get_path(IntPtr f);
    [DllImport(Lib.GLib)] static extern void g_free(IntPtr p);

    public static string FilePath(IntPtr gfile)
    {
        var p = g_file_get_path(gfile);
        var s = Lib.Str(p);
        if (p != IntPtr.Zero) g_free(p);
        return s;
    }

    static List<string> ModelPaths(IntPtr model)
    {
        var res = new List<string>();
        if (model == IntPtr.Zero) return res;
        for (uint i = 0, n = g_list_model_get_n_items(model); i < n; i++)
        {
            var f = g_list_model_get_item(model, i);
            if (FilePath(f) is { } p) res.Add(p);
            GObj.g_object_unref(f);
        }
        GObj.g_object_unref(model);
        return res;
    }

    static GObj FileDialog(string title, string start, IReadOnlyList<string> patterns = null, string initialName = null)
    {
        var d = new GObj(gtk_file_dialog_new());
        d.Set("title", title);
        d.Set("modal", true);
        if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
        {
            var f = g_file_new_for_path(start);
            d.Set("initial-folder", f);
            GObj.g_object_unref(f);
        }
        if (initialName != null) d.Set("initial-name", initialName);
        if (patterns is { Count: > 0 })
        {
            var store = g_list_store_new(GType.Of("GtkFileFilter"));
            var flt = gtk_file_filter_new();
            new GObj(flt).Set("name", "Поддерживаемые файлы");
            foreach (var p in patterns) { gtk_file_filter_add_pattern(flt, p); gtk_file_filter_add_pattern(flt, p.ToUpperInvariant()); }
            g_list_store_append(store, flt);
            var any = gtk_file_filter_new();
            new GObj(any).Set("name", "Все файлы");
            gtk_file_filter_add_pattern(any, "*");
            g_list_store_append(store, any);
            d.Set("filters", store);
        }
        return d;
    }

    static void Async(Action<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> start, GObj dlg, IntPtr parent, Action<IntPtr> finish)
    {
        AsyncReady cb = null;
        GCHandle gh = default;
        cb = (src, res, _) =>
        {
            gh.Free();
            Signals.Guard(() => finish(res));
            GObj.g_object_unref(dlg.H);
        };
        gh = GCHandle.Alloc(cb);
        start(dlg.H, parent, IntPtr.Zero, Marshal.GetFunctionPointerForDelegate(cb), IntPtr.Zero);
    }

    public static void PickFiles(W parent, string title, Action<List<string>> done, bool multiple = true, string start = null, IReadOnlyList<string> patterns = null)
    {
        var d = FileDialog(title, start, patterns);
        if (multiple)
            Async(gtk_file_dialog_open_multiple, d, parent?.Root ?? IntPtr.Zero, res =>
            {
                var ps = ModelPaths(gtk_file_dialog_open_multiple_finish(d.H, res, IntPtr.Zero));
                if (ps.Count > 0) done(ps);
            });
        else
            Async(gtk_file_dialog_open, d, parent?.Root ?? IntPtr.Zero, res =>
            {
                var f = gtk_file_dialog_open_finish(d.H, res, IntPtr.Zero);
                if (f == IntPtr.Zero) return;
                var p = FilePath(f);
                GObj.g_object_unref(f);
                if (p != null) done([p]);
            });
    }

    public static void PickFolder(W parent, string title, Action<string> done, string start = null, bool multiple = false)
    {
        var d = FileDialog(title, start);
        if (multiple)
            Async(gtk_file_dialog_select_multiple_folders, d, parent?.Root ?? IntPtr.Zero, res =>
            {
                foreach (var p in ModelPaths(gtk_file_dialog_select_multiple_folders_finish(d.H, res, IntPtr.Zero))) done(p);
            });
        else
            Async(gtk_file_dialog_select_folder, d, parent?.Root ?? IntPtr.Zero, res =>
            {
                var f = gtk_file_dialog_select_folder_finish(d.H, res, IntPtr.Zero);
                if (f == IntPtr.Zero) return;
                var p = FilePath(f);
                GObj.g_object_unref(f);
                if (p != null) done(p);
            });
    }

    public static void SaveFile(W parent, string title, string name, Action<string> done)
    {
        var d = FileDialog(title, null, null, name);
        Async(gtk_file_dialog_save, d, parent?.Root ?? IntPtr.Zero, res =>
        {
            var f = gtk_file_dialog_save_finish(d.H, res, IntPtr.Zero);
            if (f == IntPtr.Zero) return;
            var p = FilePath(f);
            GObj.g_object_unref(f);
            if (p != null) done(p);
        });
    }

    // --- перетаскивание файлов ---
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_drop_target_new(nuint type, int actions);
    [DllImport(Lib.Gtk)] static extern void gtk_drop_target_set_gtypes(IntPtr t, nuint[] types, nuint n);
    [DllImport(Lib.Gtk)] static extern IntPtr gdk_file_list_get_files(IntPtr list);
    [DllImport(Lib.GLib)] static extern void g_slist_free(IntPtr l);

    /// <summary>Приём файлов (GdkFileList) и текста (ссылки) перетаскиванием.</summary>
    public static GObj DropTarget(W widget, Action<List<string>> onFiles, Action<string> onText = null)
    {
        var t = new GObj(gtk_drop_target_new(0, 1)); // GDK_ACTION_COPY
        var types = new List<nuint> { GType.Of("GdkFileList") };
        if (onText != null) types.Add(GValue.String);
        gtk_drop_target_set_gtypes(t.H, types.ToArray(), (nuint)types.Count);
        Signals.Connect(t.H, "drop", new SigDrop((_, value, _, _, _) => Signals.Guard(() =>
        {
            widget.Uncss("kt-drop-highlight");
            var vtype = (nuint)Marshal.ReadIntPtr(value); // GValue.g_type
            if (vtype == GValue.String)
            {
                var s = Lib.Str(GValue.g_value_get_string(value));
                if (!string.IsNullOrWhiteSpace(s)) onText?.Invoke(s);
                return true;
            }
            var list = gdk_file_list_get_files(GValue.g_value_get_boxed(value));
            var paths = new List<string>();
            for (var node = list; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
                if (FilePath(Marshal.ReadIntPtr(node)) is { } p) paths.Add(p);
            g_slist_free(list);
            if (paths.Count > 0) onFiles(paths);
            return true;
        }, false) ? 1 : 0));
        Signals.Connect(t.H, "enter", new SigXYB((_, _, _, _) => { widget.Css("kt-drop-highlight"); return 1; }));
        Signals.Connect(t.H, "leave", new Sig0((_, _) => widget.Uncss("kt-drop-highlight")));
        widget.AddController(t);
        return t;
    }

    // --- контроллеры ввода ---
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_event_controller_motion_new();
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_event_controller_scroll_new(int flags);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_gesture_click_new();
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_gesture_drag_new();
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_event_controller_key_new();
    [DllImport(Lib.Gtk)] static extern void gtk_gesture_single_set_button(IntPtr g, uint button);
    [DllImport(Lib.Gtk)] public static extern uint gtk_gesture_single_get_current_button(IntPtr g);
    [DllImport(Lib.Gtk)] static extern uint gtk_event_controller_get_current_event_state(IntPtr c);

    public static void OnMotion(W w, Action<double, double> move, Action leave = null)
    {
        var c = new GObj(gtk_event_controller_motion_new());
        Signals.Connect(c.H, "motion", new SigXY((_, x, y, _) => Signals.Guard(() => move(x, y))));
        if (leave != null) Signals.Connect(c.H, "leave", new Sig0((_, _) => Signals.Guard(leave)));
        w.AddController(c);
    }

    /// <summary>Прокрутка колёсиком; state — модификаторы (Ctrl = 4, Shift = 1).</summary>
    public static void OnScroll(W w, Func<double, double, uint, bool> scroll)
    {
        var c = new GObj(gtk_event_controller_scroll_new(3)); // VERTICAL | HORIZONTAL
        Signals.Connect(c.H, "scroll", new SigXYB((self, dx, dy, _) =>
            Signals.Guard(() => scroll(dx, dy, gtk_event_controller_get_current_event_state(self)), false) ? 1 : 0));
        w.AddController(c);
    }

    public static void OnClick(W w, Action<int, double, double, uint> press, uint button = 0)
    {
        var g = new GObj(gtk_gesture_click_new());
        gtk_gesture_single_set_button(g.H, button);
        Signals.Connect(g.H, "pressed", new SigPress((self, n, x, y, _) =>
            Signals.Guard(() => press(n, x, y, gtk_gesture_single_get_current_button(self)))));
        w.AddController(g);
    }

    public static void OnDrag(W w, Action<double, double> begin, Action<double, double> update, Action<double, double> end, uint button = 1)
    {
        var g = new GObj(gtk_gesture_drag_new());
        gtk_gesture_single_set_button(g.H, button);
        Signals.Connect(g.H, "drag-begin", new SigXY((_, x, y, _) => Signals.Guard(() => begin?.Invoke(x, y))));
        Signals.Connect(g.H, "drag-update", new SigXY((_, x, y, _) => Signals.Guard(() => update?.Invoke(x, y))));
        Signals.Connect(g.H, "drag-end", new SigXY((_, x, y, _) => Signals.Guard(() => end?.Invoke(x, y))));
        w.AddController(g);
    }

    public static void OnKey(W w, Func<uint, uint, bool> pressed)
    {
        var c = new GObj(gtk_event_controller_key_new());
        Signals.Connect(c.H, "key-pressed", new SigKey((_, kv, _, state, _) => Signals.Guard(() => pressed(kv, state), false) ? 1 : 0));
        w.AddController(c);
    }

    // --- рисование ---
    [DllImport(Lib.Gtk)] static extern void gtk_drawing_area_set_draw_func(IntPtr a, IntPtr fn, IntPtr data, IntPtr destroy);

    public static W DrawingArea(Action<Cairo, int, int> draw, params (string, object)[] props)
    {
        var a = new W("GtkDrawingArea", props);
        DrawFunc fn = (_, cr, w, h, _) => Signals.Guard(() => draw(new Cairo(cr), w, h));
        var gh = GCHandle.Alloc(fn);
        gtk_drawing_area_set_draw_func(a.H, Marshal.GetFunctionPointerForDelegate(fn), GCHandle.ToIntPtr(gh), Signals.FreeHandlePtr);
        return a;
    }

    // --- списки ---
    [DllImport(Lib.Gtk)] static extern void gtk_list_box_set_header_func(IntPtr b, IntPtr fn, IntPtr data, IntPtr destroy);
    [DllImport(Lib.Gtk)] public static extern void gtk_list_box_row_set_header(IntPtr row, IntPtr header);
    [DllImport(Lib.Gtk)] public static extern IntPtr gtk_list_box_get_selected_row(IntPtr b);
    [DllImport(Lib.Gtk)] public static extern void gtk_list_box_select_row(IntPtr b, IntPtr row);
    [DllImport(Lib.Gtk)] public static extern void gtk_list_box_invalidate_headers(IntPtr b);

    public static void HeaderFunc(W listbox, Action<IntPtr, IntPtr> fn)
    {
        HeaderFunc cb = (row, before, _) => Signals.Guard(() => fn(row, before));
        var gh = GCHandle.Alloc(cb);
        gtk_list_box_set_header_func(listbox.H, Marshal.GetFunctionPointerForDelegate(cb), GCHandle.ToIntPtr(gh), Signals.FreeHandlePtr);
    }

    // --- меню и действия ---
    [DllImport(Lib.Gio)] static extern IntPtr g_menu_new();
    [DllImport(Lib.Gio)] static extern void g_menu_append(IntPtr m, [MarshalAs(UnmanagedType.LPUTF8Str)] string label, [MarshalAs(UnmanagedType.LPUTF8Str)] string action);
    [DllImport(Lib.Gio)] static extern IntPtr g_simple_action_new([MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr ptype);
    [DllImport(Lib.Gio)] static extern void g_action_map_add_action(IntPtr map, IntPtr action);
    [DllImport(Lib.GLib)] static extern IntPtr g_variant_type_new([MarshalAs(UnmanagedType.LPUTF8Str)] string t);
    [DllImport(Lib.GLib)] static extern IntPtr g_variant_get_string(IntPtr v, IntPtr len);
    [DllImport(Lib.Gtk)] static extern void gtk_application_set_accels_for_action(IntPtr app, [MarshalAs(UnmanagedType.LPUTF8Str)] string action, IntPtr accels);

    public static GObj Menu(params (string Label, string Action)[] items)
    {
        var m = g_menu_new();
        foreach (var (l, a) in items) g_menu_append(m, l, a);
        return new GObj(m);
    }

    public static void Action(GObj map, string name, Action activate)
    {
        var a = new GObj(g_simple_action_new(name, IntPtr.Zero));
        a.OnP("activate", _ => activate());
        g_action_map_add_action(map.H, a.H);
    }

    public static void ActionStr(GObj map, string name, Action<string> activate)
    {
        var a = new GObj(g_simple_action_new(name, g_variant_type_new("s")));
        a.OnP("activate", v => activate(Lib.Str(g_variant_get_string(v, IntPtr.Zero))));
        g_action_map_add_action(map.H, a.H);
    }

    public static void Accels(GObj app, string action, params string[] accels)
    {
        var arr = Lib.Strv(accels);
        gtk_application_set_accels_for_action(app.H, action, arr);
        Lib.FreeStrv(arr, accels.Length);
    }

    // --- текстовый буфер ---
    [DllImport(Lib.Gtk)] public static extern IntPtr gtk_text_buffer_get_tag_table(IntPtr b);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_text_tag_new([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib.Gtk)] static extern int gtk_text_tag_table_add(IntPtr t, IntPtr tag);
    [DllImport(Lib.Gtk)] static extern void gtk_text_buffer_get_end_iter(IntPtr b, IntPtr iter);
    [DllImport(Lib.Gtk)] static extern void gtk_text_buffer_get_start_iter(IntPtr b, IntPtr iter);
    [DllImport(Lib.Gtk)] static extern void gtk_text_buffer_get_iter_at_offset(IntPtr b, IntPtr iter, int off);
    [DllImport(Lib.Gtk)] static extern int gtk_text_buffer_get_char_count(IntPtr b);
    [DllImport(Lib.Gtk)] static extern int gtk_text_buffer_get_line_count(IntPtr b);
    [DllImport(Lib.Gtk)] static extern int gtk_text_buffer_get_iter_at_line(IntPtr b, IntPtr iter, int line);
    [DllImport(Lib.Gtk)] static extern void gtk_text_buffer_insert(IntPtr b, IntPtr iter, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int len);
    [DllImport(Lib.Gtk)] static extern void gtk_text_buffer_apply_tag_by_name(IntPtr b, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr s, IntPtr e);
    [DllImport(Lib.Gtk)] static extern void gtk_text_buffer_set_text(IntPtr b, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int len);
    [DllImport(Lib.Gtk)] static extern void gtk_text_buffer_delete(IntPtr b, IntPtr s, IntPtr e);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_text_buffer_get_text(IntPtr b, IntPtr s, IntPtr e, int hidden);

    public sealed class TextBuf(IntPtr h) : GObj(h)
    {
        const int IterSize = 128;

        public void Tag(string name, params (string, object)[] props)
        {
            var t = new GObj(gtk_text_tag_new(name));
            foreach (var (k, v) in props) t.Set(k, v);
            gtk_text_tag_table_add(gtk_text_buffer_get_tag_table(H), t.H);
        }

        public void Append(string text, string tag = null)
        {
            var a = Marshal.AllocHGlobal(IterSize);
            var b = Marshal.AllocHGlobal(IterSize);
            try
            {
                int off = gtk_text_buffer_get_char_count(H);
                gtk_text_buffer_get_end_iter(H, a);
                gtk_text_buffer_insert(H, a, text, -1);
                if (tag != null)
                {
                    gtk_text_buffer_get_iter_at_offset(H, a, off);
                    gtk_text_buffer_get_end_iter(H, b);
                    gtk_text_buffer_apply_tag_by_name(H, tag, a, b);
                }
            }
            finally { Marshal.FreeHGlobal(a); Marshal.FreeHGlobal(b); }
        }

        public void SetText(string t) => gtk_text_buffer_set_text(H, t, -1);
        public int Lines => gtk_text_buffer_get_line_count(H);

        public void DeleteLines(int count)
        {
            var a = Marshal.AllocHGlobal(IterSize);
            var b = Marshal.AllocHGlobal(IterSize);
            gtk_text_buffer_get_start_iter(H, a);
            gtk_text_buffer_get_iter_at_line(H, b, count);
            gtk_text_buffer_delete(H, a, b);
            Marshal.FreeHGlobal(a); Marshal.FreeHGlobal(b);
        }

        public string Text
        {
            get
            {
                var a = Marshal.AllocHGlobal(IterSize);
                var b = Marshal.AllocHGlobal(IterSize);
                gtk_text_buffer_get_start_iter(H, a);
                gtk_text_buffer_get_end_iter(H, b);
                var p = gtk_text_buffer_get_text(H, a, b, 0);
                var s = Lib.Str(p);
                g_free(p);
                Marshal.FreeHGlobal(a); Marshal.FreeHGlobal(b);
                return s ?? "";
            }
        }
    }

    [DllImport(Lib.Gtk)] static extern void gtk_progress_bar_pulse(IntPtr p);
    public static void ProgressPulse(W bar) => gtk_progress_bar_pulse(bar.H);

    // --- регулировки (GtkAdjustment) ---
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_scrolled_window_get_vadjustment(IntPtr s);
    public static GObj VAdjustment(W scrolled) => new(gtk_scrolled_window_get_vadjustment(scrolled.H));
}
