using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MinecraftDownloader.Core;
using MinecraftDownloader.Dialogs;

namespace MinecraftDownloader.Pages;

public partial class PageDownloadModpack : UserControl, IRefreshablePage
{
    private List<ModrinthProject> _results = new();
    private bool _busy;

    public PageDownloadModpack()
    {
        InitializeComponent();
    }

    public void RefreshPage()
    {
    }

    private async void BtnSearch_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void TextSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await SearchAsync();
    }

    private async Task SearchAsync()
    {
        if (_busy)
            return;
        _busy = true;
        BarBusy.Visibility = Visibility.Visible;
        LabStatus.Text = "正在搜索…";
        try
        {
            var hits = await ModrinthService.SearchAsync(TextSearch.Text.Trim(), "modpack", limit: 20);
            _results = hits;
            ListPacks.ItemsSource = null;
            ListPacks.ItemsSource = _results;
            LabEmpty.Text = _results.Count == 0 ? "没有找到匹配的整合包" : "";
            LabEmpty.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LabStatus.Text = _results.Count == 0 ? "" : $"共 {_results.Count} 个结果";
        }
        catch (Exception ex)
        {
            LabStatus.Text = "搜索失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            BarBusy.Visibility = Visibility.Collapsed;
        }
    }

    private async void BtnInstallPack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ModrinthProject project })
            return;

        var owner = Window.GetWindow(this);
        var (ok, instanceId, error) = await ProgressDialog.RunAsync(owner, $"安装整合包：{project.Title}",
            async (progress, ct) =>
            {
                var versions = await ModrinthService.GetVersionsAsync(project.Id, ct: ct);
                var version = versions.FirstOrDefault()
                              ?? throw new InvalidOperationException("该整合包没有可下载的版本");
                var file = version.Files.FirstOrDefault(x => x.Primary)
                           ?? version.Files.FirstOrDefault()
                           ?? throw new InvalidOperationException("整合包版本中没有文件");
                return await ModpackService.DownloadAndInstallAsync(file.Url, file.FileName, progress, ct);
            });

        if (ok)
        {
            LabStatus.Text = $"整合包已安装：{instanceId}";
            MessageBox.Show(owner!, $"整合包已安装为版本「{instanceId}」。\n请到「版本管理」页查看。", "安装完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(owner!, error ?? "安装失败", "安装失败", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择整合包",
            Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        var owner = Window.GetWindow(this);
        var (ok, instanceId, error) = await ProgressDialog.RunAsync(owner, "导入整合包",
            (progress, ct) => ModpackService.InstallMrpackAsync(dialog.FileName, progress, ct));

        if (ok)
            MessageBox.Show(owner!, $"整合包已导入为版本「{instanceId}」。", "导入完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(owner!, error ?? "导入失败", "导入失败", MessageBoxButton.OK,
                MessageBoxImage.Warning);
    }
}
