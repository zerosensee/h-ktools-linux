using System.Runtime.InteropServices;
using HKTools.Core;
using HKTools.Media;
using HKTools.Ui.Native;

namespace HKTools.Ui;

/// <summary>
/// Дымовой тест интерфейса: HKTOOLS_SMOKE=/папка hktools — обходит страницы, открывает окна инструментов
/// и сохраняет снимки (используется в CI под Xvfb). Код возврата ≠ 0 при ошибках.
/// </summary>
internal static class Smoke
{
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_widget_paintable_new(IntPtr w);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_snapshot_new();
    [DllImport(Lib.Gtk)] static extern void gdk_paintable_snapshot(IntPtr p, IntPtr s, double w, double h);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_snapshot_free_to_node(IntPtr s);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_widget_get_native(IntPtr w);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_native_get_renderer(IntPtr n);
    [DllImport(Lib.Gtk)] static extern IntPtr gsk_renderer_render_texture(IntPtr r, IntPtr node, IntPtr viewport);
    [DllImport(Lib.Gtk)] static extern int gdk_texture_save_to_png(IntPtr t, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Lib.Gtk)] static extern void gsk_render_node_unref(IntPtr n);
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_window_get_toplevels();
    [DllImport(Lib.Adw)] static extern IntPtr adw_application_window_get_visible_dialog(IntPtr w);

    static readonly List<string> Errors = new();
    static string _out;
    public static int ExitCode => Errors.Count > 0 ? 1 : 0;

    public static bool Enabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HKTOOLS_SMOKE"));

    public static void Shot(W w, string name)
    {
        int width = w.Width, height = w.Height;
        if (width <= 0 || height <= 0) { Errors.Add($"{name}: нулевой размер"); return; }
        var paintable = gtk_widget_paintable_new(w.H);
        var snap = gtk_snapshot_new();
        gdk_paintable_snapshot(paintable, snap, width, height);
        var node = gtk_snapshot_free_to_node(snap);
        GObj.g_object_unref(paintable);
        if (node == IntPtr.Zero) { Errors.Add($"{name}: пустой снимок"); return; }
        var tex = gsk_renderer_render_texture(gtk_native_get_renderer(gtk_widget_get_native(w.H)), node, IntPtr.Zero);
        gdk_texture_save_to_png(tex, Path.Combine(_out, name + ".png"));
        GObj.g_object_unref(tex);
        gsk_render_node_unref(node);
        Console.WriteLine($"saved {name} {width}x{height}");
    }

    static W FindWindow(string titlePart)
    {
        var list = gtk_window_get_toplevels();
        for (uint i = 0, n = G.g_list_model_get_n_items(list); i < n; i++)
        {
            var p = G.g_list_model_get_item(list, i);
            GObj.g_object_unref(p);
            var w = new W(p);
            if (w.Visible && (w.GetStr("title") ?? "").Contains(titlePart)) return w;
        }
        return null;
    }

    static void CloseDialog(MainWindow win)
    {
        var d = adw_application_window_get_visible_dialog(win.Win.H);
        if (d != IntPtr.Zero) G.adw_dialog_close(d);
    }

    static void ShotDialog(MainWindow win, string name) => Shot(win.Win, name);

    public static void Run(MainWindow win)
    {
        _out = Environment.GetEnvironmentVariable("HKTOOLS_SMOKE");
        Directory.CreateDirectory(_out);
        var sample = Environment.GetEnvironmentVariable("HKTOOLS_SAMPLE") ?? "/tmp/kt/sample.mkv";
        var dir = Path.GetDirectoryName(sample);
        var outBase = Path.Combine(_out, "results");
        var steps = new Queue<(Action Fn, uint Delay)>();
        void Step(Action fn, uint delay = 700) => steps.Enqueue((fn, delay));
        WorkPanel P() => win.CurrentPanel();

        win.Win.Set("default-width", 1360);
        win.Win.Set("default-height", 860);
        Step(() => Shot(win.Win, "01_home"), 2500);
        foreach (var id in new[] { "video-encode", "audio-encode", "mkv-assembly", "track-extractor", "subtitles-convert", "speech-recognition",
                     "media-downloader", "audio-transplant", "timing-calculator", "deps", "log" })
        {
            Step(() => win.ShowPage(id), 900);
            Step(() => Shot(win.Win, $"page_{id}"), 300);
        }
        Step(() => win.ShowPreferences(), 900);
        Step(() => ShotDialog(win, "d_preferences"), 300);
        Step(() => CloseDialog(win), 500);
        if (File.Exists(sample))
        {
            Step(() => { win.ShowPage("track-extractor"); P().AddPaths([sample]); }, 2500);
            Step(() => Shot(win.Win, "q_track_extractor_queue"), 300);
            Step(() => P().OpenTracks(P().Items[0]), 1200);
            Step(() => ShotDialog(win, "q_track_dialog"), 300);
            Step(() => CloseDialog(win), 600);
            Step(() => { P().SetOutDir(Path.Combine(outBase, "extract")); P().ToggleRun(); }, 4000);
            Step(() => Shot(win.Win, "q_track_extractor_done"), 300);
            Step(() => { win.ShowPage("video-encode"); P().AddPaths([sample]); }, 2000);
            Step(() => Shot(win.Win, "q_video_queue"), 300);
            Step(() => { win.ShowPage("bitrate-viewer"); P().AddPaths([sample]); }, 2000);
            Step(() => { P().SetOutDir(Path.Combine(outBase, "br")); P().ToggleRun(); }, 4000);
            Step(() =>
            {
                var w = FindWindow("Битрейт");
                if (w == null) Errors.Add("окно графика битрейта не открылось");
                else { Shot(w, "w_bitrate_chart"); G.gtk_window_close(w.H); }
            }, 500);
            var signs = Path.Combine(dir, "signs.ass");
            if (File.Exists(signs))
            {
                Step(() => { win.ShowPage("subtitles-convert"); P().AddPaths([signs]); SubtitlePreview.Show(P(), P().Items[0]); }, 1500);
                Step(() => ShotDialog(win, "q_sub_preview"), 300);
                Step(() => CloseDialog(win), 500);
            }
            var dub = Path.Combine(dir, "sample.dub.flac");
            if (File.Exists(dub))
            {
                Step(() =>
                {
                    win.ShowPage("audio-transplant");
                    P().Form.SetValue("transplant_source", dub);
                    P().AddPaths([sample]);
                    WaveformSync.Show(P());
                }, 2500);
                Step(() => WaveformSync.Last?.Load(), 3500);
                Step(() => { WaveformSync.Last?.SetShift(250); WaveformSync.Last?.Zoom(4000); }, 600);
                Step(() => { if (FindWindow("осциллограмм") is { } w) Shot(w, "w_waveform"); else Errors.Add("окно синхронизации не открылось"); }, 600);
                Step(() => { if (FindWindow("осциллограмм") is { } w) G.gtk_window_close(w.H); }, 600);
                Step(() =>
                {
                    win.ShowPage("mkv-assembly");
                    var extra = new[] { dub, Path.Combine(dir, "sample.signs.ass"), Path.Combine(dir, "sample.ass") }.Where(File.Exists);
                    P().AddPaths([sample, .. extra]);
                }, 1500);
                Step(() => Shot(win.Win, "q_mkv_queue"), 300);
                Step(() => { P().SetOutDir(Path.Combine(outBase, "mkv")); P().ToggleRun(); }, 3500);
                Step(() => Shot(win.Win, "q_mkv_done"), 300);
            }
            Step(() => { win.ShowPage("timing-calculator"); }, 600);
            Step(() => { win.ShowPage("home"); }, 600);
        }

        MainLoop.Timeout(100, () => { NextStep(); return false; });

        void NextStep()
        {
            if (steps.Count == 0)
            {
                Proc.KillAll();
                foreach (var e in Errors) Console.Error.WriteLine("SMOKE ERROR: " + e);
                foreach (var e in Log.All().Where(e => e.Level == "ERROR" && e.Source == "UI"))
                {
                    Console.Error.WriteLine("UI ERROR: " + e.Message + "\n" + e.Details);
                    Errors.Add(e.Message);
                }
                Console.WriteLine(Errors.Count == 0 ? "SMOKE OK" : $"SMOKE FAILED: {Errors.Count}");
                Settings.Instance.SaveNow();
                Environment.Exit(ExitCode);
            }
            var (fn, delay) = steps.Dequeue();
            try { fn(); }
            catch (Exception ex) { Errors.Add(ex.ToString()); }
            MainLoop.Timeout(delay, () => { NextStep(); return false; });
        }
    }
}
