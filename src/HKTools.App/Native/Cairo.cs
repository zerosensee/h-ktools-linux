using System.Runtime.InteropServices;
using HKTools.Scripts;

namespace HKTools.Ui.Native;

/// <summary>Контекст cairo (только используемые функции).</summary>
internal sealed class Cairo(IntPtr cr) : IChartCanvas
{
    public readonly IntPtr Cr = cr;

    [StructLayout(LayoutKind.Sequential)]
    struct TextExtents { public double XBearing, YBearing, Width, Height, XAdvance, YAdvance; }

    [DllImport(Lib.Cairo)] static extern void cairo_set_source_rgba(IntPtr cr, double r, double g, double b, double a);
    [DllImport(Lib.Cairo)] static extern void cairo_paint(IntPtr cr);
    [DllImport(Lib.Cairo)] static extern void cairo_move_to(IntPtr cr, double x, double y);
    [DllImport(Lib.Cairo)] static extern void cairo_line_to(IntPtr cr, double x, double y);
    [DllImport(Lib.Cairo)] static extern void cairo_rectangle(IntPtr cr, double x, double y, double w, double h);
    [DllImport(Lib.Cairo)] static extern void cairo_arc(IntPtr cr, double x, double y, double r, double a1, double a2);
    [DllImport(Lib.Cairo)] static extern void cairo_close_path(IntPtr cr);
    [DllImport(Lib.Cairo)] static extern void cairo_new_path(IntPtr cr);
    [DllImport(Lib.Cairo)] static extern void cairo_stroke(IntPtr cr);
    [DllImport(Lib.Cairo)] static extern void cairo_fill(IntPtr cr);
    [DllImport(Lib.Cairo)] static extern void cairo_fill_preserve(IntPtr cr);
    [DllImport(Lib.Cairo)] static extern void cairo_clip(IntPtr cr);
    [DllImport(Lib.Cairo)] static extern void cairo_save(IntPtr cr);
    [DllImport(Lib.Cairo)] static extern void cairo_restore(IntPtr cr);
    [DllImport(Lib.Cairo)] static extern void cairo_set_dash(IntPtr cr, double[] d, int n, double off);
    [DllImport(Lib.Cairo)] static extern void cairo_set_line_width(IntPtr cr, double w);
    [DllImport(Lib.Cairo)] static extern void cairo_select_font_face(IntPtr cr, [MarshalAs(UnmanagedType.LPUTF8Str)] string family, int slant, int weight);
    [DllImport(Lib.Cairo)] static extern void cairo_set_font_size(IntPtr cr, double s);
    [DllImport(Lib.Cairo)] static extern void cairo_text_extents(IntPtr cr, [MarshalAs(UnmanagedType.LPUTF8Str)] string s, out TextExtents e);
    [DllImport(Lib.Cairo)] static extern void cairo_show_text(IntPtr cr, [MarshalAs(UnmanagedType.LPUTF8Str)] string s);
    [DllImport(Lib.Cairo)] static extern IntPtr cairo_image_surface_create(int format, int w, int h);
    [DllImport(Lib.Cairo)] static extern IntPtr cairo_create(IntPtr surface);
    [DllImport(Lib.Cairo)] static extern void cairo_destroy(IntPtr cr);
    [DllImport(Lib.Cairo)] static extern void cairo_surface_destroy(IntPtr s);
    [DllImport(Lib.Cairo)] static extern int cairo_surface_write_to_png(IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    public void Rgba(double r, double g, double b, double a = 1) => cairo_set_source_rgba(Cr, r, g, b, a);
    public void Paint() => cairo_paint(Cr);
    public void MoveTo(double x, double y) => cairo_move_to(Cr, x, y);
    public void LineTo(double x, double y) => cairo_line_to(Cr, x, y);
    public void Rect(double x, double y, double w, double h) => cairo_rectangle(Cr, x, y, w, h);
    public void Arc(double x, double y, double r, double a1, double a2) => cairo_arc(Cr, x, y, r, a1, a2);
    public void ClosePath() => cairo_close_path(Cr);
    public void NewPath() => cairo_new_path(Cr);
    public void Stroke() => cairo_stroke(Cr);
    public void Fill(bool preserve = false) { if (preserve) cairo_fill_preserve(Cr); else cairo_fill(Cr); }
    public void Clip() => cairo_clip(Cr);
    public void Save() => cairo_save(Cr);
    public void Restore() => cairo_restore(Cr);
    public void Dash(params double[] d) => cairo_set_dash(Cr, d, d.Length, 0);
    public void LineWidth(double w) => cairo_set_line_width(Cr, w);
    public void FontSize(double s) => cairo_set_font_size(Cr, s);
    public void Font(string family = "Sans", bool bold = false) => cairo_select_font_face(Cr, family, 0, bold ? 1 : 0);
    public double TextWidth(string s) { cairo_text_extents(Cr, s, out var e); return e.Width; }
    public void ShowText(string s) => cairo_show_text(Cr, s);

    /// <summary>Отрисовать в PNG (ARGB32).</summary>
    public static void RenderPng(string path, int w, int h, Action<Cairo> draw)
    {
        var surf = cairo_image_surface_create(0, w, h);
        var cr = cairo_create(surf);
        try
        {
            var c = new Cairo(cr);
            c.Font();
            draw(c);
            if (cairo_surface_write_to_png(surf, path) != 0) throw new IOException("cairo не смог записать PNG");
        }
        finally { cairo_destroy(cr); cairo_surface_destroy(surf); }
    }
}
