using Velopack;

namespace RobloxOneLauncher;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetArgs(args)
            .SetAutoApplyOnStartup(false)
            .Run();

        var application = new App();
        application.InitializeComponent();
        application.Run();
    }
}
