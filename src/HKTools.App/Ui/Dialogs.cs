using HKTools.Core;
using HKTools.Media;
using HKTools.Scripts;
using HKTools.Ui.Native;

namespace HKTools.Ui;

/// <summary>Диалоги: выбор дорожек, выбор субтитров для хардсаба, заголовок аудио, модели Whisper.</summary>
internal static class Dialogs
{
    static readonly Dictionary<string, string> TypeIcon = new()
    {
        ["video"] = "video-x-generic-symbolic", ["audio"] = "audio-x-generic-symbolic", ["subtitle"] = "font-x-generic-symbolic", ["attachment"] = "font-x-generic-symbolic",
    };

    static (W Dialog, W Toolbar, W Header) Dialog(string title, int w, int h, bool buttons = true)
    {
        var d = new W("AdwDialog", ("title", title), ("content-width", w), ("content-height", h));
        var tv = new W("AdwToolbarView");
        var header = new W("AdwHeaderBar");
        if (!buttons)
        {
            header.Set("show-end-title-buttons", false);
            header.Set("show-start-title-buttons", false);
        }
        tv.Top(header);
        d.Set("child", tv);
        return (d, tv, header);
    }

    /// <summary>Выбор дорожек (управление потоками / разборка) или замена дорожек (замена потоков).</summary>
    public static void Tracks(W parent, Script script, QueueItem item, List<QueueItem> all, Action onApply)
    {
        var mode = script.TrackSelection ?? "manage";
        var info = item.Info;
        var selected = item.Selected.ToHashSet();
        var reps = item.Replacements;
        var checks = new Dictionary<int, W>();
        var repLabels = new Dictionary<int, W>();
        var (d, tv, header) = Dialog(item.Name, 640, 620, buttons: false);

        void Apply(bool toSimilar)
        {
            if (mode == "replace") item.Replacements = new Dictionary<int, string>(reps);
            else
            {
                item.Selected = selected.OrderBy(x => x).ToList();
                if (toSimilar)
                {
                    var sig = info.Signature;
                    var media = info.MediaTracks;
                    var pos = media.Select((t, n) => (t.Index, n)).ToDictionary(x => x.Index, x => x.n);
                    var selPos = selected.Where(pos.ContainsKey).Select(i => pos[i]).ToHashSet();
                    bool fontSel = info.Fonts.Any(t => selected.Contains(t.Index));
                    foreach (var other in all)
                    {
                        if (other == item || other.Info == null || other.Info.Signature != sig) continue;
                        var om = other.Info.MediaTracks;
                        var chosen = selPos.Where(p => p < om.Count).Select(p => om[p].Index).ToList();
                        if (fontSel) chosen.AddRange(other.Info.Fonts.Select(t => t.Index));
                        other.Selected = chosen.OrderBy(x => x).ToList();
                    }
                }
            }
            onApply();
            G.adw_dialog_close(d.H);
        }

        header.Start(G.Button("Отмена", onClick: () => G.adw_dialog_close(d.H)));
        header.End(G.Button("Применить", onClick: () => Apply(false), css: "suggested-action"));
        var page = new W("AdwPreferencesPage");
        var hint = mode switch
        {
            "extract" => "Отметьте дорожки и шрифты для извлечения.",
            "replace" => "Для нужных дорожек выберите файл-замену. Язык, название и флаги сохранятся.",
            _ => "Отметьте дорожки, которые нужно удалить или сохранить (зависит от режима).",
        };

        void Quick(string kind)
        {
            foreach (var (idx, check) in checks)
            {
                var t = info.TrackAt(idx);
                if (t == null) continue;
                bool cur = check.GetBool("active");
                bool on = kind switch { "all" => true, "none" => false, "font" => t.IsFont || cur, _ => t.Type == kind || (cur && t.Type != kind) };
                check.Set("active", on);
            }
        }

        if (mode != "replace")
        {
            var quick = new W("AdwPreferencesGroup", ("title", "Быстрый выбор"), ("description", hint));
            var box = G.Box(spacing: 6);
            foreach (var (label, kind) in new[] { ("Все", "all"), ("Ничего", "none"), ("Видео", "video"), ("Аудио", "audio"), ("Субтитры", "subtitle"), ("Шрифты", "font") })
                box.Add(G.Button(label, onClick: () => Quick(kind), css: "pill"));
            quick.Add(new W("GtkScrolledWindow", ("vscrollbar-policy", 2), ("child", box)));
            page.Add(quick);
        }
        else page.Set("description", hint);

        W TrackRow(Track t)
        {
            var title = t.Type != "attachment" ? $"#{t.Index} · {Track.TypeRu(t.Type)}" : (t.Filename != "" ? t.Filename : $"#{t.Index}");
            var row = new W("AdwActionRow", ("title", title), ("subtitle", t.Describe()), ("use-markup", false));
            row.Start(new W("GtkImage", ("icon-name", TypeIcon.GetValueOrDefault(t.Type, "text-x-generic-symbolic"))));
            if (mode == "replace")
            {
                if (t.Type == "attachment") return row;
                var lbl = G.Label(reps.TryGetValue(t.Index, out var rp) ? Path.GetFileName(rp) : "—", ("ellipsize", Ellipsize.Middle), ("max-width-chars", 22)).Css("dim-label");
                repLabels[t.Index] = lbl;
                row.End(lbl);
                void SetRep(string p)
                {
                    if (p != "") reps[t.Index] = p; else reps.Remove(t.Index);
                    lbl.Set("label", p != "" ? Path.GetFileName(p) : "—");
                }
                row.End(G.IconButton("document-open-symbolic", "Выбрать файл-замену", () =>
                    G.PickFiles(d, $"Замена для дорожки #{t.Index}", ps => SetRep(ps[0]), multiple: false, start: Path.GetDirectoryName(item.Path))));
                row.End(G.IconButton("edit-clear-symbolic", "Убрать замену", () => SetRep("")));
                return row;
            }
            var check = new W("GtkCheckButton", ("active", selected.Contains(t.Index)), ("valign", Align.Center));
            check.On("toggled", () => { if (check.GetBool("active")) selected.Add(t.Index); else selected.Remove(t.Index); });
            row.End(check);
            row.Set("activatable-widget", check);
            checks[t.Index] = check;
            return row;
        }

        foreach (var kind in new[] { "video", "audio", "subtitle" })
        {
            var tracks = info.OfType(kind);
            if (tracks.Count == 0) continue;
            var group = new W("AdwPreferencesGroup", ("title", Track.TypeRu(kind)));
            foreach (var t in tracks) group.Add(TrackRow(t));
            page.Add(group);
        }
        if (mode == "extract" && info.Fonts.Count > 0)
        {
            var group = new W("AdwPreferencesGroup", ("title", $"Шрифты ({info.Fonts.Count})"));
            var exp = new W("AdwExpanderRow", ("title", "Вложенные шрифты"), ("subtitle", "Сохраняются в подпапку fonts"));
            foreach (var t in info.Fonts) exp.Add(TrackRow(t));
            group.Add(exp);
            page.Add(group);
        }
        var similar = all.Count(i => i != item && i.Info != null && i.Info.Signature == info.Signature);
        if (similar > 0 && mode != "replace")
        {
            var g = new W("AdwPreferencesGroup");
            var btn = G.Button($"Применить ко всем похожим файлам ({similar})", onClick: () => Apply(true), css: "pill");
            btn.Set("halign", Align.Center);
            g.Add(btn);
            page.Add(g);
        }
        tv.Set("content", page);
        G.adw_dialog_present(d.H, parent.H);
    }

    /// <summary>Диалог «надписи не найдены» — выбрать дорожку для хардсаба.</summary>
    public static void ChooseSubtitle(W parent, string filename, List<Track> tracks, Action<Track> done)
    {
        var responses = new List<(string, string, int)> { ("skip", "Без хардсаба", 0) };
        foreach (var t in tracks)
        {
            var label = $"#{t.Index} {(t.Title != "" ? t.Title : t.Language)}";
            responses.Add(($"t{t.Index}", label.Length > 40 ? label[..40] : label, 0));
        }
        var fired = false;
        G.Alert(parent, "Надписи не найдены", $"Выберите дорожку субтитров для вшивания в «{filename}»", r =>
        {
            if (fired) return;
            fired = true;
            done(r != null && r.StartsWith('t') && int.TryParse(r[1..], out var idx) ? tracks.FirstOrDefault(t => t.Index == idx) : null);
        }, null, responses.ToArray());
    }

    /// <summary>Заголовок для добавленных аудиодорожек (Сборка MKV).</summary>
    public static void AskAudioTitle(W parent, int count, List<string> presets, string def, Action<string> done)
    {
        var box = G.VBox(8);
        var entry = new W("GtkEntry", ("text", def ?? ""), ("placeholder-text", "Например: Дубляж (Studio)"));
        box.Add(entry);
        if (presets.Count > 0)
        {
            var flow = new W("GtkFlowBox", ("selection-mode", 0), ("max-children-per-line", 3u), ("column-spacing", 6u), ("row-spacing", 6u));
            foreach (var p in presets) flow.Add(G.Button(p, onClick: () => entry.Set("text", p), css: "pill"));
            box.Add(flow);
        }
        bool fired = false;
        void Finish(string v) { if (fired) return; fired = true; done(v); }
        var dlg = G.Alert(parent, "Заголовок аудиодорожек", $"Добавлено аудиофайлов: {count}. Название будет записано в дорожки при сборке.",
            r => Finish(r == "ok" ? entry.GetStr("text").Trim() : null), box, ("skip", "Без заголовка", 0), ("ok", "Применить", 1));
        entry.On("activate", () => { Finish(entry.GetStr("text").Trim()); G.adw_dialog_close(dlg.H); });
    }

    public static void AskText(W parent, string heading, string body, string value, Action<string> done)
    {
        var entry = new W("GtkEntry", ("text", value ?? ""));
        bool fired = false;
        void Finish(bool ok) { if (fired) return; fired = true; if (ok) done(entry.GetStr("text")); }
        var dlg = G.Alert(parent, heading, body, r => Finish(r == "ok"), entry, ("cancel", "Отмена", 0), ("ok", "OK", 1));
        entry.On("activate", () => { Finish(true); G.adw_dialog_close(dlg.H); });
    }

    /// <summary>Менеджер моделей whisper.cpp: скачивание (с докачкой) и удаление.</summary>
    public static void WhisperModels(W parent, Action onChanged)
    {
        var (d, tv, header) = Dialog("Модели Whisper", 620, 640);
        header.Start(G.IconButton("folder-open-symbolic", "Открыть папку моделей", () => Desktop.OpenPath(SpeechRecognition.ModelsDir())));
        var page = new W("AdwPreferencesPage");
        var group = new W("AdwPreferencesGroup", ("title", "Доступные модели"), ("description", $"Папка: {SpeechRecognition.ModelsDir()}"));
        page.Add(group);
        var tokens = new Dictionary<string, CancelToken>();
        var boxes = new Dictionary<string, W>();
        var bars = new Dictionary<string, W>();

        void Refresh(string key, bool downloading = false)
        {
            var box = boxes[key];
            box.Clear();
            if (downloading)
            {
                var bar = new W("GtkProgressBar", ("show-text", true), ("text", "0%"), ("valign", Align.Center));
                bar.Size(160, -1);
                bars[key] = bar;
                box.Add(bar, G.IconButton("process-stop-symbolic", "Отменить", () => tokens.GetValueOrDefault(key)?.Cancel()));
            }
            else if (File.Exists(SpeechRecognition.ModelPath(key)))
            {
                box.Add(new W("GtkImage", ("icon-name", "emblem-ok-symbolic")).Css("success"));
                box.Add(G.IconButton("user-trash-symbolic", "Удалить модель", () => G.Confirm(d, "Удалить модель?", key, () =>
                {
                    try { File.Delete(SpeechRecognition.ModelPath(key)); } catch { }
                    Refresh(key);
                    onChanged();
                }, "Удалить", destructive: true)));
            }
            else box.Add(G.Button("Скачать", onClick: () => Download(key), css: "pill"));
        }

        void Download(string key)
        {
            var token = new CancelToken();
            tokens[key] = token;
            Refresh(key, downloading: true);
            long last = 0;
            MainLoop.RunBg(() => SpeechRecognition.Download(key, (pct, msg) =>
            {
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref last) < 150) return;
                Interlocked.Exchange(ref last, now);
                MainLoop.Post(() =>
                {
                    if (!bars.TryGetValue(key, out var bar)) return;
                    bar.Set("fraction", pct / 100);
                    bar.Set("text", $"{pct:0}% · {msg}");
                });
            }, token), (_, err) =>
            {
                bars.Remove(key);
                Refresh(key);
                if (err != null && !token.Cancelled) G.Info(d, "Ошибка загрузки", err.Message);
                onChanged();
            });
        }

        foreach (var m in SpeechRecognition.Models)
        {
            var row = new W("AdwActionRow", ("title", m.Key), ("subtitle", $"~{m.SizeMb:0} МБ · {m.Description}"), ("use-markup", false));
            var box = G.Box(spacing: 6, props: [("valign", Align.Center)]);
            row.End(box);
            boxes[m.Key] = box;
            group.Add(row);
            Refresh(m.Key);
        }
        tv.Set("content", page);
        d.On("closed", () => { foreach (var t in tokens.Values) t.Cancel(); });
        G.adw_dialog_present(d.H, parent.H);
    }
}
