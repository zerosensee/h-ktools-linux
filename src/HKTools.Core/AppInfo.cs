namespace HKTools;

/// <summary>Сведения о приложении. hktools — независимый Linux-форк K-Tools (krnzhnr).</summary>
public static class AppInfo
{
    public const string Id = "io.github.zerosensee.HKTools";
    public const string Name = "hktools";
    public const string Version = "2.0.0-alpha.1";
    public const string RepoUrl = "https://github.com/zerosensee/h-ktools-linux";
    public const string UpstreamName = "K-Tools";
    public const string UpstreamUrl = "https://github.com/krnzhnr/k-tools";
}

/// <summary>XDG-пути.</summary>
public static class Paths
{
    const string App = "hktools";

    static string Xdg(string var, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(var);
        return !string.IsNullOrEmpty(v) && System.IO.Path.IsPathRooted(v)
            ? v
            : System.IO.Path.Combine(Home, fallback);
    }

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static string Ensure(string p) { Directory.CreateDirectory(p); return p; }

    public static string ConfigDir => Ensure(System.IO.Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), App));
    public static string DataDir => Ensure(System.IO.Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), App));
    public static string CacheDir => Ensure(System.IO.Path.Combine(Xdg("XDG_CACHE_HOME", ".cache"), App));
    public static string StateDir => Ensure(System.IO.Path.Combine(Xdg("XDG_STATE_HOME", ".local/state"), App));

    public static string RuntimeDir
    {
        get
        {
            var b = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(b)) b = System.IO.Path.GetTempPath();
            return Ensure(System.IO.Path.Combine(b, App));
        }
    }

    public static string StatusFile => System.IO.Path.Combine(RuntimeDir, "status.json");

    public static string TempDir(string prefix = "hk_")
    {
        var d = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static string WhisperModelsDir(string overrideDir = null)
    {
        var p = string.IsNullOrWhiteSpace(overrideDir) ? System.IO.Path.Combine(DataDir, "whisper-models") : ExpandUser(overrideDir);
        return Ensure(p);
    }

    public static string ExpandUser(string p) =>
        p != null && p.StartsWith("~") ? Home + p[1..] : p;
}
