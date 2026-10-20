using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MinecraftDownloader.Core;
using MinecraftDownloader.Dialogs;

namespace MinecraftDownloader.Pages;

public partial class PageInstance : UserControl, IRefreshablePage
{
    private InstanceInfo? _selected;

    public PageInstance()
    {
        InitializeComponent();
    }

    public void RefreshPage()
    {
        var instances = InstanceService.GetAll();
        var keep = _selected?.Id;
        ListInstances.ItemsSource = instances;
        LabCount.Text = instances.Count == 0 ? "" : $"{instances.Count} 个";
        _selected = instances.FirstOrDefault(x => x.Id == keep) ?? instances.FirstOrDefault();
        ListInstances.SelectedItem = _selected;
        UpdateDetails();
    }

    private void ListInstances_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = ListInstances.SelectedItem as InstanceInfo;
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        var instance = _selected;
        var has = instance is not null;
        BtnFolder.IsEnabled = has;
        BtnMods.IsEnabled = has;
        BtnExport.IsEnabled = has;
        BtnDelete.IsEnabled = has;

        if (instance is null)
        {
            LabTitle.Text = "请选择一个版本";
            LabSub.Text = "";
            LabId.Text = LabLoader.Text = LabMc.Text = LabPath.Text = "-";
            ListMods.ItemsSource = null;
            LabModsEmpty.Visibility = Visibility.Visible;
            return;
        }

        LabTitle.Text = instance.DisplayName;
        LabSub.Text = instance.SubTitle;
        LabId.Text = instance.Id;
        LabLoader.Text = instance.Loader;
        LabMc.Text = instance.McVersion;
        LabPath.Text = instance.Directory;

        var modsDir = InstanceService.GetModsDir(instance.Id);
        var mods = Directory.Exists(modsDir)
            ? Directory.GetFiles(modsDir).Select(Path.GetFileName).Where(x => x is not null).ToList()
            : new List<string?>();
        ListMods.ItemsSource = mods;
        LabModsEmpty.Visibility = mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LabStatus.Text = "";
    }

    private void BtnFolder_Click(object sender, RoutedEventArgs e)
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

    private async void BtnExport_Click(object sender, RoutedEventArgs e)
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
            MessageBox.Show(Window.GetWindow(this)!, "已导出到：\n" + dialog.FileName, "导出完成",
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
                $"确定删除版本「{_selected.DisplayName}」？该操作不可恢复。", "删除版本",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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
}
