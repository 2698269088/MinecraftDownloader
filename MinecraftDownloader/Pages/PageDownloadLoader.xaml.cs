using System.Windows;
using System.Windows.Controls;
using MinecraftDownloader.Core;
using MinecraftDownloader.Core.Models;
using MinecraftDownloader.Dialogs;

namespace MinecraftDownloader.Pages;

public partial class PageDownloadLoader : UserControl, IRefreshablePage
{
    private bool _loaded;
    private int _loadToken;

    public PageDownloadLoader()
    {
        InitializeComponent();
    }

    public async void RefreshPage()
    {
        if (_loaded)
            return;
        _loaded = true;
        try
        {
            var manifest = await VersionService.GetManifestAsync();
            var releases = manifest.Versions
                .Where(x => x.Type == "release")
                .OrderByDescending(x => x.ReleaseTime ?? DateTime.MinValue)
                .ToList();
            ComboMcVersion.ItemsSource = releases;
            ComboMcVersion.SelectedItem = releases.FirstOrDefault(x => x.Id == manifest.Latest?.Release)
                                          ?? releases.FirstOrDefault();
            await LoadLoaderVersionsAsync();
        }
        catch (Exception ex)
        {
            LabHint.Text = "获取版本列表失败：" + ex.Message;
        }
    }

    private async void ComboLoader_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await LoadLoaderVersionsAsync();
    }

    private async void ComboMcVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await LoadLoaderVersionsAsync();
    }

    private LoaderKind SelectedLoader => ComboLoader.SelectedItem is ComboBoxItem item
        ? item.Content?.ToString() switch
        {
            "Quilt" => LoaderKind.Quilt,
            "Forge" => LoaderKind.Forge,
            "NeoForge" => LoaderKind.NeoForge,
            _ => LoaderKind.Fabric
        }
        : LoaderKind.Fabric;

    private async Task LoadLoaderVersionsAsync()
    {
        if (ComboMcVersion.SelectedItem is not VersionSummary version)
            return;

        var token = ++_loadToken;
        var kind = SelectedLoader;
        BarLoader.Visibility = Visibility.Visible;
        ComboLoaderVersion.IsEnabled = false;
        LabHint.Text = $"正在获取 {kind} 版本列表…";
        try
        {
            var versions = await Installer.GetLoaderVersionsAsync(kind, version.Id, CancellationToken.None);
            if (token != _loadToken)
                return;

            ComboLoaderVersion.ItemsSource = versions;
            ComboLoaderVersion.SelectedIndex = versions.Count > 0 ? 0 : -1;
            LabHint.Text = versions.Count == 0
                ? $"该游戏版本没有可用的 {kind} 版本"
                : $"共 {versions.Count} 个 {kind} 版本";
        }
        catch (Exception ex)
        {
            if (token == _loadToken)
                LabHint.Text = "获取加载器版本失败：" + ex.Message;
        }
        finally
        {
            if (token == _loadToken)
            {
                BarLoader.Visibility = Visibility.Collapsed;
                ComboLoaderVersion.IsEnabled = true;
            }
        }
    }

    private async void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        if (ComboMcVersion.SelectedItem is not VersionSummary version)
        {
            MessageBox.Show(Window.GetWindow(this)!, "请先选择游戏版本", "安装模组加载器",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var kind = SelectedLoader;
        var loaderVersion = ComboLoaderVersion.SelectedItem as string;
        var owner = Window.GetWindow(this);
        BtnInstall.IsEnabled = false;

        var (ok, instanceId, error) = await ProgressDialog.RunAsync(owner,
            $"安装 {kind} ({version.Id})",
            (progress, ct) => Installer.InstallLoaderAsync(kind, version.Id, loaderVersion, progress, ct));

        BtnInstall.IsEnabled = true;
        if (ok)
        {
            MessageBox.Show(owner!, $"{kind} 安装完成：\n{instanceId}\n\n可在「版本管理」页查看。", "安装完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(owner!, error ?? "安装失败", "安装失败", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
