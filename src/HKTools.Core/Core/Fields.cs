namespace HKTools.Core;

public enum FieldType { Text, Combo, Check, Int, Float, Keywords, Expander }

/// <summary>Условие видимости/блокировки поля (аналог условий SettingField из Windows-версии).</summary>
public sealed class Cond
{
    public string Key;
    public string[] Values;
    public bool Negate;

    public Cond(string key, object values, bool negate = false)
    {
        Key = key;
        Negate = negate;
        Values = values switch
        {
            string s => [s],
            bool b => [Norm(b)],
            IEnumerable<string> e => e.ToArray(),
            _ => [Norm(values)],
        };
    }

    public static string Norm(object v) => v switch { bool b => b ? "True" : "False", null => "", _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) };

    public bool Test(IDictionary<string, object> vals)
    {
        var r = Values.Contains(Norm(vals.Get(Key)));
        return Negate ? !r : r;
    }
}

/// <summary>Декларативное поле настроек: UI строит форму, CLI принимает --set key=value.</summary>
public sealed class Field
{
    public string Key = "", Label = "";
    public FieldType Type;
    public object Default;
    public string Group = "Общие";          // «Вкладка:Группа» или «Группа»
    public List<string> Options;
    public string Comment = "";
    public List<Cond> Visible = new();      // все условия должны выполняться
    public List<Cond> Disabled = new();     // любое отключает
    public List<Field> Children = new();
    public bool Toggle = true;              // у Expander есть переключатель
    public double? Min, Max, Step;
    public int Digits = 2;
    public (string Title, string Text)? Warning;
    public (string Key, string Label)? ExtraFlag;
    public string Placeholder = "";
    public string Browse;                   // "file" | "folder"

    public Field(string key, string label, FieldType type, object def = null, string group = "Общие")
    {
        Key = key; Label = label; Type = type; Default = def; Group = group;
    }

    public string Tab => Group.Contains(':') ? Group.Split(':', 2)[0] : "";
    public string Section => Group.Contains(':') ? Group.Split(':', 2)[1] : Group;
    public bool IsVisible(IDictionary<string, object> v) => Visible.All(c => c.Test(v));
    public bool IsDisabled(IDictionary<string, object> v) => Disabled.Any(c => c.Test(v));
    public bool HasValue => !(Type == FieldType.Expander && !Toggle);

    // флюент-помощники для компактного описания схем
    public Field Opts(params string[] o) { Options = o.ToList(); return this; }
    public Field Opts(IEnumerable<string> o) { Options = o.ToList(); return this; }
    public Field Hint(string c) { Comment = c; return this; }
    public Field When(params Cond[] c) { Visible.AddRange(c); return this; }
    public Field Off(params Cond[] c) { Disabled.AddRange(c); return this; }
    public Field Range(double min, double max, double? step = null, int digits = 2) { Min = min; Max = max; Step = step; Digits = digits; return this; }
    public Field Kids(params Field[] f) { Children.AddRange(f); return this; }
    public Field Warn(string t, string x) { Warning = (t, x); return this; }
    public Field NoToggle() { Toggle = false; return this; }
    public Field Flag(string key, string label) { ExtraFlag = (key, label); return this; }
    public Field Pick(string kind) { Browse = kind; return this; }
    public Field Ph(string p) { Placeholder = p; return this; }

    public static IEnumerable<Field> Walk(IEnumerable<Field> fields)
    {
        foreach (var f in fields)
        {
            yield return f;
            foreach (var c in Walk(f.Children)) yield return c;
        }
    }

    public static Dictionary<string, object> Defaults(IEnumerable<Field> fields) =>
        Walk(fields).Where(f => f.HasValue).GroupBy(f => f.Key).ToDictionary(g => g.Key, g => Json.DeepCopy(g.First().Default));

    /// <summary>Привести значение (из CLI или JSON) к типу поля.</summary>
    public object Coerce(object raw)
    {
        switch (Type)
        {
            case FieldType.Check:
            case FieldType.Expander:
                return Json.ToBool(raw);
            case FieldType.Int:
            {
                var v = Json.ToLong(raw, Json.ToLong(Default));
                if (Min.HasValue) v = Math.Max((long)Min.Value, v);
                if (Max.HasValue) v = Math.Min((long)Max.Value, v);
                return v;
            }
            case FieldType.Float:
            {
                var v = Json.ToDouble(raw, Json.ToDouble(Default));
                if (Min.HasValue) v = Math.Max(Min.Value, v);
                if (Max.HasValue) v = Math.Min(Max.Value, v);
                return v;
            }
            case FieldType.Combo:
            {
                var s = raw?.ToString() ?? "";
                if (Options == null || Options.Contains(s)) return s;
                return Options.FirstOrDefault(o => o.Equals(s, StringComparison.OrdinalIgnoreCase)) ?? Default;
            }
            case FieldType.Keywords:
                return KwItem.ToStore(KwItem.Parse(raw));
            default:
                return raw?.ToString() ?? "";
        }
    }
}

/// <summary>Чтение значений настроек с приведением типов.</summary>
public static class Vals
{
    public static string Str(this IDictionary<string, object> v, string k, string def = "") =>
        v.TryGetValue(k, out var o) && o != null ? Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) : def;
    public static bool Bool(this IDictionary<string, object> v, string k, bool def = false) =>
        v.TryGetValue(k, out var o) && o != null ? Json.ToBool(o) : def;
    public static int Int(this IDictionary<string, object> v, string k, int def = 0) =>
        v.TryGetValue(k, out var o) && o != null ? (int)Json.ToLong(o, def) : def;
    public static double Dbl(this IDictionary<string, object> v, string k, double def = 0) =>
        v.TryGetValue(k, out var o) && o != null ? Json.ToDouble(o, def) : def;
    public static List<string> Words(this IDictionary<string, object> v, string k, bool lower = false) =>
        KwItem.ActiveWords(v.Get(k), lower);
    public static object Get(this IDictionary<string, object> v, string k) => v.TryGetValue(k, out var o) ? o : null;
}
