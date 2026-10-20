using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MinecraftDownloader.Core;

namespace MinecraftDownloader.Pages;

public partial class PageSetup : UserControl, IRefreshablePage
{
    public PageSetup()
    {
        InitializeComponent();
    }

    public void RefreshPage()
    {
        TextGameDir.Text = LauncherConfig.Current.GameDir;
        TextThreads.Text = LauncherConfig.Current.DownloadThreads.ToString();
        ComboMirror.SelectedIndex = LauncherConfig.Current.UseMirror ? 0 : 1;
        RefreshJavaList();
        LabPaths.Text = $"配置文件：{AppPaths.ConfigFile}\n游戏目录：{AppPaths.GameDir}";
    }

    private void RefreshJavaList()
    {
        var javas = JavaService.Scan(true);
        ComboJava.ItemsSource = javas;
        var configured = LauncherConfig.Current.JavaPath;
        ComboJava.SelectedItem = javas.FirstOrDefault(x =>
                                       !string.IsNullOrEmpty(configured) &&
                                       string.Equals(x.Path, configured, StringComparison.OrdinalIgnoreCase))
                                   ?? javas.FirstOrDefault();
    }

    private void BtnDetectJava_Click(object sender, RoutedEventArgs e)
    {
        RefreshJavaList();
        LabStatus.Text = "已重新检测 Java";
    }

    private void BtnBrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Java 可执行文件",
            Filter = "Java (javaw.exe;java.exe)|javaw.exe;java.exe|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        var info = JavaService.Parse(dialog.FileName);
        if (info is null)
        {
            LabStatus.Text = "无法识别该 Java，请确认文件可用";
            return;
        }

        var list = (ComboJava.ItemsSource as IEnumerable<JavaInfo>)?.ToList() ?? new List<JavaInfo>();
        list = list.Where(x => !string.Equals(x.Path, info.Path, StringComparison.OrdinalIgnoreCase))
            .Prepend(info).ToList();
        ComboJava.ItemsSource = list;
        ComboJava.SelectedItem = info;
        LabStatus.Text = $"已选择 Java {info.Version}";
    }

    private void BtnBrowseGameDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择游戏目录" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            TextGameDir.Text = dialog.FolderName;
    }

    private void BtnOpenGameDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = TextGameDir.Text.Trim();
        if (!Directory.Exists(dir))
        {
            LabStatus.Text = "游戏目录不存在";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LabStatus.Text = "打开失败：" + ex.Message;
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var gameDir = TextGameDir.Text.Trim();
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            LabStatus.Text = "游戏目录不能为空";
            return;
        }

        if (!int.TryParse(TextThreads.Text.Trim(), out var threads))
            threads = LauncherConfig.Current.DownloadThreads;

        LauncherConfig.Current.GameDir = gameDir;
        LauncherConfig.Current.DownloadThreads = Math.Clamp(threads, 1, 64);
        LauncherConfig.Current.UseMirror = ComboMirror.SelectedIndex == 0;
        if (ComboJava.SelectedItem is JavaInfo java)
            LauncherConfig.Current.JavaPath = java.Path;

        try
        {
            AppPaths.EnsureBaseDirs();
        }
        catch (Exception ex)
        {
            LabStatus.Text = "创建目录失败：" + ex.Message;
            return;
        }

        LauncherConfig.Save();
        TextThreads.Text = LauncherConfig.Current.DownloadThreads.ToString();
        LabPaths.Text = $"配置文件：{AppPaths.ConfigFile}\n游戏目录：{AppPaths.GameDir}";
        LabStatus.Text = "设置已保存";
    }
}
