using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;

namespace HKTools.Ui.Native;

/// <summary>
/// Тонкий слой P/Invoke над GLib / GObject / GTK 4 / libadwaita / cairo.
/// Без внешних пакетов NuGet: приложение собирается офлайн и не тянет рантайм-генераторов привязок.
/// </summary>
internal static partial class Lib
{
    public const string GLib = "libglib-2.0.so.0";
    public const string GObject = "libgobject-2.0.so.0";
    public const string Gio = "libgio-2.0.so.0";
    public const string Gtk = "libgtk-4.so.1";
    public const string Adw = "libadwaita-1.so.0";
    public const string Cairo = "libcairo.so.2";

    static readonly string[] SearchLibs = [Gtk, Adw, Gio, GObject, GLib];

    [DllImport("libc", EntryPoint = "dlopen")] static extern IntPtr DlopenC([MarshalAs(UnmanagedType.LPUTF8Str)] string file, int mode);
    [DllImport("libdl.so.2", EntryPoint = "dlopen")] static extern IntPtr DlopenDl([MarshalAs(UnmanagedType.LPUTF8Str)] string file, int mode);

    /// <summary>
    /// Загрузить библиотеки с RTLD_GLOBAL: GtkBuilder (шаблоны libadwaita) ищет функции *_get_type через
    /// g_module_open(NULL) — без глобальной видимости символов типы вроде GSignalGroup не находятся.
    /// </summary>
    public static void LoadGlobal()
    {
        const int RTLD_NOW = 2, RTLD_GLOBAL = 0x100;
        foreach (var l in new[] { GLib, GObject, Gio, Cairo, Gtk, Adw })
        {
            try { DlopenC(l, RTLD_NOW | RTLD_GLOBAL); }
            catch (EntryPointNotFoundException) { DlopenDl(l, RTLD_NOW | RTLD_GLOBAL); }
            catch (DllNotFoundException) { DlopenDl(l, RTLD_NOW | RTLD_GLOBAL); }
        }
    }
    static readonly Dictionary<string, IntPtr> Handles = new();

    static IntPtr Handle(string name)
    {
        lock (Handles)
        {
            if (!Handles.TryGetValue(name, out var h))
                Handles[name] = h = NativeLibrary.Load(name, typeof(Lib).Assembly, DllImportSearchPath.SafeDirectories);
            return h;
        }
    }

    public static IntPtr Export(string symbol)
    {
        foreach (var l in SearchLibs)
            if (NativeLibrary.TryGetExport(Handle(l), symbol, out var p)) return p;
        return IntPtr.Zero;
    }

    public static string Str(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    /// <summary>NULL-терминированный массив UTF-8 строк (освобождать FreeStrv).</summary>
    public static IntPtr Strv(IReadOnlyList<string> items)
    {
        var arr = Marshal.AllocHGlobal(IntPtr.Size * (items.Count + 1));
        for (int i = 0; i < items.Count; i++) Marshal.WriteIntPtr(arr, i * IntPtr.Size, Marshal.StringToCoTaskMemUTF8(items[i]));
        Marshal.WriteIntPtr(arr, items.Count * IntPtr.Size, IntPtr.Zero);
        return arr;
    }

    public static void FreeStrv(IntPtr arr, int count)
    {
        for (int i = 0; i < count; i++) Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(arr, i * IntPtr.Size));
        Marshal.FreeHGlobal(arr);
    }
}

/// <summary>GType по имени класса: GtkListBoxRow → gtk_list_box_row_get_type().</summary>
internal static unsafe class GType
{
    static readonly ConcurrentDictionary<string, nuint> Cache = new();

    public static nuint Of(string typeName) => Cache.GetOrAdd(typeName, n =>
    {
        var fn = Lib.Export(GetTypeFunc(n));
        if (fn == IntPtr.Zero) throw new InvalidOperationException($"Неизвестный тип GObject: {n}");
        return ((delegate* unmanaged<nuint>)fn)();
    });

    static string GetTypeFunc(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])))) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb + "_get_type";
    }

    [DllImport(Lib.GObject)] public static extern nuint g_type_fundamental(nuint t);
    [DllImport(Lib.GObject)] public static extern IntPtr g_type_name(nuint t);
    [DllImport(Lib.GObject)] public static extern int g_type_is_a(nuint t, nuint isA);
}

/// <summary>GValue (24 байта) с заполнением по фундаментальному типу.</summary>
internal static class GValue
{
    public const int Size = 24;
    // фундаментальные типы GLib (G_TYPE_MAKE_FUNDAMENTAL(n) = n << 2)
    public const nuint Interface = 2 << 2, Boolean = 5 << 2, Int = 6 << 2, UInt = 7 << 2, Long = 8 << 2, ULong = 9 << 2, Int64 = 10 << 2, UInt64 = 11 << 2,
        Enum = 12 << 2, Flags = 13 << 2, Float = 14 << 2, Double = 15 << 2, String = 16 << 2, Pointer = 17 << 2, Boxed = 18 << 2,
        Param = 19 << 2, Object = 20 << 2, Variant = 21 << 2;

    [DllImport(Lib.GObject)] public static extern IntPtr g_value_init(IntPtr v, nuint t);
    [DllImport(Lib.GObject)] public static extern void g_value_unset(IntPtr v);
    [DllImport(Lib.GObject)] public static extern void g_value_set_boolean(IntPtr v, int b);
    [DllImport(Lib.GObject)] public static extern int g_value_get_boolean(IntPtr v);
    [DllImport(Lib.GObject)] public static extern void g_value_set_int(IntPtr v, int i);
    [DllImport(Lib.GObject)] public static extern int g_value_get_int(IntPtr v);
    [DllImport(Lib.GObject)] public static extern void g_value_set_uint(IntPtr v, uint i);
    [DllImport(Lib.GObject)] public static extern uint g_value_get_uint(IntPtr v);
    [DllImport(Lib.GObject)] public static extern void g_value_set_long(IntPtr v, nint i);
    [DllImport(Lib.GObject)] public static extern nint g_value_get_long(IntPtr v);
    [DllImport(Lib.GObject)] public static extern void g_value_set_int64(IntPtr v, long i);
    [DllImport(Lib.GObject)] public static extern long g_value_get_int64(IntPtr v);
    [DllImport(Lib.GObject)] public static extern void g_value_set_enum(IntPtr v, int i);
    [DllImport(Lib.GObject)] public static extern int g_value_get_enum(IntPtr v);
    [DllImport(Lib.GObject)] public static extern void g_value_set_flags(IntPtr v, uint i);
    [DllImport(Lib.GObject)] public static extern uint g_value_get_flags(IntPtr v);
    [DllImport(Lib.GObject)] public static extern void g_value_set_float(IntPtr v, float f);
    [DllImport(Lib.GObject)] public static extern float g_value_get_float(IntPtr v);
    [DllImport(Lib.GObject)] public static extern void g_value_set_double(IntPtr v, double d);
    [DllImport(Lib.GObject)] public static extern double g_value_get_double(IntPtr v);
    [DllImport(Lib.GObject)] public static extern void g_value_set_string(IntPtr v, [MarshalAs(UnmanagedType.LPUTF8Str)] string s);
    [DllImport(Lib.GObject)] public static extern IntPtr g_value_get_string(IntPtr v);
    [DllImport(Lib.GObject)] public static extern void g_value_set_object(IntPtr v, IntPtr o);
    [DllImport(Lib.GObject)] public static extern IntPtr g_value_get_object(IntPtr v);
    [DllImport(Lib.GObject)] public static extern IntPtr g_value_get_boxed(IntPtr v);
    [DllImport(Lib.GObject)] public static extern void g_value_set_boxed(IntPtr v, IntPtr b);

    public static IntPtr Alloc()
    {
        var v = Marshal.AllocHGlobal(Size);
        unsafe { new Span<byte>((void*)v, Size).Clear(); }
        return v;
    }

    public static void Fill(IntPtr v, nuint type, object value)
    {
        g_value_init(v, type);
        switch (GType.g_type_fundamental(type))
        {
            case Boolean: g_value_set_boolean(v, Convert.ToBoolean(value) ? 1 : 0); break;
            case Int: g_value_set_int(v, Convert.ToInt32(value)); break;
            case UInt: g_value_set_uint(v, Convert.ToUInt32(value)); break;
            case Long: g_value_set_long(v, (nint)Convert.ToInt64(value)); break;
            case Int64: g_value_set_int64(v, Convert.ToInt64(value)); break;
            case Enum: g_value_set_enum(v, Convert.ToInt32(value)); break;
            case Flags: g_value_set_flags(v, Convert.ToUInt32(value)); break;
            case Float: g_value_set_float(v, Convert.ToSingle(value)); break;
            case Double: g_value_set_double(v, Convert.ToDouble(value)); break;
            case String: g_value_set_string(v, value?.ToString()); break;
            case Object:
            case Interface: g_value_set_object(v, value switch { GObj o => o.H, IntPtr p => p, null => IntPtr.Zero, _ => throw new ArgumentException("ожидается объект") }); break;
            case Boxed: g_value_set_boxed(v, value is IntPtr bp ? bp : IntPtr.Zero); break;
            default: throw new NotSupportedException($"Тип свойства {Lib.Str(GType.g_type_name(type))} не поддерживается");
        }
    }

    public static object Read(IntPtr v, nuint type) => GType.g_type_fundamental(type) switch
    {
        Boolean => g_value_get_boolean(v) != 0,
        Int => g_value_get_int(v),
        UInt => g_value_get_uint(v),
        Long => (long)g_value_get_long(v),
        Int64 => g_value_get_int64(v),
        Enum => g_value_get_enum(v),
        Flags => g_value_get_flags(v),
        Float => (double)g_value_get_float(v),
        Double => g_value_get_double(v),
        String => Lib.Str(g_value_get_string(v)),
        Object or Interface => g_value_get_object(v),
        Boxed => g_value_get_boxed(v),
        _ => null,
    };
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Sig0(IntPtr self, IntPtr data);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SigP(IntPtr self, IntPtr a, IntPtr data);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SigB(IntPtr self, IntPtr data);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SigPB(IntPtr self, IntPtr a, IntPtr data);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SigXY(IntPtr self, double x, double y, IntPtr data);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SigXYB(IntPtr self, double x, double y, IntPtr data);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SigDrop(IntPtr self, IntPtr value, double x, double y, IntPtr data);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SigPress(IntPtr self, int n, double x, double y, IntPtr data);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SigKey(IntPtr self, uint keyval, uint keycode, uint state, IntPtr data);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void DestroyNotify(IntPtr data);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SourceFunc(IntPtr data);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void AsyncReady(IntPtr source, IntPtr result, IntPtr data);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void DrawFunc(IntPtr area, IntPtr cr, int w, int h, IntPtr data);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void HeaderFunc(IntPtr row, IntPtr before, IntPtr data);

/// <summary>Сигналы: делегат удерживается GCHandle и освобождается вместе с подключением.</summary>
internal static class Signals
{
    [DllImport(Lib.GObject)]
    static extern nuint g_signal_connect_data(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string signal, IntPtr handler, IntPtr data, IntPtr destroy, int flags);
    [DllImport(Lib.GObject)] public static extern void g_signal_handler_disconnect(IntPtr instance, nuint id);
    [DllImport(Lib.GObject)] public static extern void g_signal_handler_block(IntPtr instance, nuint id);
    [DllImport(Lib.GObject)] public static extern void g_signal_handler_unblock(IntPtr instance, nuint id);

    static readonly DestroyNotify FreeHandle = data => { try { GCHandle.FromIntPtr(data).Free(); } catch { } };
    public static readonly IntPtr FreeHandlePtr = Marshal.GetFunctionPointerForDelegate(FreeHandle);

    public static nuint Connect(IntPtr obj, string signal, Delegate trampoline)
    {
        var h = GCHandle.Alloc(trampoline);
        return g_signal_connect_data(obj, signal, Marshal.GetFunctionPointerForDelegate(trampoline), GCHandle.ToIntPtr(h), FreeHandlePtr, 0);
    }

    /// <summary>Исключение в обработчике не должно ронять процесс (вызов из нативного кода).</summary>
    public static void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) { HKTools.Log.Error($"Ошибка интерфейса: {ex.Message}", "UI", ex.ToString()); }
    }

    public static T Guard<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch (Exception ex) { HKTools.Log.Error($"Ошибка интерфейса: {ex.Message}", "UI", ex.ToString()); return fallback; }
    }
}

/// <summary>Главный цикл: вызовы из рабочих потоков, таймеры.</summary>
internal static class MainLoop
{
    [DllImport(Lib.GLib)] static extern uint g_idle_add_full(int priority, IntPtr fn, IntPtr data, IntPtr notify);
    [DllImport(Lib.GLib)] static extern uint g_timeout_add_full(int priority, uint ms, IntPtr fn, IntPtr data, IntPtr notify);
    [DllImport(Lib.GLib)] public static extern int g_source_remove(uint id);

    static readonly ConcurrentQueue<Action> Queue = new();
    static readonly SourceFunc Drain = _ =>
    {
        int n = 0;
        while (n++ < 200 && Queue.TryDequeue(out var a)) Signals.Guard(a);
        return Queue.IsEmpty ? 0 : 1;
    };
    static readonly IntPtr DrainPtr = Marshal.GetFunctionPointerForDelegate(Drain);
    static int _scheduled;

    /// <summary>Выполнить в главном потоке GTK.</summary>
    public static void Post(Action a)
    {
        Queue.Enqueue(a);
        g_idle_add_full(200, DrainPtr, IntPtr.Zero, IntPtr.Zero);
        Interlocked.Increment(ref _scheduled);
    }

    static readonly SourceFunc Tick = data => Signals.Guard(() => ((Func<bool>)GCHandle.FromIntPtr(data).Target)(), false) ? 1 : 0;
    static readonly IntPtr TickPtr = Marshal.GetFunctionPointerForDelegate(Tick);

    /// <summary>Таймер: повторяется, пока fn возвращает true.</summary>
    public static uint Timeout(uint ms, Func<bool> fn) =>
        g_timeout_add_full(0, ms, TickPtr, GCHandle.ToIntPtr(GCHandle.Alloc(fn)), Signals.FreeHandlePtr);

    public static uint Idle(Func<bool> fn) =>
        g_idle_add_full(200, TickPtr, GCHandle.ToIntPtr(GCHandle.Alloc(fn)), Signals.FreeHandlePtr);

    /// <summary>Фоновая работа, затем done(result, error) в главном потоке.</summary>
    public static void RunBg<T>(Func<T> work, Action<T, Exception> done)
    {
        new Thread(() =>
        {
            T res = default;
            Exception err = null;
            try { res = work(); } catch (Exception ex) { err = ex; }
            Post(() => done(res, err));
        }) { IsBackground = true }.Start();
    }
}
