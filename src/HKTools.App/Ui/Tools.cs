using System.Globalization;
using HKTools.Core;
using HKTools.Media;
using HKTools.Scripts;
using HKTools.Ui.Native;

namespace HKTools.Ui;

/// <summary>Предпросмотр конвертации субтитров: фильтр по актёрам/стилям и ручное включение/исключение реплик.</summary>
internal static class SubtitlePreview
{
    static readonly Dictionary<string, string> Reason = new()
    {
        ["actor"] = "актёр", ["style"] = "стиль", ["effect"] = "эффект", ["regex"] = "regex", ["caps-empty"] = "пусто после очистки",
        ["manual-ex"] = "исключено вручную", ["manual-in"] = "включено вручную", ["empty"] = "пустая",
    };

    public static void Show(WorkPanel panel, QueueItem item)
    {
        SubDoc doc;
        try { doc = Subs.Load(item.Path); }
        catch (Exception ex) { doc = new SubDoc(); panel.Toast($"Не удалось прочитать: {ex.Message}"); }
        var manualIn = SubFilters.IntSet(item.Extra.GetValueOrDefault("manual_in"));
        var manualEx = SubFilters.IntSet(item.Extra.GetValueOrDefault("manual_ex"));
        var exActors = panel.Form.Values.Words("exclude_actors").ToHashSet();
        var exStyles = panel.Form.Values.Words("exclude_styles").ToHashSet();

        var d = new W("AdwDialog", ("title", $"Предпросмотр — {item.Name}"), ("content-width", 1000), ("content-height", 700));
        var tv = new W("AdwToolbarView");
        var header = new W("AdwHeaderBar");
        var stats = G.Label("").Css("dim-label");
        header.Start(stats);
        tv.Top(header);
        var search = new W("GtkSearchEntry", ("placeholder-text", "Поиск по тексту"), ("margin-start", 8), ("margin-end", 8), ("margin-top", 8));
        var listbox = new W("GtkListBox", ("selection-mode", 0)).Css("boxed-list");
        var rows = new List<(W Row, int Index, bool Deleted, string Text)>();

        Dictionary<string, object> Values()
        {
            var v = new Dictionary<string, object>(panel.Form.Values)
            {
                ["exclude_actors"] = KwItem.ToStore(exActors.Select(n => new KwItem { Word = n })),
                ["exclude_styles"] = KwItem.ToStore(exStyles.Select(n => new KwItem { Word = n })),
            };
            return v;
        }

        void SaveManual()
        {
            item.Extra["manual_in"] = manualIn.OrderBy(x => x).ToList();
            item.Extra["manual_ex"] = manualEx.OrderBy(x => x).ToList();
            panel.RefreshRows();
        }

        void ApplySearch()
        {
            var q = search.GetStr("text").Trim().ToLowerInvariant();
            foreach (var r in rows) r.Row.Visible = q == "" || r.Text.Contains(q);
        }

        void Render()
        {
            listbox.Clear();
            rows.Clear();
            var states = SubFilters.Apply(doc, Values(), manualIn, manualEx);
            int kept = 0;
            foreach (var st in states)
            {
                if (st.Reason == "empty") continue;
                var row = new W("GtkListBoxRow", ("activatable", true));
                var box = G.Box(spacing: 10, props: [("margin-top", 6), ("margin-bottom", 6), ("margin-start", 10), ("margin-end", 10)]);
                box.Add(G.Label(Subs.FmtSrt(st.Dialogue.Start, '.')[..^1], ("xalign", 0f)).Css("numeric", "dim-label"));
                box.Add(G.Label(string.Join(" · ", new[] { st.Dialogue.Style, st.Dialogue.Actor }.Where(x => x != "")), ("xalign", 0f),
                    ("width-chars", 16), ("max-width-chars", 16), ("ellipsize", Ellipsize.End)).Css("caption"));
                var text = Subs.StripTags(st.Deleted ? st.Dialogue.Text : st.Text).Replace("\n", " ⏎ ");
                var esc = System.Security.SecurityElement.Escape(text);
                var lbl = G.Label("", ("xalign", 0f), ("hexpand", true), ("wrap", true), ("wrap-mode", 2));
                lbl.Set("use-markup", true);
                lbl.Set("label", st.Deleted ? $"<s>{esc}</s>" : esc);
                box.Add(lbl);
                if (st.Reason != "") box.Add(G.Label(Reason.GetValueOrDefault(st.Reason, st.Reason)).Css("kt-pill", st.Deleted ? "kt-cat-network" : "kt-cat-audio"));
                if (st.Deleted) row.Css("kt-sub-deleted"); else kept++;
                row.Set("child", box);
                listbox.Add(row);
                rows.Add((row, st.Index, st.Deleted, text.ToLowerInvariant()));
            }
            stats.Set("label", $"Останется {kept} из {states.Count(s => s.Reason != "empty")}");
            ApplySearch();
        }

        listbox.OnP("row-activated", rowPtr =>
        {
            var r = rows.FirstOrDefault(x => x.Row.H == rowPtr);
            if (r.Row == null) return;
            if (r.Deleted) { manualEx.Remove(r.Index); manualIn.Add(r.Index); }
            else { manualIn.Remove(r.Index); manualEx.Add(r.Index); }
            SaveManual();
            MainLoop.Post(Render);
        });
        search.On("search-changed", ApplySearch);
        header.End(G.Button("Сбросить ручные правки", onClick: () => { manualIn.Clear(); manualEx.Clear(); SaveManual(); Render(); }, css: "flat"));

        var side = new W("AdwPreferencesPage");
        void FilterGroup(string title, List<string> names, HashSet<string> excluded, string key)
        {
            if (names.Count == 0) return;
            var group = new W("AdwPreferencesGroup", ("title", $"{title} ({names.Count})"));
            var counts = doc.Dialogues.GroupBy(x => key == "exclude_actors" ? x.Actor : x.Style).ToDictionary(g => g.Key, g => g.Count());
            foreach (var name in names)
            {
                var n = name;
                var row = new W("AdwSwitchRow", ("title", n != "" ? n : "(без имени)"), ("active", !excluded.Contains(n)),
                    ("subtitle", $"реплик: {counts.GetValueOrDefault(n)}"), ("use-markup", false));
                row.OnNotify("active", () =>
                {
                    if (row.GetBool("active")) excluded.Remove(n); else excluded.Add(n);
                    var items = KwItem.ToStore(excluded.OrderBy(x => x, StringComparer.Ordinal).Select(x => new KwItem { Word = x }));
                    panel.Form.SetValue(key, items);
                    Render();
                });
                group.Add(row);
            }
            side.Add(group);
        }
        FilterGroup("Актёры", doc.Actors, exActors, "exclude_actors");
        FilterGroup("Стили", doc.StyleNames, exStyles, "exclude_styles");
        side.Add(new W("AdwPreferencesGroup", ("description", "Снимите флажок, чтобы исключить все реплики актёра или стиля. Щелчок по реплике справа переключает её вручную.")));
        var right = G.VBox(6);
        right.Add(search, new W("GtkScrolledWindow", ("vexpand", true), ("child", new W("AdwClamp", ("maximum-size", 900), ("child", listbox),
            ("margin-top", 6), ("margin-bottom", 12), ("margin-start", 8), ("margin-end", 8)))));
        var paned = new W("GtkPaned", ("orientation", Orient.H), ("position", 300), ("shrink-start-child", false), ("start-child", side), ("end-child", right));
        tv.Set("content", paned);
        d.Set("child", tv);
        Render();
        G.adw_dialog_present(d.H, panel.Window.Win.H);
    }
}

/// <summary>Окно интерактивного графика битрейта (cairo, масштаб колёсиком, подсказка под курсором).</summary>
internal static class BitrateWindow
{
    public static void Show(W parent, BitrateData data)
    {
        int n = Math.Max(1, data.PerSecond.Count);
        double t0 = 0, t1 = n;
        double? hover = null;
        (double, double)? dragOrigin = null;
        var win = new W("AdwWindow", ("title", $"Битрейт — {Path.GetFileName(data.Path)}"), ("default-width", 1180), ("default-height", 620),
            ("transient-for", parent));
        var tv = new W("AdwToolbarView");
        var header = G.Header(Path.GetFileName(data.Path), data.StreamLabel);
        W area = null;
        header.End(G.Button(icon: "document-save-symbolic", tooltip: "Сохранить PNG", onClick: () =>
            G.SaveFile(win, "Сохранить график", Path.GetFileNameWithoutExtension(data.Path) + "_bitrate.png", p =>
            {
                try { BitrateChart.PngRenderer(data, p, 1920, 720, G.Dark); }
                catch (Exception ex) { G.Info(win, "PNG не сохранён", ex.Message); }
            })));
        header.End(G.Button(icon: "zoom-original-symbolic", tooltip: "Весь файл", onClick: () => { t0 = 0; t1 = n; area.Redraw(); }));
        tv.Top(header);
        var box = G.VBox();
        var stats = G.Box(spacing: 18, props: [("margin-start", 16), ("margin-end", 16), ("margin-top", 10), ("margin-bottom", 6), ("halign", Align.Center)]);
        string F2(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        foreach (var (label, value) in new[]
                 {
                     ("Среднее", $"{F2(data.Mean)} Мбит/с"), ("Пик", $"{F2(data.Peak)} Мбит/с"), ("Минимум", $"{F2(data.Minimum)} Мбит/с"), ("σ", F2(data.Stdev)),
                     ("FPS", data.Fps > 0 ? data.Fps.ToString("0.###", CultureInfo.InvariantCulture) : "—"), ("Ключевых кадров", data.Keyframes.Count.ToString()),
                     ("GOP", data.Gop > 0 ? $"{F2(data.Gop)} с" : "—"), ("Размер потока", Pages.HumanSize(data.TotalBytes)),
                 })
        {
            var col = G.VBox();
            col.Add(G.Label(label).Css("caption", "dim-label"), G.Label(value).Css("heading"));
            stats.Add(col);
        }
        box.Add(stats);
        area = G.DrawingArea((cr, w, h) =>
        {
            cr.Font();
            BitrateChart.Draw(cr, w, h, data, G.Dark, view: (t0, t1), hoverX: hover);
        }, ("vexpand", true), ("hexpand", true));
        double PlotW() => Math.Max(10, area.Width - 64 - 20);
        G.OnMotion(area, (x, _) => { hover = x; area.Redraw(); }, () => { hover = null; area.Redraw(); });
        G.OnScroll(area, (_, dy, _) =>
        {
            double span = t1 - t0;
            double center = hover is { } hx ? t0 + (hx - 64) / PlotW() * span : t0 + span / 2;
            double ns = Math.Min(n, Math.Max(10, span * (dy > 0 ? 1.25 : 0.8)));
            double rel = span > 0 ? (center - t0) / span : 0.5;
            t0 = Math.Max(0, Math.Min(n - ns, center - rel * ns));
            t1 = t0 + ns;
            area.Redraw();
            return true;
        });
        G.OnDrag(area, (_, _) => dragOrigin = (t0, t1), (dx, _) =>
        {
            if (dragOrigin is not { } o) return;
            double span = o.Item2 - o.Item1;
            t0 = Math.Max(0, Math.Min(n - span, o.Item1 - dx / PlotW() * span));
            t1 = t0 + span;
            area.Redraw();
        }, (_, _) => dragOrigin = null);
        box.Add(area);
        box.Add(G.Label("Колёсико — масштаб, перетаскивание — прокрутка", ("margin-bottom", 6)).Css("caption", "dim-label"));
        tv.Set("content", box);
        win.Set("content", tv);
        G.gtk_window_present(win.H);
    }
}

/// <summary>Окно синхронизации «Пересадки аудио» по осциллограммам.</summary>
internal sealed class WaveformSync
{
    static readonly (string Label, double? Limit)[] Limits = [("Первые 5 мин", 300), ("Первые 10 мин", 600), ("Первые 30 мин", 1800), ("Весь файл", null)];
    const double Ruler = 26;

    readonly WorkPanel _panel;
    readonly W _win, _area, _refDd, _refTrackDd, _donTrackDd, _donLabel, _limitDd, _progress, _hint, _spin;
    readonly List<QueueItem> _refItems;
    List<Track> _refTracks = new(), _donTracks = new();
    Envelope _ref, _don;
    int _shift;
    double _t0, _span = 20_000;
    double? _hover;
    (bool Shift, double T0, double ShiftStart)? _drag;
    CancelToken _token = new();
    string _donPath;
    nuint _spinHandler;

    public static WaveformSync Last;
    public static void Show(WorkPanel panel) => Last = new WaveformSync(panel);
    public void Zoom(double spanMs) { _span = spanMs; _area.Redraw(); }

    WaveformSync(WorkPanel panel)
    {
        _panel = panel;
        var vals = panel.Form.Values;
        _shift = vals.Int("transplant_shift_ms");
        _donPath = vals.Str("transplant_source");
        _win = new W("AdwWindow", ("title", "Синхронизация по осциллограмме"), ("default-width", 1200), ("default-height", 640), ("transient-for", panel.Window.Win));
        var tv = new W("AdwToolbarView");
        var header = new W("AdwHeaderBar");
        header.End(G.Button("Применить сдвиг", onClick: Apply, css: "suggested-action"));
        tv.Top(header);

        var top = new W("GtkGrid", ("column-spacing", 8), ("row-spacing", 6), ("margin-start", 12), ("margin-end", 12), ("margin-top", 8), ("margin-bottom", 4));
        _refItems = panel.Items.Where(i => Exts.VideoContainers.Contains(i.Ext)).ToList();
        _refDd = new W("GtkDropDown", ("model", G.StringList(_refItems.Count > 0 ? _refItems.Select(i => i.Name) : ["(очередь пуста)"])), ("hexpand", true));
        _refTrackDd = new W("GtkDropDown", ("model", G.StringList(["—"])));
        var donBtn = new W("GtkButton", ("hexpand", true));
        _donLabel = G.Label(_donPath != "" ? Path.GetFileName(_donPath) : "Выбрать файл…", ("ellipsize", Ellipsize.Middle));
        donBtn.Set("child", _donLabel);
        donBtn.On("clicked", PickDonor);
        _donTrackDd = new W("GtkDropDown", ("model", G.StringList(["—"])));
        _limitDd = new W("GtkDropDown", ("model", G.StringList(Limits.Select(l => l.Label))), ("selected", 1u));
        var load = G.Button("Построить осциллограммы", onClick: Load, css: "pill");
        Grid.Attach(top, G.Label("Эталон (видео):", ("xalign", 0f)), 0, 0);
        Grid.Attach(top, _refDd, 1, 0);
        Grid.Attach(top, _refTrackDd, 2, 0);
        Grid.Attach(top, _limitDd, 3, 0);
        Grid.Attach(top, G.Label("Донор аудио:", ("xalign", 0f)), 0, 1);
        Grid.Attach(top, donBtn, 1, 1);
        Grid.Attach(top, _donTrackDd, 2, 1);
        Grid.Attach(top, load, 3, 1);
        _refDd.OnNotify("selected", ProbeRef);

        _area = G.DrawingArea(Draw, ("vexpand", true), ("hexpand", true), ("focusable", true));
        G.OnMotion(_area, (x, _) => { _hover = x; _area.Redraw(); }, () => { _hover = null; _area.Redraw(); });
        G.OnScroll(_area, OnScroll);
        G.OnDrag(_area, (_, y) =>
        {
            double lane = (_area.Height - Ruler) / 2;
            _drag = (y > Ruler + lane, _t0, _shift);
            _area.Focus();
        }, (dx, _) =>
        {
            if (_drag is not { } d) return;
            double ms = dx * _span / Math.Max(1, _area.Width);
            if (!d.Shift) { _t0 = Math.Max(-_span / 2, d.T0 - ms); _area.Redraw(); }
            else SetShift((int)Math.Round(d.ShiftStart + ms));
        }, (_, _) => _drag = null);
        G.OnKey(_area, (kv, state) =>
        {
            int step = (state & 1) != 0 ? 10 : 1; // GDK_SHIFT_MASK
            if (kv is 0xff51 or 0xff96) { SetShift(_shift - step); return true; } // Left / KP_Left
            if (kv is 0xff53 or 0xff98) { SetShift(_shift + step); return true; } // Right / KP_Right
            return false;
        });

        var bottom = G.Box(spacing: 6, props: [("margin-start", 12), ("margin-end", 12), ("margin-top", 6), ("margin-bottom", 10)]);
        _hint = G.Label("Тяните нижнюю дорожку мышью · колёсико — масштаб · Shift+колёсико — прокрутка · ←/→ ±1 мс, Shift ±10 мс",
            ("hexpand", true), ("xalign", 0f), ("ellipsize", Ellipsize.End)).Css("dim-label", "caption");
        _progress = new W("GtkProgressBar", ("hexpand", true), ("visible", false), ("valign", Align.Center), ("show-text", true));
        bottom.Add(_hint, _progress);
        foreach (var step in new[] { -100, -10, -1 }) bottom.Add(G.Button($"{step:+0;-0}", onClick: () => SetShift(_shift + step), css: "flat"));
        _spin = Native.Spin.New(-3_600_000, 3_600_000, 1);
        _spin.Set("value", (double)_shift);
        _spin.Set("width-chars", 9);
        _spinHandler = _spin.On("value-changed", () => SetShift((int)_spin.GetDouble("value"), fromSpin: true));
        bottom.Add(_spin, G.Label("мс"));
        foreach (var step in new[] { 1, 10, 100 }) bottom.Add(G.Button($"{step:+0;-0}", onClick: () => SetShift(_shift + step), css: "flat"));
        var auto = G.Button("Авто", tooltip: "Подобрать сдвиг кросс-корреляцией огибающих", onClick: Auto);
        bottom.Add(auto);

        var box = G.VBox();
        box.Add(top, _area, bottom);
        tv.Set("content", box);
        _win.Set("content", tv);
        _win.OnB("close-request", () => { _token.Cancel(); return false; });
        ProbeRef();
        if (_donPath != "") ProbeDonor();
        G.gtk_window_present(_win.H);
    }

    static string TrackLabel(Track t)
    {
        var s = $"#{t.Index} {t.Describe()}";
        return s.Length > 60 ? s[..60] : s;
    }

    void ProbeRef()
    {
        var i = Convert.ToInt32(_refDd.Get("selected"));
        if (i < 0 || i >= _refItems.Count) return;
        var path = _refItems[i].Path;
        MainLoop.RunBg(() => Probe.Run(path), (info, err) =>
        {
            if (err != null || info == null) return;
            _refTracks = info.Audio;
            _refTrackDd.Set("model", G.StringList(info.Audio.Count > 0 ? info.Audio.Select(TrackLabel) : ["нет аудио"]));
        });
    }

    void PickDonor()
    {
        var start = _donPath != "" ? Path.GetDirectoryName(_donPath) : null;
        G.PickFiles(_win, "Файл-донор аудио", ps =>
        {
            _donPath = ps[0];
            _donLabel.Set("label", Path.GetFileName(_donPath));
            _don = null;
            ProbeDonor();
            _area.Redraw();
        }, multiple: false, start: start, patterns: Exts.Union(Exts.AudioAll, Exts.VideoContainers).OrderBy(e => e, StringComparer.Ordinal).Select(e => "*" + e).ToList());
    }

    void ProbeDonor()
    {
        var path = _donPath;
        MainLoop.RunBg(() => Probe.Run(path), (info, err) =>
        {
            if (err != null || info == null) { _hint.Set("label", $"Не удалось прочитать донора: {err?.Message}"); return; }
            _donTracks = info.Audio;
            _donTrackDd.Set("model", G.StringList(info.Audio.Count > 0 ? info.Audio.Select(TrackLabel) : ["нет аудио"]));
            int sel = _panel.Form.Values.Int("transplant_source_track");
            if (sel >= 0 && sel < info.Audio.Count) _donTrackDd.Set("selected", (uint)sel);
        });
    }

    public void Load()
    {
        int ri = Convert.ToInt32(_refTrackDd.Get("selected")), di = Convert.ToInt32(_donTrackDd.Get("selected")), fi = Convert.ToInt32(_refDd.Get("selected"));
        if (fi < 0 || fi >= _refItems.Count || ri < 0 || ri >= _refTracks.Count || di < 0 || di >= _donTracks.Count)
        {
            _hint.Set("label", "Выберите эталонное видео и файл-донор с аудиодорожками");
            return;
        }
        string refPath = _refItems[fi].Path, donPath = _donPath;
        int refIdx = _refTracks[ri].Index, donIdx = _donTracks[di].Index;
        double? limit = Limits[Math.Clamp(Convert.ToInt32(_limitDd.Get("selected")), 0, Limits.Length - 1)].Limit;
        _token.Cancel();
        var token = _token = new CancelToken();
        _progress.Visible = true;
        _progress.Set("fraction", 0.0);
        _progress.Set("text", "Декодирование эталона…");
        long last = 0;
        void Prog(int part, double pct)
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref last) < 100) return;
            Interlocked.Exchange(ref last, now);
            MainLoop.Post(() =>
            {
                _progress.Set("fraction", (part * 100 + pct) / 200);
                _progress.Set("text", (part == 0 ? "Эталон" : "Донор") + $" · {pct:0}%");
            });
        }
        MainLoop.RunBg(() => (Waveform.Compute(refPath, refIdx, limit, token, p => Prog(0, p)), Waveform.Compute(donPath, donIdx, limit, token, p => Prog(1, p))),
            (res, err) =>
            {
                _progress.Visible = false;
                if (err != null)
                {
                    if (token == _token) _hint.Set("label", $"Ошибка: {err.Message}");
                    return;
                }
                (_ref, _don) = res;
                _t0 = 0;
                _span = Math.Min(30_000, Math.Max(_ref.LengthMs, 1000));
                _area.Redraw();
                _area.Focus();
            });
    }

    void Auto()
    {
        if (_ref == null || _don == null) { _hint.Set("label", "Сначала постройте осциллограммы"); return; }
        _progress.Visible = true;
        _progress.Set("text", "Поиск сдвига…");
        G.ProgressPulse(_progress);
        var (r, d) = (_ref, _don);
        MainLoop.RunBg(() => Waveform.AutoOffset(r, d), (lag, err) =>
        {
            _progress.Visible = false;
            if (err != null) { _hint.Set("label", $"Авто-синхронизация не удалась: {err.Message}"); return; }
            SetShift(lag);
            _panel.Toast($"Найден сдвиг: {lag:+0;-0;+0} мс");
        });
    }

    public void SetShift(int value, bool fromSpin = false)
    {
        _shift = value;
        if (!fromSpin && (int)_spin.GetDouble("value") != _shift)
        {
            _spin.Block(_spinHandler);
            _spin.Set("value", (double)_shift);
            _spin.Unblock(_spinHandler);
        }
        _area.Redraw();
    }

    void Apply()
    {
        var form = _panel.Form;
        form.SetValue("transplant_shift_ms", (long)_shift);
        if (_donPath != "") form.SetValue("transplant_source", _donPath);
        int di = Convert.ToInt32(_donTrackDd.Get("selected"));
        if (di >= 0 && di < _donTracks.Count) form.SetValue("transplant_source_track", (long)di);
        _panel.Toast($"Сдвиг {_shift:+0;-0;+0} мс применён");
        G.gtk_window_close(_win.H);
    }

    bool OnScroll(double dx, double dy, uint state)
    {
        double w = Math.Max(1, _area.Width);
        if ((state & 1) != 0 || Math.Abs(dx) > Math.Abs(dy))
        {
            double delta = (Math.Abs(dx) > Math.Abs(dy) ? dx : dy) * _span * 0.1;
            _t0 = Math.Max(-_span / 2, _t0 + delta);
        }
        else
        {
            double anchor = _t0 + (_hover ?? w / 2) / w * _span;
            double total = Math.Max(_ref?.LengthMs ?? 60_000, 1000);
            double ns = Math.Max(50, Math.Min(total * 1.2, _span * (dy > 0 ? 1.25 : 0.8)));
            double rel = (anchor - _t0) / _span;
            _t0 = Math.Max(-ns / 2, anchor - rel * ns);
            _span = ns;
        }
        _area.Redraw();
        return true;
    }

    void Draw(Cairo cr, int width, int height)
    {
        bool dark = G.Dark;
        (double R, double G, double B) bg = dark ? (0.11, 0.11, 0.13) : (0.97, 0.97, 0.98);
        (double R, double G, double B) fg = dark ? (0.9, 0.9, 0.92) : (0.15, 0.15, 0.18);
        void Fg(double a) => cr.Rgba(fg.R, fg.G, fg.B, a);
        cr.Rgba(bg.R, bg.G, bg.B);
        cr.Paint();
        double w = width, h = height, lane = (h - Ruler) / 2;
        double msPerPx = _span / Math.Max(1, w);
        cr.Font();
        cr.FontSize(11);
        cr.LineWidth(1);
        double step = BitrateChart.NiceStep(_span / 1000, 10) * 1000;
        for (double t = Math.Floor(_t0 / step) * step; t <= _t0 + _span; t += step)
        {
            double x = (t - _t0) / msPerPx;
            Fg(0.25); cr.MoveTo(x, Ruler); cr.LineTo(x, h); cr.Stroke();
            Fg(0.8);
            double s = Math.Max(0, t) / 1000;
            var label = step < 1000 ? $"{(int)(s / 60)}:{(s % 60).ToString("00.000", CultureInfo.InvariantCulture)}" : $"{(int)(s / 60)}:{(int)(s % 60):00}";
            cr.MoveTo(x + 3, 16);
            cr.ShowText(label);
        }
        if (_ref == null || _don == null)
        {
            Fg(0.6);
            cr.FontSize(15);
            const string msg = "Выберите файлы и нажмите «Построить осциллограммы»";
            cr.MoveTo((w - cr.TextWidth(msg)) / 2, h / 2);
            cr.ShowText(msg);
            return;
        }
        double scale = (lane / 2 - 6) / Math.Max(1, Math.Max(_ref.Peak, _don.Peak));
        foreach (var (env, top, color, offset, title) in new[]
                 {
                     (_ref, Ruler, (0.11, 0.62, 0.89), 0, "Эталон"),
                     (_don, Ruler + lane, (0.16, 0.79, 0.78), _shift, $"Донор ({_shift:+0;-0;+0} мс)"),
                 })
        {
            double mid = top + lane / 2;
            cr.Rgba(color.Item1, color.Item2, color.Item3, 0.85);
            for (int px = 0; px < (int)w; px++)
            {
                double a = _t0 + px * msPerPx - offset;
                double v = env.WindowMax(a, a + msPerPx) * scale;
                if (v > 0.5) { cr.MoveTo(px + 0.5, mid - v); cr.LineTo(px + 0.5, mid + v); }
            }
            cr.LineWidth(1);
            cr.Stroke();
            Fg(0.15); cr.MoveTo(0, mid); cr.LineTo(w, mid); cr.Stroke();
            Fg(0.85);
            cr.FontSize(12);
            cr.MoveTo(8, top + 16);
            cr.ShowText(title);
        }
        Fg(0.3); cr.MoveTo(0, Ruler + lane); cr.LineTo(w, Ruler + lane); cr.Stroke();
        if (_hover is { } hx)
        {
            cr.Rgba(1, 0.6, 0.2, 0.8);
            cr.MoveTo(hx, Ruler); cr.LineTo(hx, h); cr.Stroke();
            cr.MoveTo(hx + 4, Ruler + 14);
            cr.ShowText(((_t0 + hx * msPerPx) / 1000).ToString("0.000", CultureInfo.InvariantCulture) + " с");
        }
    }
}
