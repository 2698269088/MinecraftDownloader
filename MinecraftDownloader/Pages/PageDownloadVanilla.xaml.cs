using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MinecraftDownloader.Core;
using MinecraftDownloader.Core.Models;
using MinecraftDownloader.Dialogs;

namespace MinecraftDownloader.Pages;

public partial class PageDownloadVanilla : UserControl, IRefreshablePage
{
    private List<VersionSummary> _manifest = new();
    private bool _loaded;

    public PageDownloadVanilla()
    {
        InitializeComponent();
    }

    public async void RefreshPage()
    {
        if (_loaded)
            return;
        _loaded = true;
        await LoadVersionsAsync(false);
    }

    private async Task LoadVersionsAsync(bool force)
    {
        BarBusy.Visibility = Visibility.Visible;
        LabStatus.Text = "正在获取版本列表…";
        LabEmpty.Visibility = Visibility.Visible;
        try
        {
            var manifest = await VersionService.GetManifestAsync(force);
            _manifest = manifest.Versions;
            ApplyFilter();
            LabStatus.Text = $"共 {_manifest.Count} 个版本";
        }
        catch (Exception ex)
        {
            LabEmpty.Text = "获取版本列表失败：" + ex.Message;
            LabStatus.Text = "获取失败，可点击「刷新列表」重试";
        }
        finally
        {
            BarBusy.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyFilter()
    {
        if (ListVersions is null || ComboFilter is null)
            return;

        var type = ComboFilter.SelectedItem is ComboBoxItem item ? item.Content?.ToString() : "正式版";
        var keyword = TextSearch?.Text.Trim() ?? "";
        IEnumerable<VersionSummary> query = _manifest;

        query = type switch
        {
            "正式版" => query.Where(x => x.Type == "release"),
            "快照" => query.Where(x => x.Type == "snapshot"),
            _ => query
        };

        if (!string.IsNullOrEmpty(keyword))
            query = query.Where(x => x.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        var rows = query.Select(x => new VersionRow
        {
            Id = x.Id,
            Type = x.Type,
            TypeText = x.Type switch
            {
                "release" => "正式版",
                "snapshot" => "快照",
                "old_beta" => "Beta",
                "old_alpha" => "Alpha",
                _ => x.Type
            },
            TimeText = x.ReleaseTime?.ToLocalTime().ToString("yyyy-MM-dd") ?? "",
            BadgeBrush = new SolidColorBrush(x.Type switch
            {
                "release" => Color.FromRgb(0x0b, 0x5b, 0xcb),
                "snapshot" => Color.FromRgb(0xb0, 0x6a, 0x00),
                _ => Color.FromRgb(0x60, 0x60, 0x60)
            }),
            BadgeBackground = new SolidColorBrush(x.Type switch
            {
                "release" => Color.FromRgb(0xe0, 0xea, 0xfd),
                "snapshot" => Color.FromRgb(0xfd, 0xf0, 0xd8),
                _ => Color.FromRgb(0xf0, 0xf0, 0xf0)
            })
        }).ToList();

        ListVersions.ItemsSource = rows;
        LabEmpty.Text = rows.Count == 0 ? "没有匹配的版本" : "";
        LabEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VersionRow row })
            return;

        var owner = Window.GetWindow((DependencyObject)sender);
        var (ok, instanceId, error) = await ProgressDialog.RunAsync(owner, $"安装 Minecraft {row.Id}",
            (progress, ct) => Installer.InstallVanillaAsync(row.Id, progress, ct));
        if (ok)
        {
            LogService.Info($"Minecraft {row.Id} 安装完成");
            MessageBox.Show(owner!, $"Minecraft {row.Id} 安装完成。\n可在「版本管理」页查看，或直接用 PCL 等启动器启动。", "安装完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(owner!, error ?? "安装失败", "安装失败", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ComboFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void TextSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadVersionsAsync(true);
}

public sealed class VersionRow
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string TypeText { get; set; } = "";
    public string TimeText { get; set; } = "";
    public Brush BadgeBrush { get; set; } = Brushes.Gray;
    public Brush BadgeBackground { get; set; } = Brushes.WhiteSmoke;
}
