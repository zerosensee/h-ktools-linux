using System.Runtime.InteropServices;

namespace HKTools.Ui.Native;

/// <summary>Обёртка над GObject: свойства по имени (через GParamSpec) и сигналы с управляемыми обработчиками.</summary>
internal class GObj
{
    public readonly IntPtr H;
    public GObj(IntPtr h) { H = h; }
    public override bool Equals(object o) => o is GObj g && g.H == H;
    public override int GetHashCode() => H.GetHashCode();
    public static implicit operator IntPtr(GObj o) => o?.H ?? IntPtr.Zero;

    [DllImport(Lib.GObject)] static extern IntPtr g_object_class_find_property(IntPtr klass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib.GObject)] static extern void g_object_set_property(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr value);
    [DllImport(Lib.GObject)] static extern void g_object_get_property(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr value);
    [DllImport(Lib.GObject)] static extern IntPtr g_object_new_with_properties(nuint type, uint n, IntPtr names, IntPtr values);
    [DllImport(Lib.GObject)] public static extern IntPtr g_object_ref(IntPtr o);
    [DllImport(Lib.GObject)] public static extern IntPtr g_object_ref_sink(IntPtr o);
    [DllImport(Lib.GObject)] public static extern void g_object_unref(IntPtr o);
    [DllImport(Lib.GObject)] static extern IntPtr g_object_bind_property(IntPtr src, [MarshalAs(UnmanagedType.LPUTF8Str)] string sp, IntPtr dst, [MarshalAs(UnmanagedType.LPUTF8Str)] string dp, int flags);

    static nuint PropType(IntPtr obj, string name)
    {
        var klass = Marshal.ReadIntPtr(obj);
        var pspec = g_object_class_find_property(klass, name);
        if (pspec == IntPtr.Zero) throw new ArgumentException($"У объекта {Lib.Str(GType.g_type_name((nuint)Marshal.ReadIntPtr(klass)))} нет свойства «{name}»");
        return (nuint)Marshal.ReadIntPtr(pspec, 24); // GParamSpec.value_type
    }

    /// <summary>Создать объект GObject с начальными свойствами.</summary>
    public static IntPtr New(string typeName, params (string Name, object Value)[] props)
    {
        var type = GType.Of(typeName);
        if (props.Length == 0) return g_object_new_with_properties(type, 0, IntPtr.Zero, IntPtr.Zero);
        var klass = g_type_class_ref(type);
        var names = Lib.Strv(props.Select(p => p.Name).ToList());
        var values = Marshal.AllocHGlobal(GValue.Size * props.Length);
        unsafe { new Span<byte>((void*)values, GValue.Size * props.Length).Clear(); }
        try
        {
            for (int i = 0; i < props.Length; i++)
            {
                var pspec = g_object_class_find_property(klass, props[i].Name);
                if (pspec == IntPtr.Zero) throw new ArgumentException($"У {typeName} нет свойства «{props[i].Name}»");
                GValue.Fill(values + i * GValue.Size, (nuint)Marshal.ReadIntPtr(pspec, 24), props[i].Value);
            }
            return g_object_new_with_properties(type, (uint)props.Length, names, values);
        }
        finally
        {
            for (int i = 0; i < props.Length; i++) GValue.g_value_unset(values + i * GValue.Size);
            Marshal.FreeHGlobal(values);
            Lib.FreeStrv(names, props.Length);
            g_type_class_unref(klass);
        }
    }

    [DllImport(Lib.GObject)] static extern IntPtr g_type_class_ref(nuint type);
    [DllImport(Lib.GObject)] static extern void g_type_class_unref(IntPtr klass);

    public void Set(string name, object value)
    {
        var t = PropType(H, name);
        var v = GValue.Alloc();
        try { GValue.Fill(v, t, value); g_object_set_property(H, name, v); }
        finally { GValue.g_value_unset(v); Marshal.FreeHGlobal(v); }
    }

    public object Get(string name)
    {
        var t = PropType(H, name);
        var v = GValue.Alloc();
        try { GValue.g_value_init(v, t); g_object_get_property(H, name, v); return GValue.Read(v, t); }
        finally { GValue.g_value_unset(v); Marshal.FreeHGlobal(v); }
    }

    public string GetStr(string name) => Get(name) as string ?? "";
    public bool GetBool(string name) => Get(name) is true;
    public int GetInt(string name) => Convert.ToInt32(Get(name));
    public double GetDouble(string name) => Convert.ToDouble(Get(name));

    /// <summary>Связать свойство с свойством другого объекта (SYNC_CREATE = 2, INVERT_BOOLEAN = 4).</summary>
    public void Bind(string prop, GObj target, string targetProp, bool invert = false) =>
        g_object_bind_property(H, prop, target.H, targetProp, 2 | (invert ? 4 : 0));

    // --- сигналы ---
    public nuint On(string signal, Action a) => Signals.Connect(H, signal, new Sig0((_, _) => Signals.Guard(a)));
    public nuint OnP(string signal, Action<IntPtr> a) => Signals.Connect(H, signal, new SigP((_, p, _) => Signals.Guard(() => a(p))));
    public nuint OnB(string signal, Func<bool> f) => Signals.Connect(H, signal, new SigB((_, _) => Signals.Guard(f, false) ? 1 : 0));
    public nuint OnNotify(string prop, Action a) => OnP("notify::" + prop, _ => a());
    public void Block(nuint id) => Signals.g_signal_handler_block(H, id);
    public void Unblock(nuint id) => Signals.g_signal_handler_unblock(H, id);
}

/// <summary>Виджет GTK.</summary>
internal class W : GObj
{
    public W(IntPtr h) : base(h) { }
    public W(string typeName, params (string, object)[] props) : base(New(typeName, props)) { }

    [DllImport(Lib.Gtk)] static extern void gtk_widget_add_css_class(IntPtr w, [MarshalAs(UnmanagedType.LPUTF8Str)] string c);
    [DllImport(Lib.Gtk)] static extern void gtk_widget_remove_css_class(IntPtr w, [MarshalAs(UnmanagedType.LPUTF8Str)] string c);
    [DllImport(Lib.Gtk)] static extern int gtk_widget_has_css_class(IntPtr w, [MarshalAs(UnmanagedType.LPUTF8Str)] string c);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_widget_get_parent(IntPtr w);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_widget_get_root(IntPtr w);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_widget_get_first_child(IntPtr w);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_widget_get_next_sibling(IntPtr w);
    [DllImport(Lib.Gtk)] static extern void gtk_widget_queue_draw(IntPtr w);
    [DllImport(Lib.Gtk)] static extern int gtk_widget_get_width(IntPtr w);
    [DllImport(Lib.Gtk)] static extern int gtk_widget_get_height(IntPtr w);
    [DllImport(Lib.Gtk)] static extern void gtk_widget_add_controller(IntPtr w, IntPtr c);
    [DllImport(Lib.Gtk)] static extern int gtk_widget_grab_focus(IntPtr w);
    [DllImport(Lib.Gtk)] static extern void gtk_widget_set_size_request(IntPtr w, int width, int height);
    [DllImport(Lib.Gtk)] static extern void gtk_widget_set_cursor_from_name(IntPtr w, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_widget_get_clipboard(IntPtr w);
    [DllImport(Lib.Gtk)] static extern void gtk_widget_unparent(IntPtr w);
    [DllImport(Lib.Gtk)] static extern int gtk_widget_get_mapped(IntPtr w);
    [DllImport(Lib.Gtk)] static extern int gtk_widget_activate_action_variant(IntPtr w, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr args);

    public W Css(params string[] classes) { foreach (var c in classes) if (!string.IsNullOrEmpty(c)) gtk_widget_add_css_class(H, c); return this; }
    public void Uncss(string c) => gtk_widget_remove_css_class(H, c);
    public void ToggleCss(string c, bool on) { if (on) Css(c); else Uncss(c); }
    public bool HasCss(string c) => gtk_widget_has_css_class(H, c) != 0;
    public W Parent => gtk_widget_get_parent(H) is var p && p != IntPtr.Zero ? new W(p) : null;
    public IntPtr Root => gtk_widget_get_root(H);
    public void Redraw() => gtk_widget_queue_draw(H);
    public int Width => gtk_widget_get_width(H);
    public int Height => gtk_widget_get_height(H);
    public bool Mapped => gtk_widget_get_mapped(H) != 0;
    public void AddController(GObj c) => gtk_widget_add_controller(H, c.H);
    public void Focus() => gtk_widget_grab_focus(H);
    public void Size(int w, int h) => gtk_widget_set_size_request(H, w, h);
    public void Cursor(string name) => gtk_widget_set_cursor_from_name(H, name);
    public void Unparent() => gtk_widget_unparent(H);
    public void Activate(string action) => gtk_widget_activate_action_variant(H, action, IntPtr.Zero);

    public IEnumerable<W> Children()
    {
        var list = new List<W>();
        for (var c = gtk_widget_get_first_child(H); c != IntPtr.Zero; c = gtk_widget_get_next_sibling(c)) list.Add(new W(c));
        return list;
    }

    public bool Visible { get => GetBool("visible"); set => Set("visible", value); }
    public bool Sensitive { get => GetBool("sensitive"); set => Set("sensitive", value); }
    public string Tooltip { set => Set("tooltip-text", value); }

    // --- буфер обмена ---
    [DllImport(Lib.Gtk)] static extern void gdk_clipboard_set_text(IntPtr cb, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Lib.Gtk)] static extern void gdk_clipboard_read_text_async(IntPtr cb, IntPtr cancellable, IntPtr callback, IntPtr data);
    [DllImport(Lib.Gtk)] static extern IntPtr gdk_clipboard_read_text_finish(IntPtr cb, IntPtr res, IntPtr error);
    [DllImport(Lib.GLib)] static extern void g_free(IntPtr p);

    public void Copy(string text) => gdk_clipboard_set_text(gtk_widget_get_clipboard(H), text);

    public void Paste(Action<string> done)
    {
        AsyncReady cb = null;
        GCHandle gh = default;
        cb = (src, res, _) =>
        {
            gh.Free();
            var p = gdk_clipboard_read_text_finish(src, res, IntPtr.Zero);
            var s = Lib.Str(p);
            if (p != IntPtr.Zero) g_free(p);
            Signals.Guard(() => done(s));
        };
        gh = GCHandle.Alloc(cb);
        gdk_clipboard_read_text_async(gtk_widget_get_clipboard(H), IntPtr.Zero, Marshal.GetFunctionPointerForDelegate(cb), IntPtr.Zero);
    }

    // --- контейнеры ---
    [DllImport(Lib.Gtk)] static extern void gtk_box_append(IntPtr b, IntPtr c);
    [DllImport(Lib.Gtk)] static extern void gtk_box_prepend(IntPtr b, IntPtr c);
    [DllImport(Lib.Gtk)] static extern void gtk_box_remove(IntPtr b, IntPtr c);
    [DllImport(Lib.Gtk)] static extern void gtk_list_box_append(IntPtr b, IntPtr c);
    [DllImport(Lib.Gtk)] static extern void gtk_list_box_remove(IntPtr b, IntPtr c);
    [DllImport(Lib.Gtk)] static extern void gtk_flow_box_append(IntPtr b, IntPtr c);
    [DllImport(Lib.Adw)] static extern void adw_header_bar_pack_start(IntPtr b, IntPtr c);
    [DllImport(Lib.Adw)] static extern void adw_header_bar_pack_end(IntPtr b, IntPtr c);
    [DllImport(Lib.Adw)] static extern void adw_toolbar_view_add_top_bar(IntPtr b, IntPtr c);
    [DllImport(Lib.Adw)] static extern void adw_toolbar_view_add_bottom_bar(IntPtr b, IntPtr c);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_stack_add_named(IntPtr s, IntPtr c, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib.Gtk)] static extern void gtk_stack_remove(IntPtr s, IntPtr c);
    [DllImport(Lib.Adw)] static extern void adw_preferences_group_add(IntPtr g, IntPtr c);
    [DllImport(Lib.Adw)] static extern void adw_preferences_group_remove(IntPtr g, IntPtr c);
    [DllImport(Lib.Adw)] static extern void adw_preferences_page_add(IntPtr p, IntPtr g);
    [DllImport(Lib.Adw)] static extern void adw_preferences_page_remove(IntPtr p, IntPtr g);
    [DllImport(Lib.Adw)] static extern void adw_preferences_dialog_add(IntPtr d, IntPtr p);
    [DllImport(Lib.Adw)] static extern void adw_action_row_add_suffix(IntPtr r, IntPtr c);
    [DllImport(Lib.Adw)] static extern void adw_action_row_add_prefix(IntPtr r, IntPtr c);
    [DllImport(Lib.Adw)] static extern void adw_entry_row_add_suffix(IntPtr r, IntPtr c);
    [DllImport(Lib.Adw)] static extern void adw_expander_row_add_row(IntPtr r, IntPtr c);
    [DllImport(Lib.Adw)] static extern void adw_expander_row_add_suffix(IntPtr r, IntPtr c);
    [DllImport(Lib.Adw)] static extern void adw_expander_row_add_prefix(IntPtr r, IntPtr c);
    [DllImport(Lib.Adw)] static extern void adw_expander_row_remove(IntPtr r, IntPtr c);

    public W Add(params W[] children)
    {
        foreach (var c in children)
        {
            if (c == null) continue;
            if (Is("GtkBox")) gtk_box_append(H, c.H);
            else if (Is("GtkListBox")) gtk_list_box_append(H, c.H);
            else if (Is("GtkFlowBox")) gtk_flow_box_append(H, c.H);
            else if (Is("AdwPreferencesGroup")) adw_preferences_group_add(H, c.H);
            else if (Is("AdwPreferencesPage")) adw_preferences_page_add(H, c.H);
            else if (Is("AdwPreferencesDialog")) adw_preferences_dialog_add(H, c.H);
            else if (Is("AdwExpanderRow")) adw_expander_row_add_row(H, c.H);
            else Set("child", c);
        }
        return this;
    }

    public void Prepend(W c) => gtk_box_prepend(H, c.H);

    public void Remove(W c)
    {
        if (Is("GtkBox")) gtk_box_remove(H, c.H);
        else if (Is("GtkListBox")) gtk_list_box_remove(H, c.H);
        else if (Is("AdwPreferencesGroup")) adw_preferences_group_remove(H, c.H);
        else if (Is("AdwPreferencesPage")) adw_preferences_page_remove(H, c.H);
        else if (Is("AdwExpanderRow")) adw_expander_row_remove(H, c.H);
        else if (Is("GtkStack")) gtk_stack_remove(H, c.H);
        else c.Unparent();
    }

    public void Clear() { foreach (var c in Children()) Remove(c); }

    public W Start(W c)
    {
        if (Is("AdwHeaderBar")) adw_header_bar_pack_start(H, c.H);
        else if (Is("AdwEntryRow")) adw_entry_row_add_prefix(H, c.H);
        else if (Is("AdwExpanderRow")) adw_expander_row_add_prefix(H, c.H);
        else adw_action_row_add_prefix(H, c.H);
        return this;
    }

    public W End(W c)
    {
        if (Is("AdwHeaderBar")) adw_header_bar_pack_end(H, c.H);
        else if (Is("AdwEntryRow")) adw_entry_row_add_suffix(H, c.H);
        else if (Is("AdwExpanderRow")) adw_expander_row_add_suffix(H, c.H);
        else adw_action_row_add_suffix(H, c.H);
        return this;
    }

    [DllImport(Lib.Adw)] static extern void adw_entry_row_add_prefix(IntPtr r, IntPtr c);

    public W Top(W bar) { adw_toolbar_view_add_top_bar(H, bar.H); return this; }
    public W Bottom(W bar) { adw_toolbar_view_add_bottom_bar(H, bar.H); return this; }
    public void AddNamed(W child, string name) => gtk_stack_add_named(H, child.H, name);

    public bool Is(string typeName) => GType.g_type_is_a((nuint)Marshal.ReadIntPtr(Marshal.ReadIntPtr(H)), GType.Of(typeName)) != 0;

    /// <summary>Свойства-сеттеры в стиле fluent.</summary>
    public W P(string name, object value) { Set(name, value); return this; }
}
