using System.Globalization;
using HKTools.Core;
using static HKTools.Core.FieldType;

namespace HKTools.Encoders;

public sealed record EncContext(bool Lossless, bool Force10Bit, string Container, string VaapiDevice = "");

/// <summary>Видеоэнкодеры: CPU (x265 / x264 / SVT-AV1), NVENC (NVIDIA), VAAPI (AMD / Intel).</summary>
public abstract class Encoder
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public virtual bool Available => true;
    public abstract List<Field> Fields();
    public virtual List<string> InputArgs(IDictionary<string, object> v, EncContext c) => new();
    public virtual List<string> FilterSuffix(IDictionary<string, object> v, EncContext c) => new();
    public abstract List<string> Args(IDictionary<string, object> v, EncContext c);
    public virtual string Tag(IDictionary<string, object> v, EncContext c) => null;

    protected static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    protected const string G = "Видео:Кодирование", B = "Видео:Битрейт", A = "Видео:Расширенные параметры";

    /// <summary>Поля битрейта (основной/мин/макс/буфер) с автоматическим расчётом границ.</summary>
    protected static List<Field> BrFields(string prefix, Cond vis)
    {
        var lossless = new Cond("lossless", true);
        var auto = new Cond($"{prefix}auto_bitrate", true);
        return
        [
            new Field($"{prefix}auto_bitrate", "Автовычисление границ битрейтов", Check, true, B).Hint("Мин = основной, макс = ×2, буфер = макс×2").When(vis).Off(lossless),
            new Field($"{prefix}v_bitrate", "Битрейт видео (кбит/с)", Int, 4000L, B).When(vis).Off(lossless).Range(100, 500000, 100),
            new Field($"{prefix}min_bitrate", "Мин. битрейт (кбит/с)", Int, 4000L, B).When(vis).Off(lossless, auto).Range(0, 500000, 100),
            new Field($"{prefix}max_bitrate", "Макс. битрейт (кбит/с)", Int, 8000L, B).When(vis).Off(lossless, auto).Range(0, 500000, 100),
            new Field($"{prefix}bufsize", "Размер буфера VBV (кбит)", Int, 16000L, B).When(vis).Off(lossless, auto).Range(0, 1000000, 500),
        ];
    }

    protected static List<string> BrArgs(IDictionary<string, object> v, string prefix)
    {
        int vbr = v.Int($"{prefix}v_bitrate", 4000);
        bool auto = v.Bool($"{prefix}auto_bitrate", true);
        int minr = auto ? vbr : v.Int($"{prefix}min_bitrate", 4000);
        int maxr = auto ? vbr * 2 : v.Int($"{prefix}max_bitrate", 8000);
        int buf = auto ? maxr * 2 : v.Int($"{prefix}bufsize", 16000);
        var a = new List<string> { "-b:v", $"{vbr}k" };
        if (minr > 0) a.AddRange(["-minrate", $"{minr}k"]);
        if (maxr > 0) a.AddRange(["-maxrate", $"{maxr}k"]);
        if (buf > 0) a.AddRange(["-bufsize", $"{buf}k"]);
        return a;
    }

    public static readonly Encoder[] All = [new CpuEncoder(), new NvencEncoder(), new VaapiEncoder()];
    public static List<Encoder> AvailableList() => All.Where(e => e.Available).ToList();

    public static Encoder ById(string id)
    {
        if (id == "x265") id = "cpu";
        return All.FirstOrDefault(e => e.Id == id || e.Title == id);
    }
}

public sealed class CpuEncoder : Encoder
{
    public static readonly Dictionary<string, string> Codecs = new()
    {
        ["HEVC / H.265"] = "libx265", ["AVC / H.264"] = "libx264", ["AV1 (SVT-AV1)"] = "libsvtav1",
    };
    static readonly string[] Presets = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow", "placebo"];

    public override string Id => "cpu";
    public override string Title => "CPU";

    public override List<Field> Fields()
    {
        var caps = Hw.Caps();
        var codecs = Codecs.Where(kv => caps.Encoders.Count == 0 || caps.HasEncoder(kv.Value)).Select(kv => kv.Key).ToList();
        if (codecs.Count == 0) codecs = Codecs.Keys.ToList();
        var is265 = new Cond("cpu_codec", "HEVC / H.265");
        var is264 = new Cond("cpu_codec", "AVC / H.264");
        var isAv1 = new Cond("cpu_codec", "AV1 (SVT-AV1)");
        var lossless = new Cond("lossless", true);
        var o = new List<Field> { new Field("cpu_codec", "Кодек CPU", Combo, codecs[0], G).Opts(codecs) };
        // x265
        o.Add(new Field("x265_preset", "Пресет x265", Combo, "medium", G).Opts(Presets).When(is265).Hint("Баланс скорости кодирования и сжатия"));
        o.Add(new Field("x265_rc", "Режим качества", Combo, "CRF", B).Opts("CRF", "Битрейт (ABR)").When(is265).Off(lossless));
        o.Add(new Field("x265_crf", "CRF / Качество", Int, 23L, B).Hint("0–51, меньше = лучше").When(is265, new Cond("x265_rc", "CRF")).Off(lossless).Range(0, 51));
        foreach (var f in BrFields("x265_", new Cond("x265_rc", "Битрейт (ABR)"))) { f.Visible.Insert(0, is265); o.Add(f); }
        o.Add(new Field("x265_advanced_options", "Расширенные параметры x265", Expander, false, A).When(is265)
            .Hint("Выключено — чистые значения по умолчанию x265").Kids(
                new Field("x265_tune", "Tune", Combo, "Нет", A).Opts("Нет", "grain", "animation", "fastdecode", "zerolatency", "psnr", "ssim"),
                new Field("x265_aq_mode", "AQ Mode", Combo, "2", A).Opts("0", "1", "2", "3", "4")
                    .Hint("0 — выкл, 1 — обычный, 2 — auto-variance, 3 — тёмные сцены, 4 — с контурами"),
                new Field("x265_aq_strength", "Сила AQ", Float, 1.0, A).Range(0, 3, 0.1),
                new Field("x265_lookahead", "RC Lookahead", Int, 20L, A).Range(0, 250),
                new Field("x265_bframes", "B-кадры", Int, 4L, A).Range(0, 16),
                new Field("x265_b_adapt", "Адаптация B-кадров", Combo, "2", A).Opts("0", "1", "2"),
                new Field("x265_psy_rd", "Psy-RD", Float, 2.0, A).Range(0, 5, 0.1),
                new Field("x265_psy_rdoq", "Psy-RDOQ", Float, 0.0, A).Range(0, 50, 0.5),
                new Field("x265_deblock", "Деблокинг (tC,Beta)", Combo, "0,0", A).Opts("0,0", "-1,-1", "-2,-2", "1,1"),
                new Field("x265_no_sao", "Отключить SAO", Check, false, A).Hint("Сохраняет зерно и мелкие текстуры"),
                new Field("x265_no_fast_pskip", "Отключить Fast P-Skip", Check, false, A)));
        // x264
        o.Add(new Field("x264_preset", "Пресет x264", Combo, "medium", G).Opts(Presets).When(is264));
        o.Add(new Field("x264_rc", "Режим качества", Combo, "CRF", B).Opts("CRF", "Битрейт (ABR)").When(is264).Off(lossless));
        o.Add(new Field("x264_crf", "CRF / Качество", Int, 23L, B).When(is264, new Cond("x264_rc", "CRF")).Off(lossless).Range(0, 51));
        foreach (var f in BrFields("x264_", new Cond("x264_rc", "Битрейт (ABR)"))) { f.Visible.Insert(0, is264); o.Add(f); }
        o.Add(new Field("x264_advanced_options", "Расширенные параметры x264", Expander, false, A).When(is264).Kids(
            new Field("x264_tune", "Tune", Combo, "Нет", A).Opts("Нет", "film", "animation", "grain", "stillimage", "fastdecode", "zerolatency", "psnr", "ssim"),
            new Field("x264_profile", "Профиль", Combo, "Нет", A).Opts("Нет", "high", "main", "baseline")));
        // SVT-AV1
        o.Add(new Field("svt_preset", "Пресет SVT-AV1", Int, 6L, G).Hint("0 — медленно/лучше … 13 — быстро").When(isAv1).Range(0, 13));
        o.Add(new Field("svt_crf", "CRF / Качество", Int, 32L, B).Hint("0–63, меньше = лучше").When(isAv1).Range(0, 63));
        o.Add(new Field("svt_film_grain", "Синтез зерна (film-grain)", Int, 0L, A).Hint("0 — выкл, 4–10 для аниме/кино").When(isAv1).Range(0, 50));
        return o;
    }

    static string Codec(IDictionary<string, object> v) => Codecs.GetValueOrDefault(v.Str("cpu_codec", "HEVC / H.265"), "libx265");

    public override List<string> Args(IDictionary<string, object> v, EncContext c)
    {
        var codec = Codec(v);
        bool ten = c.Force10Bit && codec != "libx264";
        if (codec == "libsvtav1")
        {
            if (c.Lossless) throw new InvalidOperationException("Lossless не поддерживается SVT-AV1: выберите HEVC или H.264");
            var a = new List<string> { "-c:v", codec, "-pix_fmt", ten ? "yuv420p10le" : "yuv420p", "-preset", v.Int("svt_preset", 6).ToString(), "-crf", v.Int("svt_crf", 32).ToString() };
            int grain = v.Int("svt_film_grain");
            if (grain > 0) a.AddRange(["-svtav1-params", $"film-grain={grain}"]);
            return a;
        }
        if (codec == "libx264")
        {
            var a = new List<string> { "-c:v", codec, "-pix_fmt", "yuv420p", "-preset", v.Str("x264_preset", "medium") };
            if (c.Lossless) a.AddRange(["-qp", "0"]);
            else if (v.Str("x264_rc", "CRF") == "CRF") a.AddRange(["-crf", v.Int("x264_crf", 23).ToString()]);
            else a.AddRange(BrArgs(v, "x264_"));
            if (v.Bool("x264_advanced_options"))
            {
                if (v.Str("x264_tune", "Нет") != "Нет") a.AddRange(["-tune", v.Str("x264_tune")]);
                if (v.Str("x264_profile", "Нет") != "Нет") a.AddRange(["-profile:v", v.Str("x264_profile")]);
            }
            return a;
        }
        var args = new List<string> { "-c:v", codec, "-pix_fmt", ten ? "yuv420p10le" : "yuv420p", "-preset", v.Str("x265_preset", "medium") };
        var p = new List<string>();
        if (c.Lossless) p.Add("lossless=1");
        else if (v.Str("x265_rc", "CRF") == "CRF") args.AddRange(["-crf", v.Int("x265_crf", 23).ToString()]);
        else args.AddRange(BrArgs(v, "x265_"));
        if (v.Bool("x265_advanced_options"))
        {
            if (v.Str("x265_tune", "Нет") != "Нет") args.AddRange(["-tune", v.Str("x265_tune")]);
            p.Add($"aq-mode={v.Str("x265_aq_mode", "2")}");
            var aq = v.Dbl("x265_aq_strength", 1.0);
            if (Math.Abs(aq - 1.0) > 0.01) p.Add("aq-strength=" + Math.Clamp(aq, 0, 3).ToString("0.0", Inv));
            int la = v.Int("x265_lookahead", 20);
            if (la != 20) p.Add($"rc-lookahead={Math.Clamp(la, 0, 250)}");
            int bf = v.Int("x265_bframes", 4);
            if (bf != 4) p.Add($"bframes={Math.Clamp(bf, 0, 16)}");
            if (v.Str("x265_b_adapt", "2") != "2") p.Add($"b-adapt={v.Str("x265_b_adapt")}");
            var prd = v.Dbl("x265_psy_rd", 2.0);
            if (Math.Abs(prd - 2.0) > 0.01) p.Add("psy-rd=" + Math.Clamp(prd, 0, 5).ToString("0.0", Inv));
            var rdoq = v.Dbl("x265_psy_rdoq");
            if (Math.Abs(rdoq) > 0.01) p.Add("psy-rdoq=" + Math.Clamp(rdoq, 0, 50).ToString("0.0", Inv));
            var deb = v.Str("x265_deblock", "0,0").Replace(':', ',');
            if (deb != "" && deb != "0,0") p.Add($"deblock={deb}");
            if (v.Bool("x265_no_sao")) p.Add("no-sao=1");
            if (v.Bool("x265_no_fast_pskip")) p.Add("no-fast-pskip=1");
        }
        if (p.Count > 0) args.AddRange(["-x265-params", string.Join(":", p)]);
        return args;
    }

    public override string Tag(IDictionary<string, object> v, EncContext c) =>
        Codec(v) == "libx265" && c.Container.EndsWith("mp4") ? "hvc1" : null;
}

public sealed class NvencEncoder : Encoder
{
    public static readonly Dictionary<string, string> Codecs = new()
    {
        ["HEVC / H.265"] = "hevc_nvenc", ["AVC / H.264"] = "h264_nvenc", ["AV1"] = "av1_nvenc",
    };

    public override string Id => "nvenc";
    public override string Title => "NVENC (NVIDIA)";
    public override bool Available => Hw.Caps().NvencOk;

    public override List<Field> Fields()
    {
        var caps = Hw.Caps();
        var codecs = Codecs.Where(kv => caps.Nvenc.GetValueOrDefault(kv.Value)).Select(kv => kv.Key).ToList();
        if (codecs.Count == 0) codecs = Codecs.Keys.ToList();
        var lossless = new Cond("lossless", true);
        var notQp = new Cond("nvenc_rc", "constqp", negate: true);
        var o = new List<Field>
        {
            new Field("nvenc_codec", "Кодек NVENC", Combo, codecs[0], G).Opts(codecs),
            new Field("nvenc_preset", "Пресет скорости", Combo, "p4", G).Opts(Enumerable.Range(1, 7).Select(i => $"p{i}")).Hint("p1 — быстрее всего, p7 — лучшее сжатие"),
            new Field("nvenc_rc", "Режим управления битрейтом", Combo, "vbr", B).Opts("cbr", "vbr", "constqp").Off(lossless),
            new Field("v_qp", "QP / Качество", Int, 23L, B).Hint("0–51, меньше = лучше").When(new Cond("nvenc_rc", "constqp")).Off(lossless).Range(0, 51),
        };
        foreach (var f in BrFields("", notQp)) { f.Visible = [notQp]; o.Add(f); }
        o.AddRange([
            new Field("nv_lookahead", "Lookahead (кадров)", Int, 32L, A).Range(0, 32).Off(lossless),
            new Field("nv_spatial_aq", "Spatial AQ", Check, true, A).Off(lossless),
            new Field("nv_aq_strength", "Сила AQ", Int, 8L, A).Range(1, 15).When(new Cond("nv_spatial_aq", true)).Off(lossless),
            new Field("nv_temporal_aq", "Temporal AQ", Check, true, A).Off(lossless),
            new Field("nv_multipass", "Multipass", Combo, "fullres", A).Opts("disabled", "qres", "fullres").Off(lossless),
            new Field("nv_b_ref_mode", "Опорные B-кадры", Combo, "middle", A).Opts("disabled", "each", "middle").Hint("middle — B-пирамида").Off(lossless),
        ]);
        return o;
    }

    static string Codec(IDictionary<string, object> v) => Codecs.GetValueOrDefault(v.Str("nvenc_codec", "HEVC / H.265"), "hevc_nvenc");

    // Декодирование на GPU; кадры автоматически выгружаются в RAM для CPU-фильтров
    public override List<string> InputArgs(IDictionary<string, object> v, EncContext c) => ["-hwaccel", "cuda"];

    public override List<string> Args(IDictionary<string, object> v, EncContext c)
    {
        var codec = Codec(v);
        bool ten = c.Force10Bit && codec != "h264_nvenc";
        var a = new List<string> { "-c:v", codec, "-pix_fmt", ten ? "p010le" : "yuv420p", "-preset", v.Str("nvenc_preset", "p4") };
        if (c.Lossless)
        {
            if (codec == "av1_nvenc") throw new InvalidOperationException("Lossless не поддерживается AV1 NVENC: выберите HEVC или H.264");
            a.AddRange(["-rc", "constqp", "-tune", "lossless", "-qp", "0"]);
            return a;
        }
        var rc = v.Str("nvenc_rc", "vbr");
        a.AddRange(["-rc", rc]);
        if (rc == "constqp") a.AddRange(["-qp", v.Int("v_qp", 23).ToString()]);
        else a.AddRange(BrArgs(v, ""));
        int la = v.Int("nv_lookahead", 32);
        if (la > 0) a.AddRange(["-rc-lookahead", Math.Min(32, la).ToString()]);
        if (v.Bool("nv_spatial_aq", true)) a.AddRange(["-spatial-aq", "1", "-aq-strength", Math.Clamp(v.Int("nv_aq_strength", 8), 1, 15).ToString()]);
        else a.AddRange(["-spatial-aq", "0"]);
        a.AddRange(["-temporal-aq", v.Bool("nv_temporal_aq", true) ? "1" : "0"]);
        var mp = v.Str("nv_multipass", "fullres");
        if (mp != "") a.AddRange(["-multipass", mp]);
        var bref = v.Str("nv_b_ref_mode", "middle");
        if (codec == "av1_nvenc" && bref == "each") bref = "middle";
        if (bref != "") a.AddRange(["-b_ref_mode", bref]);
        return a;
    }

    public override string Tag(IDictionary<string, object> v, EncContext c) =>
        Codec(v) == "hevc_nvenc" && c.Container.EndsWith("mp4") ? "hvc1" : null;
}

public sealed class VaapiEncoder : Encoder
{
    public static readonly Dictionary<string, string> Codecs = new()
    {
        ["HEVC / H.265"] = "hevc_vaapi", ["AVC / H.264"] = "h264_vaapi", ["AV1"] = "av1_vaapi",
    };

    public override string Id => "vaapi";
    public override string Title => "VAAPI (AMD / Intel)";
    public override bool Available => Hw.Caps().VaapiOk;

    public override List<Field> Fields()
    {
        var caps = Hw.Caps();
        var codecs = Codecs.Where(kv => caps.Vaapi.GetValueOrDefault(kv.Value)).Select(kv => kv.Key).ToList();
        if (codecs.Count == 0) codecs = Codecs.Keys.ToList();
        var lossless = new Cond("lossless", true);
        var br = new Cond("vaapi_rc", new[] { "VBR", "CBR" });
        var o = new List<Field>
        {
            new Field("vaapi_codec", "Кодек VAAPI", Combo, codecs[0], G).Opts(codecs),
            new Field("vaapi_rc", "Режим управления битрейтом", Combo, "CQP", B).Opts("CQP", "VBR", "CBR", "ICQ")
                .Hint("ICQ — только Intel; CQP — постоянное качество").Off(lossless),
            new Field("vaapi_qp", "QP / Качество", Int, 24L, B).Hint("Меньше = лучше").When(new Cond("vaapi_rc", new[] { "CQP", "ICQ" })).Range(0, 51).Off(lossless),
        };
        foreach (var f in BrFields("va_", br)) { f.Visible = [br]; o.Add(f); }
        o.AddRange([
            new Field("vaapi_compression", "Уровень сжатия (compression_level)", Int, 0L, A)
                .Hint("0 — по умолчанию драйвера; больше — медленнее и лучше (Intel/AMD по-разному)").Range(0, 7),
            new Field("vaapi_hwdecode", "Аппаратное декодирование", Check, false, A).Hint("Быстрее, но недоступны вшивание субтитров и обрезка на CPU"),
        ]);
        return o;
    }

    static string Codec(IDictionary<string, object> v) => Codecs.GetValueOrDefault(v.Str("vaapi_codec", "HEVC / H.265"), "hevc_vaapi");

    public override List<string> InputArgs(IDictionary<string, object> v, EncContext c)
    {
        var dev = c.VaapiDevice != "" ? c.VaapiDevice : Hw.PickVaapiDevice();
        return v.Bool("vaapi_hwdecode")
            ? ["-hwaccel", "vaapi", "-hwaccel_device", dev, "-hwaccel_output_format", "vaapi"]
            : ["-vaapi_device", dev];
    }

    public override List<string> FilterSuffix(IDictionary<string, object> v, EncContext c)
    {
        bool ten = c.Force10Bit && Codec(v) != "h264_vaapi";
        return v.Bool("vaapi_hwdecode") ? [$"scale_vaapi=format={(ten ? "p010" : "nv12")}"] : [$"format={(ten ? "p010" : "nv12")}", "hwupload"];
    }

    public override List<string> Args(IDictionary<string, object> v, EncContext c)
    {
        if (c.Lossless) throw new InvalidOperationException("Lossless не поддерживается VAAPI: выберите CPU-энкодер");
        var codec = Codec(v);
        var rc = v.Str("vaapi_rc", "CQP");
        var a = new List<string> { "-c:v", codec, "-rc_mode", rc };
        if (rc is "CQP" or "ICQ") a.AddRange([rc == "CQP" ? "-qp" : "-global_quality", v.Int("vaapi_qp", 24).ToString()]);
        else a.AddRange(BrArgs(v, "va_"));
        int comp = v.Int("vaapi_compression");
        if (comp > 0) a.AddRange(["-compression_level", comp.ToString()]);
        if (c.Force10Bit && codec == "hevc_vaapi") a.AddRange(["-profile:v", "main10"]);
        return a;
    }

    public override string Tag(IDictionary<string, object> v, EncContext c) =>
        Codec(v) == "hevc_vaapi" && c.Container.EndsWith("mp4") ? "hvc1" : null;
}
