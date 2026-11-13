using System.Text.RegularExpressions;
using HKTools.Media;

namespace HKTools.Core;

public static class Exts
{
    public static readonly HashSet<string> VideoContainers = new() { ".mkv", ".mp4", ".mov", ".webm", ".avi", ".m2ts", ".mts", ".ts", ".m4s", ".m4v", ".wmv", ".flv" };
    public static readonly HashSet<string> VideoStreams = new() { ".hevc", ".h264", ".h265", ".264", ".265", ".vc1", ".m2v", ".avc", ".ivf" };
    public static readonly HashSet<string> AudioContainers = new() { ".mka", ".m4a" };
    public static readonly HashSet<string> AudioStreams = new()
    {
        ".mp3", ".flac", ".wav", ".ogg", ".oga", ".wma", ".aiff", ".aif", ".alac", ".ape", ".opus", ".ac3", ".eac3", ".ec3", ".dts",
        ".wv", ".aac", ".thd", ".truehd", ".mlp", ".dtshd", ".pcm", ".mp2", ".m2a", ".w64", ".caf", ".tta",
    };
    public static readonly HashSet<string> Subtitles = new() { ".srt", ".ass", ".ssa", ".sub", ".vtt", ".idx", ".sup" };
    public static readonly HashSet<string> AudioAll = new(AudioContainers.Concat(AudioStreams));
    public static readonly HashSet<string> AllMedia = new(VideoContainers.Concat(VideoStreams).Concat(AudioAll));

    public static HashSet<string> Union(params IEnumerable<string>[] sets) => new(sets.SelectMany(s => s));
}

public static class Categories
{
    public static readonly string[] All = ["Видео", "Аудио", "Контейнеры", "Субтитры", "Сеть", "Инструменты"];
    public static readonly Dictionary<string, string> Css = new()
    {
        ["Видео"] = "kt-cat-video", ["Аудио"] = "kt-cat-audio", ["Контейнеры"] = "kt-cat-containers",
        ["Субтитры"] = "kt-cat-subtitles", ["Сеть"] = "kt-cat-network", ["Инструменты"] = "kt-cat-tools",
    };
}

public sealed class QueueItem
{
    static int _ids;
    public readonly int Id = Interlocked.Increment(ref _ids);
    public string Path;
    public Dictionary<string, object> Extra = new();
    public volatile string Status = "pending"; // pending | running | done | partial | failed | skipped | cancelled
    public double Progress;
    public string Message = "";
    public List<string> Outputs = new();
    public MediaInfo Info;
    public string Badge = "";

    public QueueItem(string path) { Path = path; }

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('/')) is var n && n != "" ? n : Path;
    public string Ext => System.IO.Path.GetExtension(Path).ToLowerInvariant();
    public string Stem => System.IO.Path.GetFileNameWithoutExtension(Path);

    public void Reset() { Status = "pending"; Progress = 0; Message = ""; Outputs = new(); }

    public List<int> Selected
    {
        get => Extra.GetValueOrDefault("selected") switch
        {
            List<int> l => l.OrderBy(x => x).ToList(),
            System.Collections.IEnumerable e and not string => e.Cast<object>().Select(o => (int)Json.ToLong(o)).OrderBy(x => x).ToList(),
            _ => new List<int>(),
        };
        set => Extra["selected"] = value;
    }

    /// <summary>Замены потоков: индекс дорожки → внешний файл.</summary>
    public Dictionary<int, string> Replacements
    {
        get
        {
            var d = new Dictionary<int, string>();
            if (Extra.GetValueOrDefault("replacements") is System.Collections.IDictionary raw)
                foreach (System.Collections.DictionaryEntry kv in raw)
                    if (kv.Value?.ToString() is { Length: > 0 } v) d[(int)Json.ToLong(kv.Key)] = v;
            return d;
        }
        set => Extra["replacements"] = value;
    }
}

public sealed class Result
{
    public string Status = "done";
    public List<string> Messages = new();
    public List<string> Outputs = new();
    public object Data;

    public static Result Done(string msg = "", params string[] outputs) =>
        new() { Messages = msg != "" ? [msg] : [], Outputs = outputs.Where(o => !string.IsNullOrEmpty(o)).ToList() };
    public static Result Failed(string msg) => new() { Status = "failed", Messages = [msg] };
    public static Result Skipped(string msg) => new() { Status = "skipped", Messages = [msg] };
    public static Result CancelledResult() => new() { Status = "cancelled", Messages = ["Отменено пользователем"] };
}

/// <summary>Общее состояние пакета: резерв путей, счётчик переименования, вся очередь.</summary>
public sealed class Batch
{
    public readonly List<QueueItem> Items;
    readonly object _lock = new();
    readonly HashSet<string> _reserved = new();
    int _counter;

    public Batch(List<QueueItem> items) { Items = items; }
    public int NextNumber() { lock (_lock) return ++_counter; }

    public string Reserve(string path)
    {
        lock (_lock)
        {
            var dir = System.IO.Path.GetDirectoryName(path) ?? "";
            var stem = System.IO.Path.GetFileNameWithoutExtension(path);
            var ext = System.IO.Path.GetExtension(path);
            var cand = path;
            for (int n = 1; _reserved.Contains(cand); n++) cand = System.IO.Path.Combine(dir, $"{stem}_{n}{ext}");
            _reserved.Add(cand);
            return cand;
        }
    }
}

public static class Rename
{
    /// <summary>Пакетное переименование результата (аналог PowerRename).</summary>
    public static string Apply(string stem, int number, IDictionary<string, object> cfg = null)
    {
        cfg ??= new Dictionary<string, object>();
        string pattern, replacement;
        bool enabled, useRegex, caseSensitive;
        if (cfg.Bool("local_rename_override"))
        {
            pattern = cfg.Str("local_rename_search");
            replacement = cfg.Str("local_rename_replace");
            useRegex = cfg.Bool("local_rename_use_regex", true);
            caseSensitive = cfg.Bool("local_rename_case_sensitive");
            enabled = pattern != "";
        }
        else
        {
            var s = Settings.Instance;
            enabled = s.GetBool("rename_enabled");
            pattern = s.GetStr("rename_search");
            replacement = s.GetStr("rename_replace");
            useRegex = s.GetBool("rename_use_regex");
            caseSensitive = s.GetBool("rename_case_sensitive");
        }
        if (!enabled || pattern == "") return stem;
        var now = DateTime.Now;
        var repl = Regex.Replace(replacement, @"\$\{num:(\d+)\}", m => number.ToString().PadLeft(int.Parse(m.Groups[1].Value), '0'));
        foreach (var (tok, val) in new[] {
                     ("${num}", number.ToString()), ("${ruuidv4}", Guid.NewGuid().ToString()), ("${YYYY}", now.ToString("yyyy")),
                     ("${MM}", now.ToString("MM")), ("${DD}", now.ToString("dd")), ("${hh}", now.ToString("HH")),
                     ("${mm}", now.ToString("mm")), ("${ss}", now.ToString("ss")) })
            repl = repl.Replace(tok, val);
        var opts = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        try
        {
            string res = useRegex
                ? Regex.Replace(stem, pattern, repl, opts) // .NET понимает $1 и ${name} сам
                : Regex.Replace(stem, Regex.Escape(pattern), repl.Replace("$", "$$"), opts);
            return res == "" ? stem : res;
        }
        catch (ArgumentException ex)
        {
            Log.Warning($"Ошибка в шаблоне переименования: {ex.Message}", "Переименование");
            return stem;
        }
    }
}

/// <summary>Всё, что нужно инструменту для обработки одного элемента очереди.</summary>
public sealed class Context
{
    public readonly Script Script;
    public readonly QueueItem Item;
    public readonly Dictionary<string, object> Values;
    public readonly Batch Batch;
    public readonly CancelToken Token;
    public readonly string OutDir;
    public readonly Func<string, object[], object> Ask; // интерактивный вопрос из UI (null в CLI)
    public readonly bool Overwrite;
    public int Index, Total;
    readonly Action<QueueItem, double, string> _onProgress;
    readonly List<string> _temps = new();

    public Context(Script script, QueueItem item, Dictionary<string, object> values, Batch batch, CancelToken token,
        string outDir, Action<QueueItem, double, string> onProgress, Func<string, object[], object> ask)
    {
        Script = script; Item = item; Values = values; Batch = batch; Token = token;
        OutDir = string.IsNullOrWhiteSpace(outDir) ? null : outDir;
        _onProgress = onProgress; Ask = ask;
        Overwrite = Settings.Instance.GetBool("overwrite_existing");
    }

    public string Str(string k, string d = "") => Values.Str(k, d);
    public bool Bool(string k, bool d = false) => Values.Bool(k, d);
    public int Int(string k, int d = 0) => Values.Int(k, d);
    public double Dbl(string k, double d = 0) => Values.Dbl(k, d);
    public List<string> Words(string k, bool lower = false) => Values.Words(k, lower);

    public void Progress(double pct, string msg = "") => _onProgress(Item, Math.Clamp(pct, 0, 100), msg);
    public void Log(string level, string msg, string details = "") => HKTools.Log.Write(level, msg, Script.Name, details);
    public void Check() => Token.Check();

    public string TempDir(string prefix = "hk_")
    {
        var d = Paths.TempDir(prefix);
        _temps.Add(d);
        return d;
    }

    public void Cleanup()
    {
        foreach (var d in _temps) try { Directory.Delete(d, true); } catch { }
        _temps.Clear();
    }

    public MediaInfo ProbeItem(string path = null)
    {
        if (path != null && path != Item.Path) return Probe.Run(path);
        if (Item.Info != null) return Item.Info;
        return Item.Info = Probe.Run(Item.Path);
    }

    public string TargetDir(string input = null)
    {
        var src = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(input ?? Item.Path)) ?? ".";
        string d = OutDir ?? (Settings.Instance.GetBool("use_auto_subfolder")
            ? System.IO.Path.Combine(src, Settings.Instance.GetStr("output_subfolder") is var n && n != "" ? n : "HKTools_Result")
            : src);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Безопасный путь результата (аналог GetSafeOutputPath).</summary>
    public string OutputPath(string input = null, string ext = null, string suffix = "", string stem = null, string directory = null)
    {
        input ??= Item.Path;
        var inAbs = System.IO.Path.GetFullPath(input);
        var baseStem = stem ?? System.IO.Path.GetFileNameWithoutExtension(input);
        ext ??= System.IO.Path.GetExtension(input);
        if (ext != "" && !ext.StartsWith('.')) ext = "." + ext;
        directory ??= TargetDir(input);
        var newStem = Rename.Apply(baseStem, Batch.NextNumber(), Values) + suffix;
        var outp = System.IO.Path.Combine(directory, newStem + ext);
        if (System.IO.Path.GetFullPath(outp) == inAbs) outp = System.IO.Path.Combine(directory, $"{newStem}_processed{ext}");
        return Batch.Reserve(outp);
    }

    public Result SkipIfExists(string path)
    {
        if (File.Exists(path) && !Overwrite)
        {
            Progress(100, "Пропущен: файл уже существует");
            return Result.Skipped($"⏭ ПРОПУСК (файл существует): {System.IO.Path.GetFileName(path)}");
        }
        return null;
    }

    public ProcResult FFmpeg(IEnumerable<string> args, double duration = 0, string label = "Обработка", string cwd = null,
        double @base = 0, double span = 100)
    {
        var res = Media.FFmpeg.Run(args, Token, duration, p => Progress(@base + p.Percent * span / 100, p.Label(label)), cwd: cwd, source: Script.Name);
        if (res.Cancelled) throw new CancelledException();
        return res;
    }

    /// <summary>Опции «заменить исходник» / «удалить исходник». Возвращает итоговый путь.</summary>
    public string FinalizeSource(string source, string result, Result r)
    {
        if (Bool("overwrite_source") && OutDir == null) return ReplaceSource(source, result, r);
        if (Bool("delete_source") || Bool("delete_original") || Bool("DeleteOriginal"))
            r.Messages.Add(Desktop.Trash(source)
                ? $"🗑 Исходный файл перемещён в корзину: {System.IO.Path.GetFileName(source)}"
                : $"⚠ Не удалось удалить исходный файл: {System.IO.Path.GetFileName(source)}");
        return result;
    }

    public string ReplaceSource(string source, string result, Result r)
    {
        try
        {
            if (!File.Exists(result) || new FileInfo(result).Length == 0) throw new IOException("результат пустой");
            Probe.Run(result, useCache: false);
            var target = System.IO.Path.ChangeExtension(source, System.IO.Path.GetExtension(result));
            File.Move(result, target, true);
            if (System.IO.Path.GetFullPath(target) != System.IO.Path.GetFullPath(source) && File.Exists(source)) File.Delete(source);
            r.Messages.Add($"♻ Исходный файл заменён результатом: {System.IO.Path.GetFileName(target)}");
            try { Directory.Delete(System.IO.Path.GetDirectoryName(result)!); } catch { }
            return target;
        }
        catch (Exception ex)
        {
            r.Status = "partial";
            r.Messages.Add($"❌ Исходник не заменён ({ex.Message}), результат сохранён: {System.IO.Path.GetFileName(result)}");
            return result;
        }
    }

    public static void RemoveQuiet(string path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) try { File.Delete(path); } catch { }
    }

    /// <summary>Типовая обработка результата ffmpeg.</summary>
    public Result FinishFFmpeg(ProcResult res, string source, string output, string okMsg)
    {
        if (res.Ok && File.Exists(output) && new FileInfo(output).Length > 0)
        {
            var r = Result.Done(okMsg);
            r.Outputs = [FinalizeSource(source, output, r)];
            Progress(100, "Готово");
            return r;
        }
        RemoveQuiet(output);
        return Result.Failed($"❌ Ошибка: {System.IO.Path.GetFileName(source)}\n{res.ErrorText()}");
    }
}

/// <summary>Базовый класс инструмента.</summary>
public abstract class Script
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual string Category => "Инструменты";
    public virtual string Icon => "applications-multimedia-symbolic";
    public virtual HashSet<string> Extensions => null;  // null = любые файлы
    public virtual string InputMode => "files";          // files | urls | none
    public virtual string[] Deps => ["ffmpeg", "ffprobe"];
    public virtual string TrackSelection => null;        // null | extract | manage | replace

    public virtual List<Field> Fields(IDictionary<string, object> values = null) => new();

    public bool Accepts(string path) => Extensions == null || Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Значения: дефолты схемы ← сохранённые ← переопределения (CLI).</summary>
    public Dictionary<string, object> GetValues(IDictionary<string, string> overrides = null)
    {
        var vals = Field.Defaults(Fields());
        foreach (var kv in Settings.Instance.ScriptValues(Id)) vals[kv.Key] = kv.Value;
        if (overrides != null)
        {
            var all = Field.Walk(Fields(vals)).GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.First());
            foreach (var kv in overrides) vals[kv.Key] = all.TryGetValue(kv.Key, out var f) ? f.Coerce(kv.Value) : kv.Value;
        }
        return vals;
    }

    public virtual List<QueueItem> PrepareBatch(List<QueueItem> items, Dictionary<string, object> values) =>
        items.Where(i => InputMode != "files" || Accepts(i.Path)).ToList();

    public abstract Result Process(QueueItem item, Context ctx);

    public List<string> MissingDeps() => Deps.Where(d => Tools.Which(d) == null).ToList();
    public virtual string SkipReason(QueueItem item) => "Не подходит для этого инструмента";
    public virtual string RowSubtitle(QueueItem item, List<QueueItem> all) => "";
}
