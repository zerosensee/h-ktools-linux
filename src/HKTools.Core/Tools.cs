using HKTools.Media;

namespace HKTools;

public sealed record Tool(string Key, string Title, string[] Candidates, string Package, bool Aur = false,
    string[] AurVariants = null, bool Required = false, string[] VersionArgs = null, string Purpose = "");

public sealed record ToolStatus(Tool Tool, string Path, string Version)
{
    public bool Found => !string.IsNullOrEmpty(Path);
}

public sealed class ToolMissingException(string msg) : Exception(msg);

/// <summary>Внешние утилиты: поиск, версии, пакеты Arch и установка.</summary>
public static class Tools
{
    public static readonly Dictionary<string, Tool> All = new Tool[]
    {
        new("ffmpeg", "FFmpeg", ["ffmpeg"], "ffmpeg", Required: true, VersionArgs: ["-version"],
            Purpose: "Кодирование, ремукс, фильтры — основа почти всех инструментов"),
        new("ffprobe", "FFprobe", ["ffprobe"], "ffmpeg", Required: true, VersionArgs: ["-version"],
            Purpose: "Анализ структуры медиафайлов"),
        new("mkvmerge", "MKVToolNix (mkvmerge)", ["mkvmerge"], "mkvtoolnix-cli",
            Purpose: "Сборка MKV, сдвиг аудио без перекодирования, пересадка дорожек"),
        new("mkvextract", "MKVToolNix (mkvextract)", ["mkvextract"], "mkvtoolnix-cli",
            Purpose: "Извлечение VobSub-субтитров из MKV"),
        new("yt-dlp", "yt-dlp", ["yt-dlp"], "yt-dlp", Purpose: "Загрузка медиа по ссылке"),
        new("deno", "Deno (JS-рантайм для yt-dlp)", ["deno"], "deno", Purpose: "Нужен yt-dlp для части сайтов (YouTube)"),
        new("whisper", "whisper.cpp", ["whisper-cli", "whisper.cpp", "whisper-cpp"], "whisper.cpp", Aur: true,
            AurVariants: ["whisper.cpp", "whisper.cpp-cuda", "whisper.cpp-vulkan", "whisper.cpp-hip"], VersionArgs: ["--help"],
            Purpose: "Распознавание речи (CPU / CUDA / Vulkan / ROCm)"),
        new("fdkaac", "fdkaac (Fraunhofer FDK AAC)", ["fdkaac"], "fdkaac", Purpose: "Качественный AAC/HE-AAC — замена QAAC в Linux"),
        new("wine", "Wine", ["wine"], "wine", Purpose: "Необязательно: запуск qaac64.exe, если нужен именно Apple AAC"),
        new("notify-send", "libnotify (notify-send)", ["notify-send"], "libnotify",
            Purpose: "Уведомления о завершении (mako, swaync, dunst, Quickshell)"),
    }.ToDictionary(t => t.Key);

    public static string FindInPath(string name)
    {
        if (name.Contains('/')) return File.Exists(name) ? name : null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(':'))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var p = System.IO.Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static string Which(string key)
    {
        var ov = Settings.Instance.ToolPath(key);
        if (!string.IsNullOrWhiteSpace(ov))
        {
            var p = Paths.ExpandUser(ov);
            if (File.Exists(p)) return p;
            var f = FindInPath(p);
            if (f != null) return f;
        }
        var names = All.TryGetValue(key, out var t) ? t.Candidates : [key];
        foreach (var n in names)
        {
            var f = FindInPath(n);
            if (f != null) return f;
        }
        return null;
    }

    public static string Require(string key)
    {
        var p = Which(key);
        if (p != null) return p;
        var t = All.GetValueOrDefault(key);
        var hint = t is { Aur: true } ? $"paru -S {t.Package}" : $"sudo pacman -S {t?.Package ?? key}";
        throw new ToolMissingException($"Не найдена утилита «{t?.Title ?? key}». Установите: {hint}");
    }

    public static ToolStatus ProbeVersion(string key)
    {
        var t = All[key];
        var path = Which(key);
        if (path == null) return new ToolStatus(t, null, "");
        var (_, o, e) = Proc.Capture([path, .. (t.VersionArgs ?? ["--version"])], 8000);
        var first = (string.IsNullOrWhiteSpace(o) ? e : o).Split('\n').FirstOrDefault()?.Trim() ?? "";
        string ver = key switch
        {
            "ffmpeg" or "ffprobe" => first.Split(' ').ElementAtOrDefault(2) ?? first,
            "whisper" => "установлен",
            _ => first.Length > 80 ? first[..80] : first,
        };
        return new ToolStatus(t, path, ver);
    }

    public static List<string> QaacCommand()
    {
        var raw = Settings.Instance.GetStr("qaac_command").Trim();
        if (raw != "") return ShellSplit(raw).Select(Paths.ExpandUser).ToList();
        var f = FindInPath("qaac") ?? FindInPath("qaac64");
        return f != null ? [f] : null;
    }

    public static string AurHelper() =>
        new[] { "paru", "yay", "pikaur", "trizen", "aura" }.FirstOrDefault(h => FindInPath(h) != null);

    public static string InstallCommand(IEnumerable<string> keys)
    {
        var repo = new List<string>();
        var aur = new List<string>();
        foreach (var k in keys)
        {
            var t = All[k];
            var target = t.Aur ? aur : repo;
            if (!target.Contains(t.Package)) target.Add(t.Package);
        }
        var parts = new List<string>();
        if (repo.Count > 0) parts.Add("sudo pacman -S --needed " + string.Join(" ", repo));
        if (aur.Count > 0)
        {
            var h = AurHelper();
            parts.Add(h != null ? $"{h} -S --needed {string.Join(" ", aur)}"
                : $"echo 'Нужен AUR-помощник (paru/yay) для: {string.Join(" ", aur)}'");
        }
        return parts.Count > 0 ? string.Join(" && ", parts) : "true";
    }

    static readonly (string, string[])[] Terminals =
    [
        ("xdg-terminal-exec", []), ("kitty", ["--"]), ("foot", []), ("ghostty", ["-e"]), ("alacritty", ["-e"]),
        ("wezterm", ["start", "--"]), ("konsole", ["-e"]), ("gnome-terminal", ["--"]), ("xfce4-terminal", ["-x"]),
        ("xterm", ["-e"]),
    ];

    /// <summary>Открыть команду в терминале ($TERMINAL или первый найденный).</summary>
    public static bool RunInTerminal(string shellCommand)
    {
        var wrapped = shellCommand + "; echo; read -rp 'Готово. Нажмите Enter, чтобы закрыть…' _";
        var list = new List<(string, string[])>();
        var env = Environment.GetEnvironmentVariable("TERMINAL");
        if (!string.IsNullOrWhiteSpace(env)) list.Add((env.Trim(), ["-e"]));
        list.AddRange(Terminals);
        foreach (var (name, prefix) in list)
        {
            var exe = FindInPath(name);
            if (exe == null) continue;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
                foreach (var a in prefix) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add("bash");
                psi.ArgumentList.Add("-lc");
                psi.ArgumentList.Add(wrapped);
                System.Diagnostics.Process.Start(psi);
                return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>Разбор строки в аргументы по правилам POSIX-shell (кавычки, экранирование).</summary>
    public static List<string> ShellSplit(string s)
    {
        var res = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inS = false, inD = false, any = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inS) { if (c == '\'') inS = false; else cur.Append(c); continue; }
            if (inD)
            {
                if (c == '"') inD = false;
                else if (c == '\\' && i + 1 < s.Length && "\"\\$`".Contains(s[i + 1])) cur.Append(s[++i]);
                else cur.Append(c);
                continue;
            }
            if (c == '\'') { inS = true; any = true; }
            else if (c == '"') { inD = true; any = true; }
            else if (c == '\\' && i + 1 < s.Length) { cur.Append(s[++i]); any = true; }
            else if (char.IsWhiteSpace(c)) { if (any || cur.Length > 0) { res.Add(cur.ToString()); cur.Clear(); any = false; } }
            else { cur.Append(c); any = true; }
        }
        if (inS || inD) throw new FormatException("Незакрытая кавычка");
        if (any || cur.Length > 0) res.Add(cur.ToString());
        return res;
    }
}
