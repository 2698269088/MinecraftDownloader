using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MinecraftDownloader.Pages;

namespace MinecraftDownloader;

public interface IRefreshablePage
{
    void RefreshPage();
}

public partial class FormMain : Window
{
    private readonly Dictionary<string, UserControl> _pages = new();

    public static FormMain? Current { get; private set; }

    public FormMain()
    {
        InitializeComponent();
        Current = this;
        Loaded += (_, _) => SelectTab("home");
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || PanMain is null)
            return;
        var key = sender == TabHome ? "home"
            : sender == TabDownload ? "download"
            : sender == TabInstance ? "instance"
            : "setup";
        ShowPage(key);
    }

    public void SelectTab(string key)
    {
        switch (key)
        {
            case "home":
                TabHome.IsChecked = true;
                break;
            case "download":
                TabDownload.IsChecked = true;
                break;
            case "instance":
                TabInstance.IsChecked = true;
                break;
            case "setup":
                TabSetup.IsChecked = true;
                break;
        }

        ShowPage(key);
    }

    public void NavigateDownload(string subPage)
    {
        SelectTab("download");
        if (PanMain.Content is PageDownload downloadPage)
            downloadPage.SelectTab(subPage);
    }

    private void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "download" => new PageDownload(),
                "instance" => new PageInstance(),
                "setup" => new PageSetup(),
                _ => new PageHome()
            };
            _pages[key] = page;
        }

        PanMain.Content = page;
        if (page is IRefreshablePage refreshable)
            refreshable.RefreshPage();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Maximized)
        {
            MaxHeight = SystemParameters.WorkArea.Height;
            MaxWidth = SystemParameters.WorkArea.Width;
        }
        else
        {
            MaxHeight = double.PositiveInfinity;
            MaxWidth = double.PositiveInfinity;
        }
    }
}
