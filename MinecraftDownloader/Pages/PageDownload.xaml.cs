using System.Windows;
using System.Windows.Controls;
using MinecraftDownloader.Core;

namespace MinecraftDownloader.Pages;

public partial class PageDownload : UserControl, IRefreshablePage
{
    private readonly Dictionary<string, UserControl> _pages = new();

    public PageDownload()
    {
        InitializeComponent();
    }

    public void SelectTab(string key)
    {
        switch (key)
        {
            case "loader":
                NavLoader.IsChecked = true;
                break;
            case "mod":
                NavMod.IsChecked = true;
                break;
            case "modpack":
                NavPack.IsChecked = true;
                break;
            default:
                NavVanilla.IsChecked = true;
                break;
        }
    }

    public void RefreshPage()
    {
        var key = NavLoader.IsChecked == true ? "loader"
            : NavMod.IsChecked == true ? "mod"
            : NavPack.IsChecked == true ? "modpack"
            : "vanilla";
        ShowPage(key);
        LabSource.Text = LauncherConfig.Current.UseMirror ? "BMCLAPI 镜像" : "官方源";
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || PanContent is null)
            return;
        var key = sender == NavLoader ? "loader"
            : sender == NavMod ? "mod"
            : sender == NavPack ? "modpack"
            : "vanilla";
        ShowPage(key);
    }

    private void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "loader" => new PageDownloadLoader(),
                "mod" => new PageDownloadMod(),
                "modpack" => new PageDownloadModpack(),
                _ => new PageDownloadVanilla()
            };
            _pages[key] = page;
        }

        PanContent.Content = page;
        if (page is IRefreshablePage refreshable)
            refreshable.RefreshPage();
    }
}
