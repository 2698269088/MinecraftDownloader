using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MinecraftDownloader.Core;
using MinecraftDownloader.Dialogs;

namespace MinecraftDownloader.Pages;

public partial class PageHome : UserControl, IRefreshablePage
{
    private InstanceInfo? _selected;

    public PageHome()
    {
        InitializeComponent();
        LogService.Logged += AppendLog;
        Unloaded += (_, _) => LogService.Logged -= AppendLog;
        foreach (var line in LogService.Snapshot())
            AppendLog(line);
    }

    public void RefreshPage()
    {
        var instances = InstanceService.GetAll();
        var keep = _selected?.Id ?? LauncherConfig.Current.LastInstance;
        ListInstances.ItemsSource = instances;
        LabCount.Text = instances.Count == 0 ? "" : $"{instances.Count} 个";
        _selected = instances.FirstOrDefault(x => x.Id == keep) ?? instances.FirstOrDefault();
        ListInstances.SelectedItem = _selected;
        UpdateSelected();
    }

    private void ListInstances_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = ListInstances.SelectedItem as InstanceInfo;
        UpdateSelected();
    }

    private void UpdateSelected()
    {
        var hasInstance = _selected is not null;
        BtnOpenFolder.IsEnabled = hasInstance;
        BtnMods.IsEnabled = hasInstance;
        BtnExportPack.IsEnabled = hasInstance;
        BtnDelete.IsEnabled = hasInstance;

        if (_selected is null)
        {
            LabInstanceTitle.Text = "还没有安装任何版本";
            LabInstanceSub.Text = "点击左侧「下载新版本」开始安装";
            LabLoader.Text = "未选择";
            LabId.Text = LabMc.Text = LabMods.Text = LabPath.Text = "-";
            return;
        }

        LabInstanceTitle.Text = _selected.DisplayName;
        LabInstanceSub.Text = _selected.SubTitle;
        LabLoader.Text = _selected.Loader;
        LabId.Text = _selected.Id;
        LabMc.Text = _selected.McVersion;
        LabPath.Text = _selected.Directory;
        LabMods.Text = CountMods(_selected.Id).ToString();
        LauncherConfig.Current.LastInstance = _selected.Id;
        LauncherConfig.Save();
    }

    private static int CountMods(string instanceId)
    {
        var modsDir = Path.Combine(AppPaths.GetInstanceDir(instanceId), "mods");
        return Directory.Exists(modsDir) ? Directory.GetFiles(modsDir, "*.jar").Length : 0;
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
            return;
        OpenFolder(_selected.Directory);
    }

    private void BtnMods_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
            return;
        OpenFolder(InstanceService.GetModsDir(_selected.Id));
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "无法打开文件夹", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BtnExportPack_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "导出整合包",
            Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack",
            FileName = _selected.Id + ".mrpack"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        var (ok, error) = await ProgressDialog.RunAsync(Window.GetWindow(this), "导出整合包",
            (progress, ct) => ModpackService.ExportMrpackAsync(_selected.Id, dialog.FileName, progress, ct));
        if (ok)
            MessageBox.Show(Window.GetWindow(this)!, "整合包已导出到：\n" + dialog.FileName, "导出完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(Window.GetWindow(this)!, error ?? "导出失败", "导出失败", MessageBoxButton.OK,
                MessageBoxImage.Warning);
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
            return;
        if (MessageBox.Show(Window.GetWindow(this)!,
                $"确定要删除版本「{_selected.DisplayName}」吗？\n此操作会删除该版本的所有模组与配置，且无法恢复。",
                "删除版本", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            InstanceService.Delete(_selected.Id);
            _selected = null;
            RefreshPage();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "删除失败：" + ex.Message, "删除版本",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnDownload_Click(object sender, RoutedEventArgs e) => FormMain.Current?.SelectTab("download");

    private void BtnQuickVanilla_Click(object sender, RoutedEventArgs e) => FormMain.Current?.NavigateDownload("vanilla");

    private void BtnQuickLoader_Click(object sender, RoutedEventArgs e) => FormMain.Current?.NavigateDownload("loader");

    private void BtnQuickMod_Click(object sender, RoutedEventArgs e) => FormMain.Current?.NavigateDownload("mod");

    private void BtnQuickPack_Click(object sender, RoutedEventArgs e) => FormMain.Current?.NavigateDownload("modpack");

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => TxtLog.Clear();

    private void AppendLog(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(line));
            return;
        }

        TxtLog.AppendText(line + Environment.NewLine);
        if (TxtLog.LineCount > 400)
            TxtLog.Text = string.Join(Environment.NewLine, TxtLog.Text.Split('\n').TakeLast(300));
        TxtLog.ScrollToEnd();
    }
}
