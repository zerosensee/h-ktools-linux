using System.Text.Json;
using HKTools.Core;
using HKTools.Media;
using HKTools.Scripts;

namespace HKTools.Cli;

/// <summary>Командная строка: запуск инструментов без GUI, проверка зависимостей, модуль Waybar.</summary>
public static class CliMain
{
    public static readonly HashSet<string> Commands = ["list", "run", "options", "deps", "hw", "waybar", "status", "gui", "models", "help"];
    static readonly Dictionary<string, string> StatusIcon = new()
    {
        ["done"] = "✓", ["partial"] = "≈", ["failed"] = "✗", ["skipped"] = "⏭", ["cancelled"] = "×",
    };

    static bool Tty => !Console.IsOutputRedirected;
    static string B(string s) => Tty ? $"\u001b[1m{s}\u001b[0m" : s;
    static string Dim(string s) => Tty ? $"\u001b[2m{s}\u001b[0m" : s;
    static string C(string code, string s) => Tty ? $"\u001b[{code}m{s}\u001b[0m" : s;

    /// <param name="gui">Запуск графического интерфейса: (файлы, id инструмента) → код возврата. null — GUI недоступен.</param>
    public static int Run(string[] args, Func<string[], string, int> gui)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length > 0 && args[0] is "--version" or "-V") { Console.WriteLine($"{AppInfo.Name} {AppInfo.Version}"); return 0; }
        if (args.Length > 0 && args[0] is "--help" or "-h" or "help") { Help(); return 0; }
        var cmd = args.Length > 0 && Commands.Contains(args[0]) ? args[0] : "gui";
        var rest = cmd == "gui" && (args.Length == 0 || args[0] != "gui") ? args : args.Skip(1).ToArray();
        try
        {
            return cmd switch
            {
                "list" => List(),
                "options" => Options(rest),
                "run" => RunTool(rest),
                "deps" => Deps(rest),
                "hw" => HwCmd(rest),
                "waybar" => Waybar(rest),
                "status" => StatusCmd(),
                "models" => Models(rest),
                _ => Gui(rest, gui),
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Ошибка: {ex.Message}");
            return 2;
        }
    }

    static void Help()
    {
        Console.WriteLine($"""
            {AppInfo.Name} {AppInfo.Version} — пакетная обработка медиа (форк {AppInfo.UpstreamName} для Linux)

            Использование:
              hktools [gui] [ФАЙЛЫ…] [--tool ID]   графический интерфейс (по умолчанию)
              hktools list                        список инструментов
              hktools options ID                  настройки инструмента и их значения
              hktools run ID ВХОДЫ… [опции]       запуск без GUI
                  -s, --set KEY=VALUE             переопределить настройку (можно несколько раз)
                  -o, --out DIR                   папка результатов
                  -j, --parallel N                число параллельных задач
                  --overwrite                     перезаписывать существующие результаты
                  --tracks SPEC                   выбор дорожек: all | v,a,s | a:0,s:1 | 0,2,5
                  --replace IDX=FILE              замена потока (stream-replacement)
                  --format F                      формат yt-dlp (-f), для media-downloader
                  --audio-only                    только аудио (media-downloader)
                  --notify                        уведомление по завершении
              hktools deps [--install]            проверить внешние утилиты
              hktools hw [--refresh]              аппаратное кодирование (NVENC/VAAPI)
              hktools waybar [--once]             JSON-поток для custom-модуля Waybar
              hktools status                      текущий статус обработки (JSON)
              hktools models [--download MODEL]   модели whisper.cpp
            """);
    }

    // --- разбор аргументов ---
    sealed class Opts
    {
        public List<string> Positional = new();
        public Dictionary<string, List<string>> Values = new();
        public HashSet<string> Flags = new();
        public string Get(string k) => Values.TryGetValue(k, out var l) ? l[^1] : null;
        public List<string> All(string k) => Values.TryGetValue(k, out var l) ? l : new();
    }

    static Opts Parse(string[] args, Dictionary<string, string> withValue, HashSet<string> flags)
    {
        var o = new Opts();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--") { o.Positional.AddRange(args.Skip(i + 1)); break; }
            string key = a, val = null;
            if (a.StartsWith("--") && a.Contains('=')) { key = a[..a.IndexOf('=')]; val = a[(a.IndexOf('=') + 1)..]; }
            if (withValue.TryGetValue(key, out var canon))
            {
                if (val == null)
                {
                    if (i + 1 >= args.Length) throw new ArgumentException($"{key}: ожидается значение");
                    val = args[++i];
                }
                if (!o.Values.TryGetValue(canon, out var l)) o.Values[canon] = l = new();
                l.Add(val);
            }
            else if (flags.Contains(a)) o.Flags.Add(a.TrimStart('-'));
            else if (a.StartsWith('-') && a.Length > 1 && !File.Exists(a)) throw new ArgumentException($"неизвестный параметр {a}");
            else o.Positional.Add(a);
        }
        return o;
    }

    // --- команды ---
    static int List()
    {
        foreach (var cat in Categories.All)
        {
            var items = Registry.All.Where(s => s.Category == cat).ToList();
            if (items.Count == 0) continue;
            Console.WriteLine($"\n{B(cat)}");
            foreach (var s in items)
            {
                var missing = s.MissingDeps();
                var mark = missing.Count == 0 ? "" : "  " + C("33", $"(нет: {string.Join(", ", missing)})");
                Console.WriteLine($"  {s.Id,-22} {s.Name}{mark}");
                Console.WriteLine($"  {"",-22} {Dim(s.Description)}");
            }
        }
        Console.WriteLine("\nПример: hktools run audio-encode --set target_format=FLAC ~/Music/*.wav");
        return 0;
    }

    static int Options(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("укажите инструмент: hktools options ID");
        var script = Registry.Get(args[0]);
        if (script == null) { Console.Error.WriteLine($"Неизвестный инструмент: {args[0]}"); return 2; }
        var values = script.GetValues();
        Console.WriteLine($"{B(script.Name)} ({script.Id})\n");
        foreach (var f in Field.Walk(script.Fields(values)))
        {
            if (!f.HasValue) continue;
            var cur = values.Get(f.Key);
            string shown = f.Type switch
            {
                FieldType.Keywords => string.Join("|", KwItem.ActiveWords(cur)),
                FieldType.Check or FieldType.Expander => Json.ToBool(cur) ? "True" : "False",
                FieldType.Float => Json.ToDouble(cur).ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => Convert.ToString(cur, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            };
            var line = $"  {f.Key,-28} = {shown,-20} {f.Label}";
            if (f.Type == FieldType.Combo && f.Options != null) line += $"\n  {"",-28}   варианты: {string.Join(", ", f.Options)}";
            Console.WriteLine(line);
        }
        return 0;
    }

    static List<string> ExpandInputs(IEnumerable<string> inputs, Script script)
    {
        var outp = new List<string>();
        foreach (var raw in inputs)
        {
            if (script.InputMode == "urls") { outp.Add(raw); continue; }
            var p = Paths.ExpandUser(raw);
            if (Directory.Exists(p))
                outp.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)
                    .OrderBy(f => Path.GetDirectoryName(f), StringComparer.Ordinal).ThenBy(f => f, StringComparer.Ordinal).Where(script.Accepts));
            else if (File.Exists(p)) outp.Add(Path.GetFullPath(p));
            else Console.Error.WriteLine($"⚠ не найден: {p}");
        }
        return outp;
    }

    static readonly Dictionary<string, string> TypeByChar = new() { ["v"] = "video", ["a"] = "audio", ["s"] = "subtitle" };

    /// <summary>'all', 'v,a', 'a:0,s:1', '0,2,5' → индексы потоков.</summary>
    public static List<int> ParseTracks(string spec, MediaInfo info)
    {
        var res = new SortedSet<int>();
        foreach (var raw in spec.Split(','))
        {
            var part = raw.Trim();
            if (part == "") continue;
            if (part == "all") { foreach (var t in info.MediaTracks.Concat(info.Fonts)) res.Add(t.Index); }
            else if (TypeByChar.TryGetValue(part, out var kind)) { foreach (var t in info.OfType(kind)) res.Add(t.Index); }
            else if (part is "t" or "fonts") { foreach (var t in info.Fonts) res.Add(t.Index); }
            else if (part.Contains(':'))
            {
                var k = part[..part.IndexOf(':')];
                var n = part[(part.IndexOf(':') + 1)..];
                var lst = TypeByChar.TryGetValue(k, out var kk) ? info.OfType(kk) : new List<Track>();
                if (int.TryParse(n, out var ni) && ni >= 0 && ni < lst.Count) res.Add(lst[ni].Index);
            }
            else if (int.TryParse(part, out var idx)) res.Add(idx);
        }
        return res.ToList();
    }


    static int RunTool(string[] args)
    {
        var o = Parse(args,
            new() { ["--set"] = "set", ["-s"] = "set", ["--out"] = "out", ["-o"] = "out", ["--parallel"] = "parallel", ["-j"] = "parallel",
                ["--tracks"] = "tracks", ["--replace"] = "replace", ["--format"] = "format" },
            ["--overwrite", "--audio-only", "--notify"]);
        if (o.Positional.Count == 0) throw new ArgumentException("укажите инструмент: hktools run ID ВХОДЫ…");
        var script = Registry.Get(o.Positional[0]);
        if (script == null) { Console.Error.WriteLine($"Неизвестный инструмент: {o.Positional[0]}. Список: hktools list"); return 2; }
        if (script.InputMode == "none") { Console.Error.WriteLine("Этот инструмент работает только в графическом интерфейсе."); return 2; }
        if (o.Positional.Count < 2) throw new ArgumentException("не указаны входные файлы, папки или ссылки");
        var missing = script.MissingDeps();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"Не хватает утилит: {string.Join(", ", missing)}\nУстановка: {Tools.InstallCommand(missing)}");
            return 3;
        }
        var overrides = new Dictionary<string, string>();
        foreach (var kv in o.All("set"))
        {
            var eq = kv.IndexOf('=');
            if (eq < 0) { Console.Error.WriteLine($"Ожидается key=value: {kv}"); return 2; }
            overrides[kv[..eq].Trim()] = kv[(eq + 1)..];
        }
        var values = script.GetValues(overrides);
        if (o.Flags.Contains("overwrite")) Settings.Instance.SetTransient("overwrite_existing", true); // только для этого запуска
        var inputs = ExpandInputs(o.Positional.Skip(1), script);
        if (inputs.Count == 0) { Console.Error.WriteLine("Нет подходящих файлов."); return 1; }
        var items = inputs.Select(p => new QueueItem(p)).ToList();
        if (script.TrackSelection != null)
            foreach (var it in items)
            {
                try { it.Info = Probe.Run(it.Path); }
                catch (Exception ex) { Console.Error.WriteLine($"⚠ {it.Name}: {ex.Message}"); continue; }
                it.Selected = o.Get("tracks") is { } spec ? ParseTracks(spec, it.Info) : TrackSel.DefaultSelection(script, it.Info);
                if (script.TrackSelection == "replace" && o.All("replace").Count > 0)
                {
                    var reps = new Dictionary<int, string>();
                    foreach (var r in o.All("replace"))
                    {
                        var eq = r.IndexOf('=');
                        if (eq < 0 || !int.TryParse(r[..eq], out var idx)) throw new ArgumentException($"--replace: ожидается IDX=FILE, получено {r}");
                        reps[idx] = Path.GetFullPath(Paths.ExpandUser(r[(eq + 1)..]));
                    }
                    it.Replacements = reps;
                }
            }
        if (script.Id == "media-downloader")
            foreach (var it in items)
            {
                if (o.Get("format") is { } f) it.Extra["format_arg"] = f;
                it.Extra["audio_only"] = o.Flags.Contains("audio-only");
                if (o.Flags.Contains("audio-only") && o.Get("format") == null) it.Extra["format_arg"] = "ba/b";
            }
        int? parallel = o.Get("parallel") is { } pj ? int.Parse(pj) : null;
        var runner = new BatchRunner(script, items, values, o.Get("out") is { } od ? Path.GetFullPath(Paths.ExpandUser(od)) : null, parallel);
        var outLock = new object();
        runner.OnItem = it =>
        {
            if (it.Status != "running" || !Tty) return;
            var line = $"[{it.Progress,5:0.0}%] {it.Name}: {it.Message}".Replace('\n', ' ');
            int w = 120;
            try { w = Console.WindowWidth; } catch { }
            lock (outLock) Console.Write("\r\u001b[K" + (line.Length > w - 1 ? line[..Math.Max(0, w - 1)] : line));
        };
        runner.OnResult = (it, res) =>
        {
            lock (outLock)
            {
                if (Tty) Console.Write("\r\u001b[K");
                Console.WriteLine($"{StatusIcon.GetValueOrDefault(res.Status, "?")} {it.Name}");
                foreach (var m in res.Messages)
                    foreach (var ln in m.Split('\n')) Console.WriteLine($"    {ln}");
            }
        };
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nОтмена…");
            runner.Cancel();
        };
        var summary = runner.RunBlocking();
        Console.WriteLine($"\nГотово за {summary.Elapsed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} с: успешно {summary.Ok}, ошибок {summary.Failed}, пропущено {summary.Skipped}");
        if (o.Flags.Contains("notify")) Desktop.Notify($"{AppInfo.Name}: {script.Name}", $"Успешно {summary.Ok}, ошибок {summary.Failed}");
        Settings.Instance.SaveNow();
        return summary.Failed == 0 && !summary.Cancelled ? 0 : 1;
    }

    static int Deps(string[] args)
    {
        var missing = new List<string>();
        foreach (var (key, tool) in Tools.All)
        {
            var st = Tools.ProbeVersion(key);
            var mark = st.Found ? C("32", "✓") : tool.Required ? C("31", "✗") : C("33", "–");
            var pkg = tool.Package + (tool.Aur ? " (AUR)" : "");
            Console.WriteLine($" {mark} {tool.Title,-34} {(st.Found ? st.Version : "не найден"),-28} пакет: {pkg}");
            if (!st.Found) missing.Add(key);
        }
        if (missing.Count > 0)
        {
            var cmd = Tools.InstallCommand(missing);
            Console.WriteLine($"\nУстановить недостающее:\n  {cmd}");
            if (args.Contains("--install"))
            {
                var p = System.Diagnostics.Process.Start("/bin/sh", ["-c", cmd]);
                p.WaitForExit();
                return p.ExitCode;
            }
        }
        return 0;
    }

    static int HwCmd(string[] args)
    {
        var caps = Hw.Caps(args.Contains("--refresh"));
        string On(Dictionary<string, bool> d, string none) => d.Any(kv => kv.Value) ? string.Join(", ", d.Where(kv => kv.Value).Select(kv => kv.Key)) : none;
        Console.WriteLine($"FFmpeg: {caps.FfmpegVersion}");
        Console.WriteLine($"GPU: {(caps.Gpus.Count > 0 ? string.Join(", ", caps.Gpus) : "не найдено render-устройств")}");
        Console.WriteLine($"NVENC: {On(caps.Nvenc, "недоступен")}");
        Console.WriteLine($"VAAPI ({(caps.VaapiDevice != "" ? caps.VaapiDevice : "—")}): {On(caps.Vaapi, "недоступен")}");
        Console.WriteLine($"CPU: {string.Join(", ", new[] { "libx265", "libx264", "libsvtav1" }.Where(caps.HasEncoder))}");
        return 0;
    }

    /// <summary>Непрерывный вывод JSON для custom-модуля Waybar (return-type: json).</summary>
    static int Waybar(string[] args)
    {
        bool once = args.Contains("--once");
        var idle = Json.Serialize(new Dictionary<string, object> { ["text"] = "", ["class"] = "idle", ["tooltip"] = "hktools: нет задач" });
        string last = null;
        while (true)
        {
            string raw;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Paths.StatusFile));
                var root = doc.RootElement;
                var cls = root.TryGetProperty("class", out var c) ? c.GetString() : "";
                var ts = root.TryGetProperty("ts", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : 0;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                if (cls is "done" or "failed" or "cancelled" && now - ts > 20) raw = idle;
                else
                {
                    var d = new Dictionary<string, object>();
                    foreach (var k in new[] { "text", "alt", "class", "tooltip", "percentage" })
                        if (root.TryGetProperty(k, out var v)) d[k] = Json.FromElement(v);
                    raw = Json.Serialize(d);
                }
            }
            catch { raw = idle; }
            if (raw != last) { Console.WriteLine(raw); Console.Out.Flush(); last = raw; }
            if (once) return 0;
            Thread.Sleep(1000);
        }
    }

    static int StatusCmd()
    {
        try { Console.WriteLine(File.ReadAllText(Paths.StatusFile)); }
        catch { Console.WriteLine("{}"); }
        return 0;
    }

    static int Models(string[] args)
    {
        var o = Parse(args, new() { ["--download"] = "download" }, []);
        var key = o.Get("download");
        if (key == null)
        {
            var have = SpeechRecognition.Downloaded().ToHashSet();
            Console.WriteLine($"Папка моделей: {SpeechRecognition.ModelsDir()}");
            foreach (var m in SpeechRecognition.Models)
                Console.WriteLine($" [{(have.Contains(m.Key) ? "✓" : " ")}] {m.Key,-22} ~{m.SizeMb,5:0} МБ  {m.Description}");
            return 0;
        }
        var token = new CancelToken();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; token.Cancel(); };
        try
        {
            var path = SpeechRecognition.Download(key, (p, m) => Console.Write($"\r{key}: {p,5:0.0}% {m}   "), token);
            Console.WriteLine($"\nСохранено: {path}");
            return 0;
        }
        catch (CancelledException) { Console.WriteLine(); return 130; }
        catch (Exception ex) { Console.Error.WriteLine($"\nОшибка загрузки: {ex.Message}"); return 1; }
    }

    static int Gui(string[] args, Func<string[], string, int> gui)
    {
        var o = Parse(args, new() { ["--tool"] = "tool", ["-t"] = "tool" }, []);
        if (gui == null)
        {
            Console.Error.WriteLine("Графический интерфейс недоступен в этой сборке. Справка: hktools --help");
            return 2;
        }
        return gui(o.Positional.ToArray(), o.Get("tool"));
    }
}
