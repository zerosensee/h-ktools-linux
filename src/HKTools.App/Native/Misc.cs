using System.Runtime.InteropServices;

namespace HKTools.Ui.Native;

internal static class ActionGroups
{
    [DllImport(Lib.Gtk)] static extern void gtk_widget_insert_action_group(IntPtr w, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr group);
    public static void Insert(W w, string name, GObj group) => gtk_widget_insert_action_group(w.H, name, group.H);
}

internal static class Popovers
{
    [DllImport(Lib.Gtk)] static extern void gtk_popover_popdown(IntPtr p);
    public static void Popdown(W p) => gtk_popover_popdown(p.H);
}

internal static class Grid
{
    [DllImport(Lib.Gtk)] static extern void gtk_grid_attach(IntPtr g, IntPtr child, int col, int row, int w, int h);
    public static void Attach(W grid, W child, int col, int row, int w = 1, int h = 1) => gtk_grid_attach(grid.H, child.H, col, row, w, h);
}

internal static class Spin
{
    [DllImport(Lib.Gtk)] static extern IntPtr gtk_spin_button_new_with_range(double min, double max, double step);
    public static W New(double min, double max, double step) => new(gtk_spin_button_new_with_range(min, max, step));
}
