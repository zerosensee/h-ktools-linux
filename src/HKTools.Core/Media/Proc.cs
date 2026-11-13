using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HKTools.Media;

public sealed class CancelledException : Exception
{
    public CancelledException() : base("Отменено пользователем") { }
}

/// <summary>Флаг отмены, который сразу завершает связанные процессы.</summary>
public sealed class CancelToken
{
    readonly object _lock = new();
    readonly HashSet<Process> _procs = new();
    volatile bool _cancelled;

    public bool Cancelled => _cancelled;

    public void Cancel()
    {
        _cancelled = true;
        List<Process> ps;
        lock (_lock) ps = _procs.ToList();
        foreach (var p in ps) Proc.Terminate(p);
    }

    public void Check() { if (_cancelled) throw new CancelledException(); }

    internal void Attach(Process p) { lock (_lock) _procs.Add(p); if (_cancelled) Proc.Terminate(p); }
    internal void Detach(Process p) { lock (_lock) _procs.Remove(p); }
}

public sealed class ProcResult
{
    public int ExitCode;
    public string Stdout = "";
    public List<string> StderrTail = new();
    public bool Cancelled;
    public bool Ok => ExitCode == 0 && !Cancelled;
    public string ErrorText(int lines = 12) => string.Join("\n", StderrTail.TakeLast(lines));
}

/// <summary>Запуск внешних процессов (аналог ActiveProcessTracker): отмена, построчный вывод, гарантированное завершение.</summary>
public static class Proc
{
    static readonly object TrackLock = new();
    static readonly HashSet<Process> Tracked = new();

    [DllImport("libc", SetLastError = true)]
    static extern int kill(int pid, int sig);

    static Proc()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAll();
    }

    public static void KillAll()
    {
        List<Process> ps;
        lock (TrackLock) ps = Tracked.ToList();
        foreach (var p in ps) Terminate(p, 1500);
    }

    public static void Terminate(Process p, int graceMs = 3000)
    {
        try
        {
            if (p.HasExited) return;
            try { kill(p.Id, 15); } catch { /* нет libc — сразу SIGKILL */ }
            if (!p.WaitForExit(graceMs)) p.Kill(entireProcessTree: true);
        }
        catch { }
    }

    public static string Quote(string a) =>
        a.Length == 0 || a.IndexOfAny(" \t'\"$`\\|&;<>()*?[]{}".ToCharArray()) >= 0 ? "'" + a.Replace("'", "'\\''") + "'" : a;

    public static string Join(IEnumerable<string> cmd) => string.Join(" ", cmd.Select(Quote));

    /// <summary>Без UTF-8-локали mkvmerge и др. портят не-ASCII имена (аналог PEP 538 в CPython).</summary>
    static readonly bool NeedUtf8Locale = CheckLocale();

    static bool CheckLocale()
    {
        var loc = new[] { "LC_ALL", "LC_CTYPE", "LANG" }.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        return loc == null || !(loc.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) || loc.Contains("utf8", StringComparison.OrdinalIgnoreCase));
    }

    static ProcessStartInfo Psi(IReadOnlyList<string> cmd, string cwd, bool stdin, IDictionary<string, string> env = null)
    {
        var psi = new ProcessStartInfo(cmd[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in cmd.Skip(1)) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(cwd)) psi.WorkingDirectory = cwd;
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        if (NeedUtf8Locale)
        {
            psi.Environment["LC_CTYPE"] = "C.UTF-8";
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LC_ALL"))) psi.Environment["LC_ALL"] = "C.UTF-8";
        }
        return psi;
    }

    /// <summary>Читает поток побайтно и режет на строки по \n (и \r, если splitCr).</summary>
    static Task ReadLines(Stream s, bool splitCr, Action<string> onLine)
    {
        return Task.Run(() =>
        {
            var buf = new byte[65536];
            var acc = new MemoryStream();
            void Flush()
            {
                if (acc.Length == 0) return;
                var line = Encoding.UTF8.GetString(acc.GetBuffer(), 0, (int)acc.Length);
                acc.SetLength(0);
                if (line.Trim().Length > 0) onLine(line);
            }
            try
            {
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        var b = buf[i];
                        if (b == (byte)'\n' || (splitCr && b == (byte)'\r')) Flush();
                        else acc.WriteByte(b);
                    }
                }
            }
            catch { }
            Flush();
        });
    }

    public static ProcResult Run(IReadOnlyList<string> cmd, CancelToken token = null, Action<string> onStdout = null,
        Action<string> onStderr = null, bool captureStdout = false, string cwd = null, bool splitCr = false,
        int tail = 60, string source = "Процесс", IDictionary<string, string> env = null)
    {
        if (token?.Cancelled == true) return new ProcResult { ExitCode = -1, Cancelled = true };
        Log.Debug("$ " + Join(cmd), source);
        Process p;
        try { p = Process.Start(Psi(cmd, cwd, false, env)); }
        catch (Exception ex) { return new ProcResult { ExitCode = 127, StderrTail = { $"Не удалось запустить {cmd[0]}: {ex.Message}" } }; }
        lock (TrackLock) Tracked.Add(p);
        token?.Attach(p);
        var outSb = new StringBuilder();
        var errTail = new Queue<string>();
        var tOut = ReadLines(p.StandardOutput.BaseStream, splitCr, line =>
        {
            if (captureStdout) lock (outSb) outSb.Append(line).Append('\n');
            try { onStdout?.Invoke(line); } catch { }
        });
        var tErr = ReadLines(p.StandardError.BaseStream, splitCr, line =>
        {
            lock (errTail) { errTail.Enqueue(line); while (errTail.Count > tail) errTail.Dequeue(); }
            try { onStderr?.Invoke(line); } catch { }
        });
        p.WaitForExit();
        Task.WaitAll(new[] { tOut, tErr }, 5000);
        lock (TrackLock) Tracked.Remove(p);
        token?.Detach(p);
        var res = new ProcResult
        {
            ExitCode = p.ExitCode,
            Stdout = outSb.ToString(),
            StderrTail = errTail.ToList(),
            Cancelled = token?.Cancelled == true,
        };
        p.Dispose();
        return res;
    }

    /// <summary>producer | consumer (ffmpeg → fdkaac/qaac).</summary>
    public static ProcResult Pipeline(IReadOnlyList<string> producer, IReadOnlyList<string> consumer, CancelToken token,
        Action<string> onProducerStderr = null, string cwd = null, string source = "Конвейер")
    {
        if (token?.Cancelled == true) return new ProcResult { ExitCode = -1, Cancelled = true };
        Log.Debug("$ " + Join(producer) + " | " + Join(consumer), source);
        Process p1, p2;
        try { p1 = Process.Start(Psi(producer, cwd, false)); }
        catch (Exception ex) { return new ProcResult { ExitCode = 127, StderrTail = { $"Не удалось запустить {producer[0]}: {ex.Message}" } }; }
        try { p2 = Process.Start(Psi(consumer, cwd, true)); }
        catch (Exception ex)
        {
            Terminate(p1);
            return new ProcResult { ExitCode = 127, StderrTail = { $"Не удалось запустить {consumer[0]}: {ex.Message}" } };
        }
        foreach (var p in new[] { p1, p2 }) { lock (TrackLock) Tracked.Add(p); token?.Attach(p); }
        var tail = new Queue<string>();
        void Add(string l) { lock (tail) { tail.Enqueue(l); while (tail.Count > 60) tail.Dequeue(); } }
        var pump = Task.Run(() =>
        {
            try { p1.StandardOutput.BaseStream.CopyTo(p2.StandardInput.BaseStream, 1 << 20); }
            catch { }
            try { p2.StandardInput.Close(); } catch { }
        });
        var t1 = ReadLines(p1.StandardError.BaseStream, true, l => { Add(l); try { onProducerStderr?.Invoke(l); } catch { } });
        var t2 = ReadLines(p2.StandardOutput.BaseStream, true, Add);
        var t3 = ReadLines(p2.StandardError.BaseStream, true, Add);
        p2.WaitForExit();
        if (!p1.WaitForExit(5000)) Terminate(p1);
        Task.WaitAll(new[] { pump, t1, t2, t3 }, 5000);
        foreach (var p in new[] { p1, p2 }) { lock (TrackLock) Tracked.Remove(p); token?.Detach(p); }
        int rc = p2.ExitCode != 0 ? p2.ExitCode : (p1.ExitCode is 0 or 141 ? 0 : p1.ExitCode);
        var res = new ProcResult { ExitCode = rc, StderrTail = tail.ToList(), Cancelled = token?.Cancelled == true };
        p1.Dispose(); p2.Dispose();
        return res;
    }

    /// <summary>Короткий запуск с захватом вывода.</summary>
    public static (int Code, string Out, string Err) Capture(IReadOnlyList<string> cmd, int timeoutMs = 60000)
    {
        try
        {
            using var p = Process.Start(Psi(cmd, null, false));
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { Terminate(p, 500); return (-9, "", "timeout"); }
            return (p.ExitCode, o.Result, e.Result);
        }
        catch (Exception ex) { return (127, "", ex.Message); }
    }
}
