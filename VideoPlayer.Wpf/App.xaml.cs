using System.Windows;
using Velopack;

namespace VideoPlayer.Wpf;

public partial class App : Application
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run(new MainWindow());
    }
}
