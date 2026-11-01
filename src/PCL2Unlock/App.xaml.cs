using System;
using System.Windows;

namespace PCL2Unlock
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (Cli.LooksLikeCli(e.Args))
            {
                Shutdown(Cli.Run(e.Args));
                return;
            }
            var win = new MainWindow();
            win.Show();
        }
    }
}
