using System.Text.Encodings.Web;
using System.Text.Json;

namespace HKTools;

/// <summary>Элемент списка ключевых слов (KeywordList).</summary>
public sealed class KwItem
{
    public string Word = "";
    public bool Active = true;
    public bool OnlyPart;

    public static List<KwItem> Parse(object raw)
    {
        var res = new List<KwItem>();
        if (raw is string s)
        {
            foreach (var w in s.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                res.Add(new KwItem { Word = w });
            return res;
        }
        if (raw is not System.Collections.IEnumerable list) return res;
        foreach (var o in list)
        {
            if (o is KwItem k) res.Add(new KwItem { Word = k.Word, Active = k.Active, OnlyPart = k.OnlyPart });
            else if (o is IDictionary<string, object> d && d.TryGetValue("word", out var w))
                res.Add(new KwItem
                {
                    Word = w?.ToString() ?? "",
                    Active = !d.TryGetValue("active", out var a) || Json.ToBool(a),
                    OnlyPart = d.TryGetValue("only_part", out var op) && Json.ToBool(op),
                });
            else if (o is string str) res.Add(new KwItem { Word = str });
        }
        return res;
    }

    public static List<object> ToStore(IEnumerable<KwItem> items) =>
        items.Select(i => (object)new Dictionary<string, object>
        {
            ["word"] = i.Word, ["active"] = i.Active, ["only_part"] = i.OnlyPart,
        }).ToList();

    public static List<string> ActiveWords(object raw, bool lower = false) =>
        Parse(raw).Where(i => i.Active && i.Word.Trim() != "")
            .Select(i => lower ? i.Word.ToLowerInvariant() : i.Word).ToList();
}

/// <summary>Помощники JSON ↔ «родные» типы (bool, long, double, string, List, Dictionary).</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static object FromElement(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => FromElement(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(FromElement).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    public static object Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return FromElement(doc.RootElement);
    }

    public static bool ToBool(object o) => o switch
    {
        bool b => b,
        null => false,
        string s => s.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on" or "да",
        long l => l != 0,
        int i => i != 0,
        double d => d != 0,
        _ => false,
    };

    public static double ToDouble(object o, double def = 0)
    {
        if (o is double d) return d;
        if (o is long l) return l;
        if (o is int i) return i;
        if (o is float f) return f;
        if (o is string s && double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var r)) return r;
        if (o is bool b) return b ? 1 : 0;
        return def;
    }

    public static long ToLong(object o, long def = 0) => o == null ? def : (long)Math.Round(ToDouble(o, def));

    public static object DeepCopy(object o) => o switch
    {
        IDictionary<string, object> d => d.ToDictionary(kv => kv.Key, kv => DeepCopy(kv.Value)),
        List<object> l => l.Select(DeepCopy).ToList(),
        List<KwItem> k => KwItem.ToStore(k),
        _ => o,
    };

    public static string Serialize(object o) => JsonSerializer.Serialize(o, Options);
}

/// <summary>Хранилище настроек (~/.config/hktools/settings.json) — совместимо с Python-версией.</summary>
public sealed class Settings
{
    public static readonly Settings Instance = new();

    public static readonly Dictionary<string, object> GeneralDefaults = new()
    {
        ["overwrite_existing"] = false,
        ["use_auto_subfolder"] = true,
        ["output_subfolder"] = "HKTools_Result",
        ["clear_list_on_add"] = false,
        ["enable_parallel"] = false,
        ["max_parallel"] = 2L,
        ["rename_enabled"] = false,
        ["rename_search"] = "",
        ["rename_replace"] = "",
        ["rename_use_regex"] = true,
        ["rename_case_sensitive"] = false,
        ["theme"] = "system",
        ["show_settings_sidebar"] = true,
        ["last_dir"] = "",
        ["notify_on_finish"] = true,
        ["notify_only_unfocused"] = true,
        ["status_file"] = true,
        ["tool_paths"] = new Dictionary<string, object>(),
        ["qaac_command"] = "",
        ["vaapi_device"] = "",
        ["whisper_models_dir"] = "",
        ["timing_history"] = new List<object>(),
    };

    readonly object _lock = new();
    readonly string _path;
    Dictionary<string, object> _general = new();
    Dictionary<string, object> _scripts = new();
    Timer _timer;

    Settings()
    {
        _path = System.IO.Path.Combine(Paths.ConfigDir, "settings.json");
        Load();
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path)) return;
                if (Json.Parse(File.ReadAllText(_path)) is Dictionary<string, object> root)
                {
                    _general = root.GetValueOrDefault("general") as Dictionary<string, object> ?? new();
                    _scripts = root.GetValueOrDefault("scripts") as Dictionary<string, object> ?? new();
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Файл настроек повреждён, используются значения по умолчанию: {ex.Message}", "Настройки");
                try { File.Move(_path, _path + ".bak", true); } catch { }
            }
        }
    }

    public void SaveNow()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            try
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(new Dictionary<string, object> { ["general"] = _general, ["scripts"] = _scripts }));
                File.Move(tmp, _path, true);
            }
            catch (Exception ex) { Log.Error($"Не удалось сохранить настройки: {ex.Message}", "Настройки"); }
        }
    }

    void ScheduleSave()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = new Timer(_ => SaveNow(), null, 600, Timeout.Infinite);
        }
    }

    public object Get(string key)
    {
        lock (_lock)
        {
            if (_general.TryGetValue(key, out var v)) return Json.DeepCopy(v);
            return GeneralDefaults.TryGetValue(key, out var d) ? Json.DeepCopy(d) : null;
        }
    }

    public bool GetBool(string key) => Json.ToBool(Get(key));
    public string GetStr(string key) => Get(key)?.ToString() ?? "";
    public long GetLong(string key) => Json.ToLong(Get(key));

    /// <summary>Временное переопределение (например, --overwrite в CLI) без сохранения на диск.</summary>
    public void SetTransient(string key, object value) { lock (_lock) _general[key] = value; }

    public void Set(string key, object value)
    {
        lock (_lock) _general[key] = Json.DeepCopy(value);
        ScheduleSave();
    }

    public string ToolPath(string name) =>
        (Get("tool_paths") as Dictionary<string, object>)?.GetValueOrDefault(name)?.ToString() ?? "";

    public void SetToolPath(string name, string value)
    {
        var tp = Get("tool_paths") as Dictionary<string, object> ?? new();
        if (string.IsNullOrWhiteSpace(value)) tp.Remove(name); else tp[name] = value;
        Set("tool_paths", tp);
    }

    public Dictionary<string, object> ScriptValues(string id)
    {
        lock (_lock)
            return _scripts.GetValueOrDefault(id) is Dictionary<string, object> d
                ? (Dictionary<string, object>)Json.DeepCopy(d)
                : new();
    }

    public void SetScriptValue(string id, string key, object value)
    {
        lock (_lock)
        {
            if (_scripts.GetValueOrDefault(id) is not Dictionary<string, object> d)
                _scripts[id] = d = new Dictionary<string, object>();
            d[key] = Json.DeepCopy(value);
        }
        ScheduleSave();
    }

    public void ResetScript(string id)
    {
        lock (_lock) _scripts.Remove(id);
        ScheduleSave();
    }
}
