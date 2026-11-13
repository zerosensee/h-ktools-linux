namespace HKTools;

public sealed record LogEntry(DateTime Time, string Level, string Source, string Message, string Details, long Seq)
{
    public string TimeStr => Time.ToString("HH:mm:ss");
}

/// <summary>Журнал: файл с ротацией + кольцевой буфер для окна журнала.</summary>
public static class Log
{
    static readonly object Lock = new();
    static readonly LinkedList<LogEntry> Entries = new();
    static readonly List<Action<LogEntry>> Listeners = new();
    static long _seq;
    const int Capacity = 5000;
    static string _file;

    static string FilePath
    {
        get
        {
            if (_file != null) return _file;
            try { _file = System.IO.Path.Combine(Paths.StateDir, "hktools.log"); }
            catch { _file = ""; }
            return _file;
        }
    }

    public static void AddListener(Action<LogEntry> cb) { lock (Lock) Listeners.Add(cb); }
    public static void RemoveListener(Action<LogEntry> cb) { lock (Lock) Listeners.Remove(cb); }
    public static List<LogEntry> All() { lock (Lock) return Entries.ToList(); }
    public static void Clear() { lock (Lock) Entries.Clear(); }

    public static void Write(string level, string message, string source = "hktools", string details = "")
    {
        level = level.ToUpperInvariant();
        LogEntry e;
        List<Action<LogEntry>> ls;
        lock (Lock)
        {
            e = new LogEntry(DateTime.Now, level, source, message, details ?? "", ++_seq);
            Entries.AddLast(e);
            while (Entries.Count > Capacity) Entries.RemoveFirst();
            ls = Listeners.ToList();
            WriteFile(e);
        }
        foreach (var cb in ls)
        {
            try { cb(e); } catch { /* слушатель не должен ронять журнал */ }
        }
    }

    static void WriteFile(LogEntry e)
    {
        var path = FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 2_000_000)
            {
                for (int i = 4; i >= 1; i--)
                {
                    var src = $"{path}.{i}";
                    if (File.Exists(src)) File.Move(src, $"{path}.{i + 1}", true);
                }
                File.Move(path, path + ".1", true);
            }
            File.AppendAllText(path, $"{e.Time:yyyy-MM-dd HH:mm:ss} [{e.Level}] [{e.Source}] {e.Message}" +
                                     (string.IsNullOrEmpty(e.Details) ? "" : "\n" + e.Details) + "\n");
        }
        catch { }
    }

    public static void Debug(string m, string s = "hktools", string d = "") => Write("DEBUG", m, s, d);
    public static void Info(string m, string s = "hktools", string d = "") => Write("INFO", m, s, d);
    public static void Success(string m, string s = "hktools", string d = "") => Write("SUCCESS", m, s, d);
    public static void Warning(string m, string s = "hktools", string d = "") => Write("WARNING", m, s, d);
    public static void Error(string m, string s = "hktools", string d = "") => Write("ERROR", m, s, d);
}
