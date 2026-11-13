using System.Diagnostics;

namespace HKTools.Core;

/// <summary>Интеграция с рабочим столом: уведомления, корзина, открытие папок, статус для Waybar/Quickshell.</summary>
public static class Desktop
{
    static void Spawn(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch { }
    }

    public static void Notify(string title, string body = "", string urgency = "normal")
    {
        var exe = Tools.FindInPath("notify-send");
        if (exe != null) Spawn(exe, $"--app-name={AppInfo.Name}", $"--icon={AppInfo.Id}", $"--urgency={urgency}", title, body);
    }

    /// <summary>В корзину (freedesktop, через gio), иначе удалить.</summary>
    public static bool Trash(string path)
    {
        var gio = Tools.FindInPath("gio");
        if (gio != null && Media.Proc.Capture([gio, "trash", path], 15000).Code == 0) return true;
        try { File.Delete(path); return true; } catch { return false; }
    }

    public static void OpenPath(string path)
    {
        var exe = Tools.FindInPath("xdg-open");
        if (exe != null) Spawn(exe, path);
        else if ((exe = Tools.FindInPath("gio")) != null) Spawn(exe, "open", path);
    }

    /// <summary>Показать файл в файловом менеджере (D-Bus FileManager1), иначе открыть папку.</summary>
    public static void ShowInFolder(string path)
    {
        var dbus = Tools.FindInPath("dbus-send");
        if (dbus != null && File.Exists(path))
        {
            var uri = new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri;
            var r = Media.Proc.Capture([dbus, "--session", "--print-reply", "--dest=org.freedesktop.FileManager1",
                "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems", $"array:string:{uri}", "string:"], 3000);
            if (r.Code == 0) return;
        }
        OpenPath(File.Exists(path) ? System.IO.Path.GetDirectoryName(path) : path);
    }
}

/// <summary>$XDG_RUNTIME_DIR/hktools/status.json для модулей Waybar / Quickshell / eww.</summary>
public static class Status
{
    static readonly object Lock = new();
    static DateTime _last = DateTime.MinValue;
    static Timer _clear;

    static void Write(Dictionary<string, object> data)
    {
        if (!Settings.Instance.GetBool("status_file")) return;
        try
        {
            var target = Paths.StatusFile;
            File.WriteAllText(target + ".tmp", Json.Serialize(data));
            File.Move(target + ".tmp", target, true);
        }
        catch { }
    }

    static double Ts => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public static void Update(string script, int done, int total, double percent, string current, bool force = false)
    {
        lock (Lock)
        {
            if (!force && (DateTime.UtcNow - _last).TotalMilliseconds < 800) return;
            _last = DateTime.UtcNow;
            _clear?.Dispose();
            _clear = null;
        }
        Write(new()
        {
            ["text"] = $"{percent:0}%", ["alt"] = "running", ["class"] = "running", ["percentage"] = (int)percent,
            ["tooltip"] = $"{script}\n{done}/{total} · {current}", ["script"] = script, ["done"] = done, ["total"] = total,
            ["current"] = current, ["ts"] = Ts,
        });
    }

    public static void Finish(string script, int ok, int failed, bool cancelled)
    {
        var state = cancelled ? "cancelled" : failed > 0 ? "failed" : "done";
        var icon = state switch { "done" => "✓", "failed" => "!", _ => "×" };
        Write(new()
        {
            ["text"] = icon, ["alt"] = state, ["class"] = state, ["percentage"] = 100,
            ["tooltip"] = $"{script}: готово {ok}, ошибок {failed}" + (cancelled ? " (отменено)" : ""), ["script"] = script, ["ts"] = Ts,
        });
        lock (Lock)
        {
            _clear?.Dispose();
            _clear = new Timer(_ => Clear(), null, 15000, Timeout.Infinite);
        }
    }

    public static void Clear() =>
        Write(new() { ["text"] = "", ["alt"] = "idle", ["class"] = "idle", ["tooltip"] = "hktools: нет задач", ["ts"] = Ts });
}
