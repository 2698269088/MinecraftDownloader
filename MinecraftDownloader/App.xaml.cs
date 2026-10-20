using System.Windows;
using System.Windows.Threading;
using MinecraftDownloader.Core;

namespace MinecraftDownloader;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LauncherConfig.Load();
        AppPaths.EnsureBaseDirs();
        DispatcherUnhandledException += OnUnhandledException;

        var window = new FormMain();
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Error(e.Exception.ToString());
        MessageBox.Show(e.Exception.Message, "MinecraftDownloader 出现错误", MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
