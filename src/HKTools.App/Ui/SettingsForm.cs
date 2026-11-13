using System.Runtime.InteropServices;
using HKTools.Core;
using HKTools.Ui.Native;

namespace HKTools.Ui;

/// <summary>Автоматическая форма настроек по декларативной схеме (Field) — аналог ScriptSettingsControl.</summary>
internal sealed class SettingsForm
{
    public readonly W Root = G.VBox();
    readonly Script _script;
    readonly Action<string, object> _onChange;
    public Dictionary<string, object> Values = new();
    readonly Dictionary<string, (Field F, W Row)> _rows = new();
    readonly List<(W Group, List<string> Keys)> _groups = new();
    bool _guard;

    static readonly Dictionary<string, string> TabIcons = new()
    {
        ["Видео"] = "video-x-generic-symbolic", ["Аудио"] = "audio-x-generic-symbolic", ["Субтитры"] = "font-x-generic-symbolic",
        ["Общие"] = "emblem-system-symbolic", ["Основные"] = "emblem-system-symbolic", ["Движок"] = "applications-engineering-symbolic",
        ["Экспорт"] = "document-save-symbolic",
    };

    [DllImport(Lib.Adw)] static extern IntPtr adw_view_stack_add_titled(IntPtr s, IntPtr child, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);

    public SettingsForm(Script script, Action<string, object> onChange = null)
    {
        _script = script;
        _onChange = onChange;
        Rebuild();
    }

    public void Rebuild()
    {
        Root.Clear();
        _rows.Clear();
        _groups.Clear();
        Values = _script.GetValues();
        var fields = _script.Fields(Values);
        if (fields.Count == 0)
        {
            Root.Add(new W("AdwStatusPage", ("icon-name", "emblem-ok-symbolic"), ("title", "Нет настроек"),
                ("description", "Этот инструмент работает без дополнительных параметров"), ("vexpand", true)).Css("compact"));
            return;
        }
        var tabs = new List<(string Tab, List<(string Section, List<Field> Fields)> Sections)>();
        foreach (var f in fields)
        {
            var t = tabs.FindIndex(x => x.Tab == f.Tab);
            if (t < 0) { tabs.Add((f.Tab, new())); t = tabs.Count - 1; }
            var secs = tabs[t].Sections;
            var s = secs.FindIndex(x => x.Section == f.Section);
            if (s < 0) { secs.Add((f.Section, new())); s = secs.Count - 1; }
            secs[s].Fields.Add(f);
        }
        bool useTabs = tabs.Count > 1;
        var stack = useTabs ? new W("AdwViewStack", ("vexpand", true)) : null;
        foreach (var (tab, sections) in tabs)
        {
            var page = new W("AdwPreferencesPage");
            foreach (var (section, flist) in sections)
            {
                var group = new W("AdwPreferencesGroup", ("title", section));
                var keys = new List<string>();
                foreach (var f in flist)
                {
                    var row = MakeRow(f);
                    if (row == null) continue;
                    group.Add(row);
                    keys.Add(f.Key);
                    keys.AddRange(f.Children.Select(c => c.Key));
                }
                page.Add(group);
                _groups.Add((group, keys));
            }
            if (stack != null)
            {
                var name = tab == "" ? "Общие" : tab;
                var sp = new GObj(adw_view_stack_add_titled(stack.H, page.H, name, name));
                sp.Set("icon-name", TabIcons.GetValueOrDefault(tab, "emblem-system-symbolic"));
            }
            else
            {
                page.Set("vexpand", true);
                Root.Add(page);
            }
        }
        if (stack != null)
        {
            Root.Add(new W("AdwViewSwitcher", ("stack", stack), ("policy", 1), ("margin-top", 6), ("margin-start", 6), ("margin-end", 6)));
            Root.Add(stack);
        }
        var reset = G.Button("Сбросить настройки инструмента", css: "flat");
        reset.Set("halign", Align.Center);
        reset.Set("margin-bottom", 8);
        reset.On("clicked", () => G.Confirm(Root, "Сбросить настройки?", $"Все параметры «{_script.Name}» вернутся к значениям по умолчанию.",
            Reset, "Сбросить", destructive: true));
        Root.Add(reset);
        UpdateStates();
    }

    void Reset()
    {
        Settings.Instance.ResetScript(_script.Id);
        Rebuild();
        _onChange?.Invoke("*", null);
    }

    void SetVal(Field f, object value)
    {
        if (_guard) return;
        value = f.Coerce(value);
        Values[f.Key] = value;
        Settings.Instance.SetScriptValue(_script.Id, f.Key, value);
        UpdateStates();
        _onChange?.Invoke(f.Key, value);
    }

    /// <summary>Программно изменить значение (например, сдвиг из окна синхронизации).</summary>
    public void SetValue(string key, object value)
    {
        Settings.Instance.SetScriptValue(_script.Id, key, value);
        Values[key] = value;
        if (!_rows.TryGetValue(key, out var entry)) return;
        var (f, row) = entry;
        _guard = true;
        try
        {
            if (row.Is("AdwSwitchRow")) row.Set("active", Json.ToBool(value));
            else if (row.Is("AdwSpinRow")) row.Set("value", Json.ToDouble(value));
            else if (row.Is("AdwEntryRow")) row.Set("text", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
            else if (row.Is("AdwComboRow") && f.Options != null && f.Options.IndexOf(value?.ToString()) is var i and >= 0) row.Set("selected", (uint)i);
        }
        finally { _guard = false; }
        UpdateStates();
    }

    void UpdateStates()
    {
        foreach (var (f, row) in _rows.Values)
        {
            row.Visible = f.IsVisible(Values);
            row.Sensitive = !f.IsDisabled(Values);
        }
        foreach (var (group, keys) in _groups)
            group.Visible = keys.Any(k => _rows.TryGetValue(k, out var r) && r.Row.Visible);
    }

    W MakeRow(Field f)
    {
        var val = Values.TryGetValue(f.Key, out var v) ? v : f.Default;
        W row;
        switch (f.Type)
        {
            case FieldType.Check:
            {
                row = new W("AdwSwitchRow", ("title", f.Label), ("active", Json.ToBool(val)), ("use-markup", false));
                if (f.Comment != "") row.Set("subtitle", f.Comment);
                var r = row;
                r.OnNotify("active", () => OnSwitch(r, f));
                break;
            }
            case FieldType.Combo:
            {
                var opts = f.Options ?? new List<string>();
                var sval = Convert.ToString(val) ?? "";
                if (!opts.Contains(sval))
                {
                    sval = opts.Contains(f.Default?.ToString()) ? f.Default.ToString() : opts.FirstOrDefault() ?? "";
                    Values[f.Key] = sval;
                    Settings.Instance.SetScriptValue(_script.Id, f.Key, sval);
                }
                row = new W("AdwComboRow", ("title", f.Label), ("model", G.StringList(opts)), ("use-markup", false));
                if (opts.Count > 0) row.Set("selected", (uint)opts.IndexOf(sval));
                if (f.Comment != "") row.Set("subtitle", f.Comment);
                var r = row;
                r.OnNotify("selected", () =>
                {
                    var i = Convert.ToInt64(r.Get("selected"));
                    if (i >= 0 && i < opts.Count) SetVal(f, opts[(int)i]);
                });
                break;
            }
            case FieldType.Text:
            {
                row = new W("AdwEntryRow", ("title", f.Label), ("text", Convert.ToString(val) ?? ""), ("use-markup", false));
                if (f.Comment != "") row.End(InfoButton(f.Comment));
                var r = row;
                r.On("changed", () => SetVal(f, r.GetStr("text")));
                if (f.Browse != null)
                {
                    row.End(G.IconButton(f.Browse == "file" ? "document-open-symbolic" : "folder-open-symbolic", "Выбрать…", () => Browse(r, f)));
                    row.End(G.IconButton("edit-clear-symbolic", "Очистить", () => r.Set("text", "")));
                }
                break;
            }
            case FieldType.Int:
            case FieldType.Float:
            {
                bool isF = f.Type == FieldType.Float;
                double lo = f.Min ?? (isF ? -1e9 : -2_000_000_000), hi = f.Max ?? (isF ? 1e9 : 2_000_000_000);
                double step = f.Step ?? (isF ? 0.01 : 1);
                row = G.SpinRow(lo, hi, step);
                row.Set("use-markup", false);
                row.Set("title", f.Label);
                row.Set("digits", (uint)(isF ? f.Digits : 0));
                row.Set("value", Json.ToDouble(val, Json.ToDouble(f.Default)));
                if (f.Comment != "") row.Set("subtitle", f.Comment);
                var r = row;
                r.OnNotify("value", () =>
                {
                    var d = r.GetDouble("value");
                    SetVal(f, isF ? Math.Round(d, f.Digits) : (object)(long)Math.Round(d));
                });
                break;
            }
            case FieldType.Keywords:
                row = new KeywordsRow(f, val, items => SetVal(f, items)).Row;
                break;
            case FieldType.Expander:
            {
                row = new W("AdwExpanderRow", ("title", f.Label), ("use-markup", false));
                if (f.Comment != "") row.Set("subtitle", f.Comment);
                if (f.Toggle)
                {
                    row.Set("show-enable-switch", true);
                    row.Set("enable-expansion", Json.ToBool(val));
                    row.Set("expanded", Json.ToBool(val));
                    var r = row;
                    r.OnNotify("enable-expansion", () =>
                    {
                        var en = r.GetBool("enable-expansion");
                        if (en) r.Set("expanded", true);
                        SetVal(f, en);
                    });
                }
                foreach (var c in f.Children)
                    if (MakeRow(c) is { } cr) row.Add(cr);
                break;
            }
            default:
                return null;
        }
        _rows[f.Key] = (f, row);
        return row;
    }

    void OnSwitch(W row, Field f)
    {
        if (_guard) return;
        bool active = row.GetBool("active");
        if (active && f.Warning is { } w)
        {
            G.Confirm(Root, w.Title, w.Text, () => SetVal(f, true), "Включить", onNo: () =>
            {
                _guard = true;
                row.Set("active", false);
                _guard = false;
            });
            return;
        }
        SetVal(f, active);
    }

    void Browse(W row, Field f)
    {
        var cur = row.GetStr("text");
        var start = cur != "" ? Path.GetDirectoryName(Paths.ExpandUser(cur)) : Settings.Instance.GetStr("last_dir");
        if (f.Browse == "folder") G.PickFolder(Root, f.Label, p => row.Set("text", p), start);
        else G.PickFiles(Root, f.Label, ps => row.Set("text", ps[0]), multiple: false, start: start);
    }

    /// <summary>Кнопка «i» с пояснением (у EntryRow нет подзаголовка).</summary>
    public static W InfoButton(string text)
    {
        var label = G.Label(text, ("wrap", true), ("max-width-chars", 40), ("margin-top", 6), ("margin-bottom", 6),
            ("margin-start", 6), ("margin-end", 6), ("xalign", 0f));
        var pop = new W("GtkPopover", ("child", label));
        return new W("GtkMenuButton", ("icon-name", "help-about-symbolic"), ("valign", Align.Center), ("tooltip-text", text), ("popover", pop)).Css("flat");
    }
}

/// <summary>Список ключевых слов с флажками активности (KeywordList из Windows-версии).</summary>
internal sealed class KeywordsRow
{
    public readonly W Row;
    readonly Field _field;
    readonly List<KwItem> _items;
    readonly Action<List<object>> _onChange;
    readonly List<W> _children = new();

    public KeywordsRow(Field f, object value, Action<List<object>> onChange)
    {
        _field = f;
        _items = KwItem.Parse(value);
        _onChange = onChange;
        Row = new W("AdwExpanderRow", ("title", f.Label), ("use-markup", false));
        Rebuild();
    }

    string Summary()
    {
        if (_items.Count == 0) return _field.Comment != "" ? _field.Comment : "Пусто";
        var active = _items.Where(i => i.Active).Select(i => i.Word).ToList();
        var txt = string.Join(", ", active.Take(4)) + (active.Count > 4 ? "…" : "");
        return $"Активно {active.Count} из {_items.Count}" + (txt != "" ? $": {txt}" : "");
    }

    void Emit()
    {
        Row.Set("subtitle", Summary());
        _onChange(KwItem.ToStore(_items));
    }

    void Rebuild()
    {
        foreach (var w in _children) Row.Remove(w);
        _children.Clear();
        Row.Set("subtitle", Summary());
        for (int idx = 0; idx < _items.Count; idx++)
        {
            var i = idx;
            var item = _items[i];
            var row = new W("AdwEntryRow", ("title", ""), ("text", item.Word));
            var check = new W("GtkCheckButton", ("active", item.Active), ("valign", Align.Center), ("tooltip-text", "Активно"));
            check.On("toggled", () => { _items[i].Active = check.GetBool("active"); Emit(); });
            row.Start(check);
            if (_field.ExtraFlag is { } flag)
            {
                var tb = new W("GtkToggleButton", ("label", flag.Label), ("active", item.OnlyPart), ("valign", Align.Center)).Css("flat");
                tb.On("toggled", () => { _items[i].OnlyPart = tb.GetBool("active"); Emit(); });
                row.End(tb);
            }
            row.End(G.IconButton("user-trash-symbolic", "Удалить", () => Delete(i)));
            row.On("changed", () =>
            {
                if (i < _items.Count) { _items[i].Word = row.GetStr("text"); Emit(); }
            });
            Row.Add(row);
            _children.Add(row);
        }
        var add = new W("AdwEntryRow", ("title", "Добавить…"), ("show-apply-button", true));
        void OnAdd()
        {
            var text = add.GetStr("text").Trim();
            if (text == "") return;
            _items.Add(new KwItem { Word = text, Active = true });
            Emit();
            MainLoop.Post(() => { Rebuild(); Row.Set("expanded", true); });
        }
        add.On("apply", OnAdd);
        add.On("entry-activated", OnAdd);
        Row.Add(add);
        _children.Add(add);
    }

    void Delete(int i)
    {
        if (i >= _items.Count) return;
        _items.RemoveAt(i);
        Emit();
        MainLoop.Post(() => { Rebuild(); Row.Set("expanded", true); });
    }
}
