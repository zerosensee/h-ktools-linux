using HKTools.Core;
using HKTools.Media;
using HKTools.Scripts;
using HKTools.Ui.Native;

namespace HKTools.Ui;

/// <summary>Строка очереди.</summary>
internal sealed class ItemRow
{
    static readonly Dictionary<string, (string Icon, string Css)> StatusIcons = new()
    {
        ["pending"] = ("content-loading-symbolic", ""), ["running"] = ("media-playback-start-symbolic", ""),
        ["done"] = ("emblem-ok-symbolic", "kt-status-done"), ["partial"] = ("dialog-warning-symbolic", "kt-status-partial"),
        ["failed"] = ("dialog-error-symbolic", "kt-status-failed"), ["skipped"] = ("media-skip-forward-symbolic", "kt-status-skipped"),
        ["cancelled"] = ("process-stop-symbolic", "kt-status-cancelled"),
    };

    public readonly W Row;
    public readonly QueueItem Item;
    public readonly W Extras;
    public W ChartBtn, FmtDd, SubDd;
    public nuint FmtHandler, SubHandler;
    readonly WorkPanel _panel;
    readonly W _iconStack, _statusIcon, _spinner, _title, _subtitle, _bar, _badge, _percent, _openBtn, _removeBtn;

    public ItemRow(WorkPanel panel, QueueItem item)
    {
        _panel = panel;
        Item = item;
        Row = new W("GtkListBoxRow", ("activatable", false), ("selectable", false));
        var box = G.Box(spacing: 10).Css("kt-item-row");
        _iconStack = new W("GtkStack");
        _statusIcon = new W("GtkImage", ("pixel-size", 16));
        _spinner = new W("GtkSpinner");
        _iconStack.AddNamed(_statusIcon, "icon");
        _iconStack.AddNamed(_spinner, "spin");
        box.Add(_iconStack);
        var mid = G.VBox(2, ("hexpand", true));
        _title = G.Label(item.Name, ("xalign", 0f), ("ellipsize", Ellipsize.Middle)).Css("kt-item-title");
        _title.Tooltip = item.Path;
        _subtitle = G.Label("", ("xalign", 0f), ("ellipsize", Ellipsize.End)).Css("dim-label", "caption");
        _bar = new W("GtkProgressBar", ("visible", false));
        mid.Add(_title, _subtitle, _bar);
        box.Add(mid);
        _badge = G.Label("", ("visible", false), ("valign", Align.Center)).Css("kt-badge");
        _percent = G.Label("", ("valign", Align.Center), ("width-chars", 6), ("xalign", 1f)).Css("numeric");
        Extras = G.Box(spacing: 2, props: [("valign", Align.Center)]);
        _openBtn = G.IconButton("folder-open-symbolic", "Показать результат", () =>
        {
            if (Item.Outputs.Count > 0) Desktop.ShowInFolder(Item.Outputs[0]);
        });
        _openBtn.Visible = false;
        _removeBtn = G.IconButton("window-close-symbolic", "Убрать из списка", () => panel.RemoveItem(item));
        box.Add(_badge, _percent, Extras, _openBtn, _removeBtn);
        Row.Set("child", box);
        panel.BuildRowExtras(this);
        Refresh();
    }

    public void Refresh()
    {
        var it = Item;
        var (icon, css) = StatusIcons.GetValueOrDefault(it.Status, StatusIcons["pending"]);
        foreach (var c in new[] { "kt-status-done", "kt-status-partial", "kt-status-failed", "kt-status-skipped", "kt-status-cancelled" }) _statusIcon.Uncss(c);
        if (css != "") _statusIcon.Css(css);
        bool running = it.Status == "running";
        if (running)
        {
            _iconStack.Set("visible-child-name", "spin");
            _spinner.Set("spinning", true);
        }
        else
        {
            _spinner.Set("spinning", false);
            _iconStack.Set("visible-child-name", "icon");
            _statusIcon.Set("icon-name", it.Status == "pending" ? _panel.PendingIcon(it) : icon);
        }
        _bar.Visible = running;
        _bar.Set("fraction", it.Progress / 100);
        _percent.Set("label", running ? $"{it.Progress:0}%" : "");
        var sub = it.Status != "pending" ? it.Message : _panel.RowSubtitle(it);
        _subtitle.Set("label", string.IsNullOrEmpty(sub) ? "" : sub.Split('\n')[0]);
        _subtitle.Set("tooltip-text", string.IsNullOrEmpty(sub) ? null : sub);
        _badge.Visible = it.Badge != "";
        _badge.Set("label", it.Badge);
        _openBtn.Visible = it.Outputs.Count > 0 && !running;
        _removeBtn.Sensitive = !running;
        _title.Set("label", _panel.RowTitle(it));
    }
}

/// <summary>Универсальная страница инструмента: очередь (drag&amp;drop), настройки, запуск и прогресс.</summary>
internal sealed class WorkPanel
{
    public readonly W Root;
    public readonly Script Script;
    public readonly MainWindow Window;
    public readonly SettingsForm Form;
    public readonly List<QueueItem> Items = new();
    public BatchRunner Runner;
    readonly Dictionary<int, ItemRow> _rows = new();
    readonly Dictionary<int, Result> _results = new();
    string _outDir;
    readonly W _toolbar, _listStack, _listbox, _dropFrame, _toastOverlay, _split;
    W _urlEntry, _statusLabel, _totalBar, _outBtn, _outContent, _outReset, _startBtn, _startContent;

    public bool Running => Runner != null;

    public WorkPanel(MainWindow window, Script script)
    {
        Window = window;
        Script = script;
        Root = G.VBox();
        _toolbar = new W("AdwToolbarView", ("vexpand", true));
        Root.Add(_toolbar);

        // --- шапка ---
        var header = G.Header(script.Name, script.Category);
        if (script.InputMode == "files")
        {
            header.Start(G.Button(icon: "list-add-symbolic", tooltip: "Добавить файлы (Ctrl+O)", onClick: AddFilesDialog));
            header.Start(G.Button(icon: "folder-new-symbolic", tooltip: "Добавить папку", onClick: AddFolderDialog));
        }
        var settingsToggle = new W("GtkToggleButton", ("icon-name", "sidebar-show-right-symbolic"), ("tooltip-text", "Настройки"),
            ("active", Settings.Instance.GetBool("show_settings_sidebar")));
        header.End(settingsToggle);
        var group = new GObj(GObj.New("GSimpleActionGroup"));
        G.Action(group, "clear", Clear);
        G.Action(group, "clear-done", ClearDone);
        G.Action(group, "reset", ResetStatuses);
        Native.ActionGroups.Insert(Root, "panel", group);
        header.End(new W("GtkMenuButton", ("icon-name", "view-more-symbolic"), ("tooltip-text", "Ещё"),
            ("menu-model", G.Menu(("Очистить список", "panel.clear"), ("Убрать завершённые", "panel.clear-done"), ("Сбросить статусы", "panel.reset")))));
        if (script.Id == "audio-transplant")
        {
            var sync = G.Button(tooltip: "Подобрать сдвиг по осциллограмме", onClick: OpenWaveform);
            sync.Set("child", new W("AdwButtonContent", ("icon-name", "media-playlist-shuffle-symbolic"), ("label", "Синхронизация")));
            header.End(sync);
        }
        if (script.Id == "speech-recognition")
        {
            var models = G.Button(tooltip: "Скачать или удалить модели whisper.cpp", onClick: () => Dialogs.WhisperModels(Window.Win, () => Form.Rebuild()));
            models.Set("child", new W("AdwButtonContent", ("icon-name", "folder-download-symbolic"), ("label", "Модели")));
            header.End(models);
        }
        _toolbar.Top(header);
        if (script.InputMode == "urls") _toolbar.Top(BuildUrlBar());

        // --- очередь + настройки ---
        _split = new W("AdwOverlaySplitView", ("sidebar-position", 1), ("min-sidebar-width", 340.0), ("max-sidebar-width", 460.0),
            ("sidebar-width-fraction", 0.36));
        settingsToggle.Bind("active", _split, "show-sidebar");
        _split.Bind("show-sidebar", settingsToggle, "active");
        settingsToggle.On("toggled", () => Settings.Instance.Set("show_settings_sidebar", settingsToggle.GetBool("active")));
        Form = new SettingsForm(script, OnSettingChanged);
        var side = G.VBox().Css("background");
        side.Add(G.Scrolled(Form.Root, hpolicy: 2));
        _split.Set("sidebar", side);

        _listStack = new W("GtkStack", ("transition-type", 1), ("vexpand", true));
        _listStack.AddNamed(BuildEmpty(), "empty");
        _listbox = new W("GtkListBox", ("selection-mode", 0), ("valign", Align.Start)).Css("boxed-list");
        var clamp = new W("AdwClamp", ("maximum-size", 1100), ("child", _listbox), ("margin-top", 12), ("margin-bottom", 12), ("margin-start", 12), ("margin-end", 12));
        _listStack.AddNamed(G.Scrolled(clamp), "list");
        _dropFrame = G.VBox();
        _dropFrame.Add(_listStack);
        _split.Set("content", _dropFrame);
        var bin = new W("AdwBreakpointBin", ("width-request", 360), ("height-request", 300), ("child", _split));
        MainWindow.Breakpoint(bin, "max-width: 820sp", _split, "collapsed");
        _toastOverlay = new W("AdwToastOverlay", ("child", bin));
        _toolbar.Set("content", _toastOverlay);
        _toolbar.Bottom(BuildBottom());
        if (script.InputMode == "urls") G.DropTarget(_dropFrame, files => AddUrls(files.Select(f => "file://" + f).ToList()), t => AddUrls(t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).ToList()));
        else if (script.InputMode == "files") G.DropTarget(_dropFrame, AddPaths);
        UpdateSummary();
    }

    // --- построение ---
    W BuildEmpty()
    {
        if (Script.InputMode == "urls")
            return new W("AdwStatusPage", ("icon-name", Script.Icon), ("title", "Вставьте ссылку"),
                ("description", "Вставьте одну или несколько ссылок в поле выше (Ctrl+V) или перетащите их из браузера."));
        if (Script.InputMode == "none")
            return new W("AdwStatusPage", ("icon-name", Script.Icon), ("title", Script.Name), ("description", Script.Description));
        var exts = (Script.Extensions ?? new HashSet<string>()).OrderBy(e => e, StringComparer.Ordinal).ToList();
        var desc = "Перетащите файлы или папки сюда\n";
        if (exts.Count > 0) desc += "Форматы: " + string.Join(" ", exts.Take(24).Select(e => e.TrimStart('.'))) + (exts.Count > 24 ? " …" : "");
        var page = new W("AdwStatusPage", ("icon-name", Script.Icon), ("title", Script.Name), ("description", desc));
        var btns = G.Box(spacing: 12, props: [("halign", Align.Center)]);
        btns.Add(G.Button("Добавить файлы", onClick: AddFilesDialog, css: ["pill", "suggested-action"]), G.Button("Добавить папку", onClick: AddFolderDialog, css: "pill"));
        page.Set("child", btns);
        return page;
    }

    W BuildUrlBar()
    {
        var box = G.Box(spacing: 6, props: [("margin-start", 12), ("margin-end", 12), ("margin-top", 6), ("margin-bottom", 6)]);
        _urlEntry = new W("GtkEntry", ("hexpand", true), ("placeholder-text", "https://… — ссылка на видео (можно несколько через пробел)"),
            ("primary-icon-name", "web-browser-symbolic"));
        _urlEntry.On("activate", AddUrlsFromEntry);
        box.Add(_urlEntry, G.IconButton("edit-paste-symbolic", "Вставить из буфера",
                () => Root.Paste(t => { if (!string.IsNullOrWhiteSpace(t)) AddUrls(t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).ToList()); }), flat: false),
            G.Button("Добавить", onClick: AddUrlsFromEntry, css: "suggested-action"));
        return box;
    }

    W BuildBottom()
    {
        var box = G.Box(spacing: 10).Css("kt-bottom-bar");
        var left = G.VBox(4, ("hexpand", true), ("valign", Align.Center));
        _statusLabel = G.Label("", ("xalign", 0f), ("ellipsize", Ellipsize.End)).Css("caption");
        _totalBar = new W("GtkProgressBar");
        left.Add(_statusLabel, _totalBar);
        box.Add(left);
        _outBtn = G.Button(tooltip: "Папка результатов", onClick: ChooseOutDir, css: "flat");
        _outContent = new W("AdwButtonContent", ("icon-name", "folder-symbolic"), ("label", "Рядом с исходником"));
        _outBtn.Set("child", _outContent);
        _outReset = G.IconButton("edit-clear-symbolic", "Сбросить папку результатов", () => { _outDir = null; RefreshOutLabel(); });
        _outReset.Visible = false;
        if (Script.InputMode != "none") box.Add(_outBtn, _outReset);
        _startBtn = G.Button(onClick: ToggleRun, css: ["suggested-action", "pill"]);
        _startContent = new W("AdwButtonContent", ("icon-name", "media-playback-start-symbolic"), ("label", "Запуск"));
        _startBtn.Set("child", _startContent);
        if (Script.InputMode != "none") box.Add(_startBtn);
        RefreshOutLabel();
        return box;
    }

    // --- хуки инструментов ---
    public string RowTitle(QueueItem item) =>
        Script.InputMode == "urls" ? (item.Extra.GetValueOrDefault("meta") as DlInfo)?.Title is { Length: > 0 } t ? t : item.Path : item.Name;

    public string RowSubtitle(QueueItem item)
    {
        var sub = Script.RowSubtitle(item, Items);
        if (sub == "" && item.Info != null)
        {
            var info = item.Info;
            var bits = new List<string> { Pages.HumanSize(info.Size) };
            if (info.Duration > 0) bits.Add(Pages.HumanDuration(info.Duration));
            if (info.Video.Count > 0) bits.Add(info.Video[0].Resolution);
            bits.Add($"дорожек: {info.MediaTracks.Count}");
            sub = string.Join(" · ", bits.Where(b => b != ""));
        }
        else if (sub == "" && Script.InputMode == "files")
        {
            try { sub = Pages.HumanSize(new FileInfo(item.Path).Length) + " · " + Path.GetDirectoryName(item.Path); }
            catch { sub = Path.GetDirectoryName(item.Path); }
        }
        if (Json.ToBool(item.Extra.GetValueOrDefault("loading"))) sub = "Анализ…";
        if (item.Extra.GetValueOrDefault("error") is string e) sub = "⚠ " + e;
        return sub;
    }

    public string PendingIcon(QueueItem item)
    {
        if (Script.Id != "mkv-assembly") return "content-loading-symbolic";
        if (Exts.AudioAll.Contains(item.Ext)) return "audio-x-generic-symbolic";
        if (Exts.Subtitles.Contains(item.Ext)) return "font-x-generic-symbolic";
        return "video-x-generic-symbolic";
    }

    public void BuildRowExtras(ItemRow row)
    {
        var item = row.Item;
        if (Script.TrackSelection != null) row.Extras.Add(G.IconButton("view-list-bullet-symbolic", "Дорожки…", () => OpenTracks(item)));
        if (Script.Id == "subtitles-convert") row.Extras.Add(G.IconButton("view-reveal-symbolic", "Предпросмотр и фильтр реплик", () => SubtitlePreview.Show(this, item)));
        if (Script.Id == "bitrate-viewer")
        {
            row.ChartBtn = G.IconButton("utilities-system-monitor-symbolic", "Открыть график", () => OpenChart(item));
            row.ChartBtn.Visible = false;
            row.Extras.Add(row.ChartBtn);
        }
        if (Script.Id == "mkv-assembly" && !Exts.VideoContainers.Contains(item.Ext)) row.Extras.Add(MuxMenu(item));
        if (Script.Id == "media-downloader") DownloaderControls(row);
    }

    // --- сборка MKV: меню сопутствующего файла ---
    W MuxMenu(QueueItem item)
    {
        var btn = new W("GtkMenuButton", ("icon-name", "document-properties-symbolic"), ("tooltip-text", "Роль и привязка"), ("valign", Align.Center)).Css("flat");
        var box = G.VBox(2, ("margin-top", 6), ("margin-bottom", 6), ("margin-start", 6), ("margin-end", 6));
        var pop = new W("GtkPopover", ("child", box));
        void Add(string label, Action cb)
        {
            var b = G.Button(css: "flat");
            b.Set("child", G.Label(label, ("xalign", 0f)));
            b.On("clicked", () =>
            {
                Native.Popovers.Popdown(pop);
                cb();
                RefreshRows();
            });
            box.Add(b);
        }
        pop.On("show", () =>
        {
            box.Clear();
            if (Exts.Subtitles.Contains(item.Ext))
            {
                var cur = item.Extra.GetValueOrDefault("mux_role") as string;
                foreach (var (role, label) in new[] { ("signs", "Роль: надписи"), ("full", "Роль: полные"), ("other", "Роль: прочие"), (null, "Роль: авто (по имени)") })
                    Add((cur == role ? "● " : "   ") + label, () => { if (role == null) item.Extra.Remove("mux_role"); else item.Extra["mux_role"] = role; });
            }
            else
            {
                bool main = Json.ToBool(item.Extra.GetValueOrDefault("mux_main"));
                Add((main ? "● " : "   ") + "Основная аудиодорожка", () => item.Extra["mux_main"] = !main);
                Add("   Заголовок дорожки…", () => Dialogs.AskText(Window.Win, "Заголовок аудиодорожки", item.Name,
                    item.Extra.GetValueOrDefault("mux_title") as string ?? "", t => { item.Extra["mux_title"] = t.Trim(); RefreshRows(); }));
            }
            box.Add(new W("GtkSeparator"));
            var pinned = item.Extra.GetValueOrDefault("mux_pinned") as string;
            foreach (var v in Items.Where(i => Exts.VideoContainers.Contains(i.Ext)).Take(30))
            {
                var stem = v.Stem;
                Add((pinned == stem ? "● " : "   ") + $"Привязать к: {v.Name}", () => item.Extra["mux_pinned"] = stem);
            }
            Add("   Привязка: авто (по имени)", () => item.Extra.Remove("mux_pinned"));
        });
        btn.Set("popover", pop);
        return btn;
    }

    // --- загрузчик: качество и субтитры у ссылки ---
    void DownloaderControls(ItemRow row)
    {
        row.FmtDd = new W("GtkDropDown", ("valign", Align.Center), ("tooltip-text", "Качество"));
        row.SubDd = new W("GtkDropDown", ("valign", Align.Center), ("tooltip-text", "Субтитры"));
        row.Extras.Add(row.FmtDd, row.SubDd);
        FillDownloader(row);
    }

    void FillDownloader(ItemRow row)
    {
        var item = row.Item;
        var meta = item.Extra.GetValueOrDefault("meta") as DlInfo;
        var formats = meta?.Formats ?? MediaDownloader.DefaultFormats.ToList();
        var subs = new List<string> { "none" };
        if (meta != null) { subs.AddRange(meta.Subtitles); subs.AddRange(meta.AutoSubtitles.Select(s => $"{s} (авто)")); }
        if (row.FmtHandler != 0) Signals.g_signal_handler_disconnect(row.FmtDd.H, row.FmtHandler);
        if (row.SubHandler != 0) Signals.g_signal_handler_disconnect(row.SubDd.H, row.SubHandler);
        row.FmtDd.Set("model", G.StringList(formats.Select(f => f.Label)));
        row.SubDd.Set("model", G.StringList(subs.Select(s => s == "none" ? "Без субтитров" : s)));
        var cur = item.Extra.GetValueOrDefault("format_arg") as string;
        row.FmtDd.Set("selected", (uint)Math.Max(0, formats.FindIndex(f => f.Arg == cur)));
        void OnFmt()
        {
            var i = Convert.ToInt64(row.FmtDd.Get("selected"));
            if (i < 0 || i >= formats.Count) return;
            item.Extra["format_label"] = formats[(int)i].Label;
            item.Extra["format_arg"] = formats[(int)i].Arg;
            item.Extra["audio_only"] = formats[(int)i].AudioOnly;
        }
        row.FmtHandler = row.FmtDd.OnNotify("selected", OnFmt);
        row.SubHandler = row.SubDd.OnNotify("selected", () =>
        {
            var i = Convert.ToInt64(row.SubDd.Get("selected"));
            if (i >= 0 && i < subs.Count) item.Extra["sub_lang"] = subs[(int)i].Replace(" (авто)", "");
        });
        OnFmt();
        row.SubDd.Sensitive = subs.Count > 1;
    }

    // --- добавление элементов ---
    public void AddFilesDialog()
    {
        if (Script.InputMode != "files") return;
        var patterns = (Script.Extensions ?? new HashSet<string>()).OrderBy(e => e, StringComparer.Ordinal).Select(e => "*" + e).ToList();
        G.PickFiles(Window.Win, "Добавить файлы", AddPaths, start: Settings.Instance.GetStr("last_dir"), patterns: patterns.Count > 0 ? patterns : null);
    }

    void AddFolderDialog() => G.PickFolder(Window.Win, "Добавить папку", p => AddPaths([p]), Settings.Instance.GetStr("last_dir"), multiple: true);

    static List<string> CollectFiles(IEnumerable<string> paths, Func<string, bool> accept)
    {
        var res = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                try
                {
                    res.AddRange(Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .OrderBy(f => Path.GetDirectoryName(f), StringComparer.Ordinal).ThenBy(f => f, StringComparer.Ordinal).Where(accept));
                }
                catch { }
            }
            else if (File.Exists(p) && accept(p)) res.Add(p);
        }
        return res;
    }

    public void AddPaths(List<string> paths)
    {
        if (Running) { Toast("Нельзя менять список во время обработки"); return; }
        if (paths.Count == 0) return;
        Settings.Instance.Set("last_dir", File.Exists(paths[0]) ? Path.GetDirectoryName(paths[0]) : paths[0]);
        var all = CollectFiles(paths, _ => true);
        var accepted = all.Where(Script.Accepts).ToList();
        int rejected = all.Count - accepted.Count;
        if (Settings.Instance.GetBool("clear_list_on_add") && accepted.Count > 0) Clear();
        var known = Items.Select(i => i.Path).ToHashSet();
        var newItems = accepted.Select(Path.GetFullPath).Where(p => known.Add(p)).Select(p => new QueueItem(p)).ToList();
        foreach (var it in newItems) AppendItem(it);
        if (rejected > 0) Toast($"Пропущено неподходящих файлов: {rejected}");
        if (newItems.Count > 0) AfterAdd(newItems);
        UpdateSummary();
    }

    void AddUrlsFromEntry()
    {
        var text = _urlEntry.GetStr("text").Trim();
        if (text == "") return;
        AddUrls(text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).ToList());
        _urlEntry.Set("text", "");
    }

    public void AddUrls(List<string> urls)
    {
        urls = urls.Select(u => u.Trim()).Where(u => u.StartsWith("http://") || u.StartsWith("https://")).Distinct().ToList();
        var known = Items.Select(i => i.Path).ToHashSet();
        var fresh = urls.Where(u => !known.Contains(u)).Select(u => new QueueItem(u)).ToList();
        foreach (var it in fresh) AppendItem(it);
        if (fresh.Count > 0) AfterAdd(fresh);
        else Toast(urls.Count > 0 ? "Ссылки уже в списке" : "Не найдено ссылок http(s)");
        UpdateSummary();
    }

    void AppendItem(QueueItem item)
    {
        Items.Add(item);
        var row = new ItemRow(this, item);
        _rows[item.Id] = row;
        _listbox.Add(row.Row);
        _listStack.Set("visible-child-name", "list");
    }

    void AfterAdd(List<QueueItem> items)
    {
        if (Script.TrackSelection != null || Script.Id is "video-encode" or "bitrate-viewer" or "metadata-cleanup" or "container-convert") ProbeItems(items);
        if (Script.Id == "media-downloader") foreach (var it in items) FetchUrl(it);
        if (Script.Id == "mkv-assembly")
        {
            var audio = items.Where(i => Exts.AudioAll.Contains(i.Ext)).ToList();
            var vals = Form.Values;
            if (audio.Count > 0 && vals.Bool("audio_title_prompt"))
                Dialogs.AskAudioTitle(Window.Win, audio.Count, vals.Words("audio_title_presets"), vals.Str("audio_title_default"), t =>
                {
                    if (!string.IsNullOrEmpty(t)) foreach (var a in audio) a.Extra["mux_title"] = t;
                    RefreshRows();
                });
            RefreshRows();
        }
    }

    void ProbeItems(List<QueueItem> items)
    {
        foreach (var it in items) { it.Extra["loading"] = true; RefreshRow(it); }
        new Thread(() =>
        {
            foreach (var it in items)
            {
                try
                {
                    it.Info = Probe.Run(it.Path);
                    if (Script.TrackSelection != null && !it.Extra.ContainsKey("selected")) it.Selected = TrackSel.DefaultSelection(Script, it.Info);
                    it.Extra.Remove("error");
                }
                catch (Exception ex) { it.Extra["error"] = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message; }
                it.Extra.Remove("loading");
                MainLoop.Post(() => RefreshRow(it));
            }
        }) { IsBackground = true }.Start();
    }

    void FetchUrl(QueueItem item)
    {
        item.Extra["loading"] = true;
        RefreshRow(item);
        MainLoop.RunBg(() => MediaDownloader.FetchInfo(item.Path), (meta, err) =>
        {
            item.Extra.Remove("loading");
            if (err != null)
            {
                var m = $"не удалось получить данные: {err.Message}";
                item.Extra["error"] = m.Length > 200 ? m[..200] : m;
            }
            else { item.Extra["meta"] = meta; item.Extra.Remove("error"); }
            if (_rows.TryGetValue(item.Id, out var row)) FillDownloader(row);
            RefreshRow(item);
        });
    }

    // --- управление списком ---
    public void RemoveItem(QueueItem item)
    {
        if (Running) return;
        if (_rows.Remove(item.Id, out var row)) _listbox.Remove(row.Row);
        Items.Remove(item);
        if (Items.Count == 0) _listStack.Set("visible-child-name", "empty");
        RefreshRows();
        UpdateSummary();
    }

    void Clear()
    {
        if (Running) return;
        foreach (var row in _rows.Values) _listbox.Remove(row.Row);
        _rows.Clear();
        Items.Clear();
        _results.Clear();
        _listStack.Set("visible-child-name", "empty");
        _totalBar.Set("fraction", 0.0);
        UpdateSummary();
    }

    void ClearDone()
    {
        foreach (var it in Items.Where(i => i.Status is "done" or "skipped").ToList()) RemoveItem(it);
    }

    void ResetStatuses()
    {
        if (Running) return;
        foreach (var it in Items) it.Reset();
        RefreshRows();
        _totalBar.Set("fraction", 0.0);
        UpdateSummary();
    }

    void RefreshRow(QueueItem item) { if (_rows.TryGetValue(item.Id, out var r)) r.Refresh(); }
    public void RefreshRows() { foreach (var r in _rows.Values) r.Refresh(); }

    void UpdateSummary()
    {
        if (Running || Script.InputMode == "none") return;
        int n = Items.Count;
        if (n == 0) _statusLabel.Set("label", "Очередь пуста");
        else
        {
            int done = Items.Count(i => i.Status is "done" or "partial"), failed = Items.Count(i => i.Status == "failed");
            _statusLabel.Set("label", $"В очереди: {n}" + (done > 0 || failed > 0 ? $" · готово {done}, ошибок {failed}" : ""));
        }
        _startBtn.Sensitive = n > 0;
    }

    // --- папка результатов ---
    public void SetOutDir(string dir) { _outDir = dir; RefreshOutLabel(); }

    void ChooseOutDir()
    {
        var start = _outDir ?? (Items.Count > 0 && Script.InputMode == "files" ? Path.GetDirectoryName(Items[0].Path) : Settings.Instance.GetStr("last_dir"));
        G.PickFolder(Window.Win, "Папка для результатов", p => { _outDir = p; RefreshOutLabel(); }, start);
    }

    public void RefreshOutLabel()
    {
        if (_outContent == null) return;
        string label, tip;
        if (_outDir != null) { label = Path.GetFileName(_outDir.TrimEnd('/')) is { Length: > 0 } n ? n : _outDir; tip = _outDir; }
        else if (Script.InputMode == "urls") { label = "Загрузки"; tip = "По умолчанию — папка «Загрузки»"; }
        else if (Settings.Instance.GetBool("use_auto_subfolder")) { label = $"Подпапка «{Settings.Instance.GetStr("output_subfolder")}»"; tip = "Результаты сохраняются в подпапку рядом с исходником"; }
        else { label = "Рядом с исходником"; tip = "Результаты сохраняются рядом с исходным файлом"; }
        _outContent.Set("label", label);
        _outBtn.Tooltip = tip;
        _outReset.Visible = _outDir != null;
    }

    // --- запуск ---
    public void ToggleRun()
    {
        if (Script.InputMode == "none") return;
        if (Running)
        {
            Runner.Cancel();
            _statusLabel.Set("label", "Отмена…");
            return;
        }
        Start();
    }

    void Start()
    {
        if (Items.Count == 0) { Toast("Добавьте файлы в очередь"); return; }
        var missing = Script.MissingDeps();
        if (missing.Count > 0)
        {
            var names = string.Join(", ", missing.Where(Tools.All.ContainsKey).Select(m => Tools.All[m].Title));
            G.Confirm(Window.Win, "Не хватает утилит", $"Для «{Script.Name}» нужно: {names}.", () => Window.ShowPage("deps"), "Открыть «Зависимости»");
            return;
        }
        if (Items.Any(i => Json.ToBool(i.Extra.GetValueOrDefault("loading")))) { Toast("Дождитесь окончания анализа файлов"); return; }
        if (!Items.Any(i => i.Status == "pending"))
        {
            foreach (var it in Items) it.Reset();
            RefreshRows();
        }
        _results.Clear();
        Runner = new BatchRunner(Script, Items.ToList(), new Dictionary<string, object>(Form.Values), _outDir)
        {
            OnItem = it => MainLoop.Post(() => RefreshRow(it)),
            OnOverall = (pct, msg) => MainLoop.Post(() => OnOverall(pct, msg)),
            OnResult = (it, res) => MainLoop.Post(() => OnResult(it, res)),
            OnFinish = s => MainLoop.Post(() => OnFinish(s)),
            Ask = AskFromWorker,
        };
        SetRunningUi(true);
        Runner.Start();
    }

    void SetRunningUi(bool running)
    {
        _startContent.Set("icon-name", running ? "media-playback-stop-symbolic" : "media-playback-start-symbolic");
        _startContent.Set("label", running ? "Стоп" : "Запуск");
        _startBtn.ToggleCss("suggested-action", !running);
        _startBtn.ToggleCss("destructive-action", running);
        if (running) _totalBar.Set("fraction", 0.0);
        Form.Root.Sensitive = !running;
        Window.SetPageBusy(Script.Id, running);
    }

    void OnOverall(double pct, string msg)
    {
        if (!Running) return;
        _totalBar.Set("fraction", pct / 100);
        _statusLabel.Set("label", $"{pct:0}% · {msg}");
    }

    void OnResult(QueueItem item, Result res)
    {
        _results[item.Id] = res;
        RefreshRow(item);
        if (Script.Id == "bitrate-viewer" && res.Data != null)
        {
            if (_rows.TryGetValue(item.Id, out var row)) row.ChartBtn.Visible = true;
            if (Form.Values.Bool("open_chart", true)) OpenChart(item);
        }
    }

    void OnFinish(BatchSummary s)
    {
        Runner = null;
        SetRunningUi(false);
        if (!s.Cancelled) _totalBar.Set("fraction", 1.0);
        UpdateSummary();
        var text = $"Готово: {s.Ok}" + (s.Failed > 0 ? $", ошибок {s.Failed}" : "") + (s.Skipped > 0 ? $", пропущено {s.Skipped}" : "") + (s.Cancelled ? " — отменено" : "");
        Toast(text);
        if (Settings.Instance.GetBool("notify_on_finish") && !s.Cancelled && !(Settings.Instance.GetBool("notify_only_unfocused") && Window.IsActive))
            Desktop.Notify($"{AppInfo.Name}: {Script.Name}", text, s.Failed > 0 ? "critical" : "normal");
    }

    /// <summary>Синхронный вопрос пользователю из рабочего потока.</summary>
    object AskFromWorker(string kind, object[] args)
    {
        var ev = new ManualResetEventSlim();
        object result = null;
        MainLoop.Post(() =>
        {
            if (kind == "choose_subtitle") Dialogs.ChooseSubtitle(Window.Win, (string)args[0], (List<Track>)args[1], t => { result = t; ev.Set(); });
            else ev.Set();
        });
        while (!ev.Wait(200))
            if (Runner == null || Runner.Token.Cancelled) return null;
        return result;
    }

    // --- окна инструментов ---
    public void OpenTracks(QueueItem item)
    {
        if (item.Info == null) { Toast("Файл ещё анализируется"); return; }
        Dialogs.Tracks(Window.Win, Script, item, Items, RefreshRows);
    }

    void OpenChart(QueueItem item)
    {
        if (_results.GetValueOrDefault(item.Id)?.Data is BitrateData data) BitrateWindow.Show(Window.Win, data);
    }

    void OpenWaveform()
    {
        if (Items.Count == 0) { Toast("Сначала добавьте видео, в которое пересаживается аудио"); return; }
        WaveformSync.Show(this);
    }

    void OnSettingChanged(string key, object value)
    {
        if (Script.Id == "mkv-assembly") RefreshRows();
    }

    public void Toast(string text) => G.Toast(_toastOverlay, text);
}
