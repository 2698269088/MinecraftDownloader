using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MinecraftDownloader.Core;
using MinecraftDownloader.Dialogs;

namespace MinecraftDownloader.Pages;

public partial class PageDownloadMod : UserControl, IRefreshablePage
{
    private List<ModrinthProject> _results = new();
    private int _offset;
    private string _currentQuery = "";
    private bool _busy;

    public PageDownloadMod()
    {
        InitializeComponent();
    }

    public void RefreshPage()
    {
        var instances = InstanceService.GetAll();
        var keep = (ComboInstance.SelectedItem as InstanceInfo)?.Id ?? LauncherConfig.Current.LastInstance;
        ComboInstance.ItemsSource = instances;
        ComboInstance.SelectedItem = instances.FirstOrDefault(x => x.Id == keep) ?? instances.FirstOrDefault();
        if (instances.Count == 0)
            LabEmpty.Text = "还没有安装任何实例，请先下载游戏版本";
    }

    private void ComboInstance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboInstance.SelectedItem is not InstanceInfo instance)
            return;

        ComboLoader.SelectedIndex = instance.Loader switch
        {
            "Fabric" => 1,
            "Forge" => 2,
            "Quilt" => 3,
            "NeoForge" => 4,
            _ => 0
        };
    }

    private string? SelectedLoaderName => ComboLoader.SelectedItem is ComboBoxItem item
        ? item.Content?.ToString() switch
        {
            "Fabric" => "fabric",
            "Forge" => "forge",
            "Quilt" => "quilt",
            "NeoForge" => "neoforge",
            _ => null
        }
        : null;

    private async void BtnSearch_Click(object sender, RoutedEventArgs e) => await SearchAsync(true);

    private async void TextSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await SearchAsync(true);
    }

    private async void BtnMore_Click(object sender, RoutedEventArgs e)
    {
        _offset += 20;
        await SearchAsync(false);
    }

    private async Task SearchAsync(bool reset)
    {
        if (_busy)
            return;

        _busy = true;
        BarBusy.Visibility = Visibility.Visible;
        BtnMore.Visibility = Visibility.Collapsed;
        if (reset)
        {
            _offset = 0;
            _results.Clear();
            _currentQuery = TextSearch.Text.Trim();
            ListMods.ItemsSource = null;
        }

        var instance = ComboInstance.SelectedItem as InstanceInfo;
        var loader = SelectedLoaderName ?? instance?.Loader switch
        {
            "Fabric" => "fabric",
            "Forge" => "forge",
            "Quilt" => "quilt",
            "NeoForge" => "neoforge",
            _ => null
        };
        var gameVersion = instance?.McVersion;

        LabStatus.Text = "正在搜索…";
        try
        {
            var hits = await ModrinthService.SearchAsync(_currentQuery, "mod", gameVersion, loader, 20, _offset);
            _results.AddRange(hits);
            ListMods.ItemsSource = null;
            ListMods.ItemsSource = _results;
            LabEmpty.Text = _results.Count == 0 ? "没有找到匹配的模组" : "";
            LabEmpty.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LabStatus.Text = _results.Count == 0 ? "" : $"已显示 {_results.Count} 个结果";
            BtnMore.Visibility = hits.Count >= 20 ? Visibility.Visible : Visibility.Collapsed;
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

    private async void BtnDownloadMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ModrinthProject project })
            return;
        if (ComboInstance.SelectedItem is not InstanceInfo instance)
        {
            MessageBox.Show(Window.GetWindow(this)!, "请先选择要安装到的实例", "下载模组",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var owner = Window.GetWindow(this);
        var loader = SelectedLoaderName ?? instance.Loader switch
        {
            "Fabric" => "fabric",
            "Forge" => "forge",
            "Quilt" => "quilt",
            "NeoForge" => "neoforge",
            _ => null
        };

        var (ok, path, error) = await ProgressDialog.RunAsync(owner, $"下载模组：{project.Title}",
            async (progress, ct) =>
            {
                var versions = await ModrinthService.GetVersionsAsync(project.Id, instance.McVersion, loader, ct);
                var version = versions.FirstOrDefault()
                              ?? throw new InvalidOperationException(
                                  $"该模组没有适用于 {instance.McVersion} / {loader ?? "任意加载器"} 的版本");
                return await ModrinthService.DownloadVersionAsync(version, InstanceService.GetModsDir(instance.Id),
                    progress, ct);
            });

        if (ok)
            LabStatus.Text = $"已下载到：{path}";
        else
            MessageBox.Show(owner!, error ?? "下载失败", "下载失败", MessageBoxButton.OK,
                MessageBoxImage.Warning);
    }
}
