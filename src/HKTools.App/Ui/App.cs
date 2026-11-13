using System.Reflection;
using System.Runtime.InteropServices;
using HKTools.Core;
using HKTools.Media;
using HKTools.Scripts;
using HKTools.Ui.Native;

namespace HKTools.Ui;

/// <summary>Точка входа GUI: AdwApplication с единственным экземпляром (повторный запуск передаёт файлы в открытое окно).</summary>
internal static class App
{
    [DllImport(Lib.Adw)] static extern IntPtr adw_application_new([MarshalAs(UnmanagedType.LPUTF8Str)] string id, int flags);
    [DllImport(Lib.Gio)] static extern int g_application_run(IntPtr app, int argc, IntPtr argv);
    [DllImport(Lib.Gio)] static extern void g_application_activate(IntPtr app);
    [DllImport(Lib.Gio)] static extern void g_application_quit(IntPtr app);
    [DllImport(Lib.Gio)] static extern IntPtr g_application_command_line_get_arguments(IntPtr cmdline, out int argc);
    [DllImport(Lib.GLib)] static extern void g_strfreev(IntPtr strv);
    [DllImport(Lib.GLib)] static extern void g_set_application_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib.GLib)] static extern void g_set_prgname([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    public static GObj Application;
    public static MainWindow Window;
    static readonly List<string> PendingFiles = new();
    static string _pendingTool;
    static bool _smokeStarted;

    public static IEnumerable<string> DataDirs()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var d in new[]
                 {
                     Environment.GetEnvironmentVariable("HKTOOLS_DATA_DIR"), Path.Combine(baseDir, "data"), Path.Combine(baseDir, "..", "..", "..", "..", "..", "data"),
                     Path.Combine(baseDir, "..", "share", "hktools"), Paths.ExpandUser("~/.local/share/hktools"), "/usr/local/share/hktools", "/usr/share/hktools",
                 })
            if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) yield return Path.GetFullPath(d);
    }

    public static string FindIcon()
    {
        foreach (var d in DataDirs())
            foreach (var n in new[] { "icons/hktools-256.png", "hktools-256.png" })
                if (File.Exists(Path.Combine(d, n))) return Path.Combine(d, n);
        var sys = $"/usr/share/icons/hicolor/256x256/apps/{AppInfo.Id}.png";
        return File.Exists(sys) ? sys : null;
    }

    public static int Run(string[] files, string tool)
    {
        try { Lib.LoadGlobal(); }
        catch (DllNotFoundException ex)
        {
            Console.Error.WriteLine($"Не найдены библиотеки GTK 4 / libadwaita: {ex.Message}\nУстановите: sudo pacman -S gtk4 libadwaita");
            return 1;
        }
        g_set_prgname(AppInfo.Id);
        g_set_application_name(AppInfo.Name);
        Application = new GObj(adw_application_new(AppInfo.Id, 8)); // G_APPLICATION_HANDLES_COMMAND_LINE
        Application.On("startup", Startup);
        Application.On("activate", Activate);
        Application.On("shutdown", Shutdown);
        Signals.Connect(Application.H, "command-line", new SigPB((_, cmdline, _) => Signals.Guard(() => CommandLine(cmdline), 1)));
        var argv = new List<string> { "hktools" };
        if (tool != null) argv.Add("tool:" + tool);
        argv.AddRange(files.Select(f => Path.GetFullPath(Paths.ExpandUser(f))));
        var arr = Lib.Strv(argv);
        var code = g_application_run(Application.H, argv.Count, arr);
        Lib.FreeStrv(arr, argv.Count);
        return code;
    }

    static int CommandLine(IntPtr cmdline)
    {
        var strv = g_application_command_line_get_arguments(cmdline, out var argc);
        for (int i = 1; i < argc; i++)
        {
            var a = Lib.Str(Marshal.ReadIntPtr(strv, i * IntPtr.Size));
            if (a == null) continue;
            if (a.StartsWith("tool:")) _pendingTool = a[5..];
            else if (File.Exists(a) || Directory.Exists(a)) PendingFiles.Add(a);
        }
        g_strfreev(strv);
        g_application_activate(Application.H);
        return 0;
    }

    static void Startup()
    {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("style.css"))
        using (var r = new StreamReader(s!)) G.LoadCss(r.ReadToEnd());
        foreach (var d in DataDirs())
            if (Directory.Exists(Path.Combine(d, "icons", "hicolor"))) G.AddIconPath(Path.Combine(d, "icons"));
        Prefs.ApplyTheme();
        G.Action(Application, "quit", QuitSafely);
        G.Action(Application, "preferences", () => Window?.ShowPreferences());
        G.Action(Application, "about", () => Window?.ShowAbout());
        G.Accels(Application, "app.quit", "<Ctrl>q");
        G.Accels(Application, "app.preferences", "<Ctrl>comma");
        G.Accels(Application, "app.about", "F1");
        G.Accels(Application, "win.add-files", "<Ctrl>o");
        G.Accels(Application, "win.start", "<Ctrl>Return");
        G.Accels(Application, "win.open-log", "<Ctrl>l");
        Log.Info($"{AppInfo.Name} {AppInfo.Version} запущен (.NET {Environment.Version}, GTK {G.gtk_get_major_version()}.{G.gtk_get_minor_version()}, " +
                 $"libadwaita {G.adw_get_major_version()}.{G.adw_get_minor_version()}, сессия: {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "?"}, " +
                 $"рабочий стол: {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "?"})");
    }

    static void Activate()
    {
        Window ??= new MainWindow(Application, FindIcon());
        Window.Present();
        var tool = _pendingTool;
        _pendingTool = null;
        var files = PendingFiles.ToList();
        PendingFiles.Clear();
        if (tool != null) Window.ShowPage(tool);
        if (files.Count > 0) Window.OpenFiles(files, keepCurrent: tool != null);
        if (Smoke.Enabled && !_smokeStarted) { _smokeStarted = true; Smoke.Run(Window); }
    }

    public static void QuitSafely()
    {
        if (Window != null) Window.Close();
        else g_application_quit(Application.H);
    }

    static void Shutdown()
    {
        Proc.KillAll();
        Status.Clear();
        Settings.Instance.SaveNow();
    }

    /// <summary>Отрисовка графика битрейта в PNG через cairo (используется инструментом и CLI).</summary>
    public static void RegisterRenderers() =>
        BitrateChart.PngRenderer = (data, path, w, h, dark) => Cairo.RenderPng(path, w, h, c => BitrateChart.Draw(c, w, h, data, dark, title: true));
}
