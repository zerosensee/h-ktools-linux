namespace HKTools;

internal static class Program
{
    static int Main(string[] args)
    {
        Ui.App.RegisterRenderers();
        return Cli.CliMain.Run(args, (files, tool) => Ui.App.Run(files, tool));
    }
}
