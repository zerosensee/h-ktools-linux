using HKTools.Core;
using HKTools.Ui.Native;

namespace HKTools.Ui;

/// <summary>Глобальные настройки приложения (AdwPreferencesDialog).</summary>
internal static class Prefs
{
    static readonly (string Key, string Title)[] Themes = [("system", "Как в системе"), ("light", "Светлая"), ("dark", "Тёмная")];

    public static void ApplyTheme() =>
        G.ColorScheme(Settings.Instance.GetStr("theme") switch { "light" => 1, "dark" => 4, _ => 0 });

    static W Switch(W group, string key, string title, string subtitle = "", Action onChange = null)
    {
        var row = new W("AdwSwitchRow", ("title", title), ("active", Settings.Instance.GetBool(key)));
        if (subtitle != "") row.Set("subtitle", subtitle);
        row.OnNotify("active", () => { Settings.Instance.Set(key, row.GetBool("active")); onChange?.Invoke(); });
        group.Add(row);
        return row;
    }

    static W Entry(W group, string key, string title, string tooltip = "", Action onChange = null)
    {
        var row = new W("AdwEntryRow", ("title", title), ("text", Settings.Instance.GetStr(key)));
        if (tooltip != "") row.Tooltip = tooltip;
        row.On("changed", () => { Settings.Instance.Set(key, row.GetStr("text")); onChange?.Invoke(); });
        group.Add(row);
        return row;
    }

    static W Group(string title, string description = null)
    {
        var g = new W("AdwPreferencesGroup", ("title", title));
        if (description != null) g.Set("description", description);
        return g;
    }

    public static void Show(MainWindow win, Action onChanged)
    {
        var dlg = new W("AdwPreferencesDialog", ("title", "Настройки"), ("search-enabled", true));
        W preview = null;
        void Changed()
        {
            try { preview?.Set("subtitle", "→ " + Rename.Apply("Серия 01 [1080p]", 1) + ".mkv"); } catch { }
            onChanged();
        }

        // --- Общие ---
        var page = new W("AdwPreferencesPage", ("title", "Общие"), ("icon-name", "emblem-system-symbolic"));
        var outg = Group("Вывод");
        Switch(outg, "overwrite_existing", "Перезаписывать существующие результаты", "Иначе файл пропускается, если результат уже есть");
        var sub = Switch(outg, "use_auto_subfolder", "Сохранять в подпапку рядом с исходником", "Если папка результатов не выбрана вручную", Changed);
        var name = Entry(outg, "output_subfolder", "Имя подпапки результатов", onChange: Changed);
        sub.Bind("active", name, "sensitive");
        Switch(outg, "clear_list_on_add", "Очищать список при добавлении новых файлов");
        page.Add(outg);
        var par = Group("Параллельная обработка", "Несколько файлов одновременно. Для NVENC обычно разумно 2–3 потока.");
        var en = Switch(par, "enable_parallel", "Включить параллельную обработку");
        var spin = G.SpinRow(1, 16, 1);
        spin.Set("title", "Максимум одновременных задач");
        spin.Set("value", (double)Math.Max(1, Settings.Instance.GetLong("max_parallel")));
        spin.OnNotify("value", () => Settings.Instance.Set("max_parallel", (long)spin.GetDouble("value")));
        en.Bind("active", spin, "sensitive");
        par.Add(spin);
        page.Add(par);
        dlg.Add(page);

        // --- Переименование ---
        page = new W("AdwPreferencesPage", ("title", "Переименование"), ("icon-name", "document-edit-symbolic"));
        var g = Group("Пакетное переименование результатов",
            "Поиск и замена в имени выходного файла (аналог PowerRename). Переменные: $1 $2 — группы, ${num}, ${num:2}, ${YYYY} ${MM} ${DD}, ${hh} ${mm} ${ss}, ${ruuidv4}");
        Switch(g, "rename_enabled", "Включить переименование", onChange: Changed);
        Entry(g, "rename_search", "Найти", @"Например:  - (\d+)", Changed);
        Entry(g, "rename_replace", "Заменить на", "Например:  - [$1]", Changed);
        Switch(g, "rename_use_regex", "Регулярные выражения", onChange: Changed);
        Switch(g, "rename_case_sensitive", "Учитывать регистр", onChange: Changed);
        page.Add(g);
        var ex = Group("Пример");
        preview = new W("AdwActionRow", ("title", "Серия 01 [1080p].mkv"), ("subtitle", ""), ("use-markup", false));
        ex.Add(preview);
        page.Add(ex);
        dlg.Add(page);

        // --- Интеграция ---
        page = new W("AdwPreferencesPage", ("title", "Интеграция"), ("icon-name", "preferences-desktop-display-symbolic"));
        g = Group("Оформление", "Цвета берутся из темы GTK — в том числе сгенерированной matugen (Ryoku) через ~/.config/gtk-4.0/gtk.css");
        var theme = new W("AdwComboRow", ("title", "Тема"), ("model", G.StringList(Themes.Select(t => t.Title))));
        theme.Set("selected", (uint)Math.Max(0, Array.FindIndex(Themes, t => t.Key == Settings.Instance.GetStr("theme"))));
        theme.OnNotify("selected", () =>
        {
            var i = Convert.ToInt32(theme.Get("selected"));
            if (i >= 0 && i < Themes.Length) { Settings.Instance.Set("theme", Themes[i].Key); ApplyTheme(); }
        });
        g.Add(theme);
        page.Add(g);
        g = Group("Уведомления и статус");
        Switch(g, "notify_on_finish", "Уведомление по завершении пакета", "Через notify-send (mako, swaync, dunst…)");
        Switch(g, "notify_only_unfocused", "Только если окно не в фокусе");
        Switch(g, "status_file", "Файл статуса для Waybar / Quickshell", Paths.StatusFile);
        page.Add(g);
        dlg.Add(page);

        // --- Утилиты ---
        page = new W("AdwPreferencesPage", ("title", "Утилиты"), ("icon-name", "applications-utilities-symbolic"));
        g = Group("Пути к программам", "Пусто — искать в PATH. Можно указать, например, ffmpeg из ffmpeg-full (AUR) или свою сборку.");
        foreach (var key in new[] { "ffmpeg", "ffprobe", "mkvmerge", "mkvextract", "yt-dlp", "whisper", "fdkaac" })
        {
            var k = key;
            var row = new W("AdwEntryRow", ("title", Tools.All[k].Title), ("text", Settings.Instance.ToolPath(k)));
            row.Tooltip = $"Найдено: {Tools.Which(k) ?? "нет"}";
            row.On("changed", () => Settings.Instance.SetToolPath(k, row.GetStr("text").Trim()));
            row.End(G.IconButton("document-open-symbolic", "Выбрать файл", () => G.PickFiles(dlg, "Исполняемый файл", ps => row.Set("text", ps[0]), multiple: false)));
            g.Add(row);
        }
        page.Add(g);
        g = Group("Дополнительно");
        Entry(g, "qaac_command", "Команда QAAC (Wine)", "Например: wine /home/user/qaac/qaac64.exe  — нужны qaac64.exe и библиотеки Apple (QTfiles64)");
        Entry(g, "vaapi_device", "Устройство VAAPI", "Пусто — авто (/dev/dri/renderD128…)");
        Entry(g, "whisper_models_dir", "Папка моделей Whisper", Paths.WhisperModelsDir());
        page.Add(g);
        dlg.Add(page);
        Changed();
        G.adw_dialog_present(dlg.H, win.Win.H);
    }
}
