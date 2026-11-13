using System.Diagnostics;
using HKTools.Media;

namespace HKTools.Core;

public sealed class BatchSummary
{
    public int Total, Done, Partial, Failed, Skipped;
    public bool Cancelled;
    public double Elapsed;
    public Dictionary<int, Result> Results = new();
    public int Ok => Done + Partial;
}

/// <summary>Выполняет Script.Process для каждого элемента в рабочих потоках. Колбэки — из рабочих потоков.</summary>
public sealed class BatchRunner
{
    public readonly Script Script;
    public readonly List<QueueItem> AllItems;
    public readonly Dictionary<string, object> Values;
    public readonly string OutDir;
    public readonly CancelToken Token = new();
    public readonly BatchSummary Summary = new();
    public readonly int Parallel;

    public Action<QueueItem> OnItem;
    public Action<double, string> OnOverall;
    public Action<QueueItem, Result> OnResult;
    public Action<BatchSummary> OnFinish;
    public Func<string, object[], object> Ask;

    readonly object _lock = new();
    readonly Dictionary<int, double> _progress = new();
    Thread _thread;

    public BatchRunner(Script script, List<QueueItem> items, Dictionary<string, object> values, string outDir = null, int? parallel = null)
    {
        Script = script; AllItems = items; Values = values; OutDir = outDir;
        var s = Settings.Instance;
        Parallel = Math.Max(1, parallel ?? (s.GetBool("enable_parallel") ? (int)Math.Max(1, s.GetLong("max_parallel")) : 1));
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = $"batch-{Script.Id}" };
        _thread.Start();
    }

    public BatchSummary RunBlocking() { Run(); return Summary; }
    public void Cancel() => Token.Cancel();
    public bool Running => _thread?.IsAlive == true;

    void Emit(QueueItem it) { try { OnItem?.Invoke(it); } catch { } }

    void Overall(List<QueueItem> items, string current)
    {
        int total = Math.Max(1, items.Count);
        double pct; int finished;
        lock (_lock)
        {
            pct = items.Sum(i => _progress.GetValueOrDefault(i.Id)) / total;
            finished = items.Count(i => i.Status is not ("pending" or "running"));
        }
        try { OnOverall?.Invoke(pct, current); } catch { }
        Status.Update(Script.Name, finished, items.Count, pct, current);
    }

    static string DefaultMsg(string st) => st switch
    {
        "done" => "Готово", "failed" => "Ошибка", "skipped" => "Пропущен", "cancelled" => "Отменено", "partial" => "Частично", _ => st,
    };

    void Run()
    {
        var sw = Stopwatch.StartNew();
        List<QueueItem> prepared;
        try { prepared = Script.PrepareBatch(AllItems, Values); }
        catch (Exception ex)
        {
            Log.Error($"Подготовка пакета не удалась: {ex.Message}", Script.Name, ex.ToString());
            prepared = new();
        }
        var items = prepared.Where(i => i.Status == "pending").ToList();
        var chosen = new HashSet<QueueItem>(prepared);
        foreach (var it in AllItems)
            if (!chosen.Contains(it) && it.Status == "pending")
            {
                it.Status = "skipped";
                it.Message = Script.SkipReason(it);
                Emit(it);
            }
        Summary.Total = items.Count;
        var batch = new Batch(AllItems);
        Log.Info($"Запуск: {items.Count} элемент(ов), потоков: {Parallel}", Script.Name);

        void Work(int index, QueueItem item)
        {
            if (Token.Cancelled)
            {
                item.Status = "cancelled"; item.Message = "Отменено"; Emit(item);
                return;
            }
            item.Status = "running"; item.Progress = 0; item.Message = "Запуск…"; item.Outputs = new();
            Emit(item);
            long last = 0;
            void OnProgress(QueueItem it, double pct, string msg)
            {
                it.Progress = pct;
                if (msg != "") it.Message = msg;
                lock (_lock) _progress[it.Id] = pct;
                var now = sw.ElapsedMilliseconds;
                if (now - Interlocked.Read(ref last) >= 200 || pct >= 100)
                {
                    Interlocked.Exchange(ref last, now);
                    Emit(it);
                    Overall(items, msg != "" ? $"{it.Name}: {msg}" : it.Name);
                }
            }
            var ctx = new Context(Script, item, Values, batch, Token, OutDir, OnProgress, Ask) { Index = index, Total = items.Count };
            Result result;
            try { result = Script.Process(item, ctx); }
            catch (CancelledException) { result = Result.CancelledResult(); }
            catch (ToolMissingException ex) { result = Result.Failed($"❌ {ex.Message}"); }
            catch (Exception ex)
            {
                Log.Error($"Ошибка обработки {item.Name}: {ex.Message}", Script.Name, ex.ToString());
                result = Result.Failed($"❌ Ошибка: {ex.Message}");
            }
            finally { ctx.Cleanup(); }
            if (Token.Cancelled && result.Status is not ("done" or "partial" or "skipped")) result.Status = "cancelled";
            item.Status = result.Status;
            item.Outputs = result.Outputs;
            if (result.Status is "done" or "partial" or "skipped") item.Progress = 100;
            item.Message = result.Messages.Count > 0 ? result.Messages[^1] : DefaultMsg(result.Status);
            lock (_lock)
            {
                _progress[item.Id] = 100;
                Summary.Results[item.Id] = result;
                switch (result.Status)
                {
                    case "done": Summary.Done++; break;
                    case "partial": Summary.Partial++; break;
                    case "failed": Summary.Failed++; break;
                    case "skipped": Summary.Skipped++; break;
                }
            }
            var level = result.Status switch
            {
                "done" => "SUCCESS", "partial" => "WARNING", "failed" => "ERROR", "cancelled" => "WARNING", _ => "INFO",
            };
            for (int i = 0; i < result.Messages.Count; i++)
                Log.Write(i == result.Messages.Count - 1 ? level : "INFO", result.Messages[i], Script.Name);
            Emit(item);
            try { OnResult?.Invoke(item, result); } catch { }
            Overall(items, item.Name);
        }

        try
        {
            if (Parallel > 1 && items.Count > 1)
                System.Threading.Tasks.Parallel.ForEach(Enumerable.Range(0, items.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = Parallel }, i => Work(i, items[i]));
            else
                for (int i = 0; i < items.Count; i++) Work(i, items[i]);
        }
        finally
        {
            Summary.Cancelled = Token.Cancelled;
            Summary.Elapsed = sw.Elapsed.TotalSeconds;
            Status.Finish(Script.Name, Summary.Ok, Summary.Failed, Summary.Cancelled);
            Log.Info($"Завершено за {Summary.Elapsed:0.0} с: успешно {Summary.Ok}, ошибок {Summary.Failed}, пропущено {Summary.Skipped}"
                     + (Summary.Cancelled ? " (отменено)" : ""), Script.Name);
            try { OnFinish?.Invoke(Summary); }
            catch (Exception ex) { Log.Error("Ошибка обработчика завершения", Script.Name, ex.ToString()); }
        }
    }
}
