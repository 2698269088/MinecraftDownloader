using System.Text;
using System.Windows;
using System.Windows.Input;
using MinecraftDownloader.Core;

namespace MinecraftDownloader.Dialogs;

public partial class ProgressDialog : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _log = new();
    private bool _finished;

    private ProgressDialog(string title)
    {
        InitializeComponent();
        LabTitle.Text = title;
        LogService.Logged += OnLogged;
        Closed += (_, _) => LogService.Logged -= OnLogged;
    }

    private void OnLogged(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnLogged(line));
            return;
        }

        _log.AppendLine(line);
        if (_log.Length > 12000)
            _log.Remove(0, _log.Length - 10000);
        LabLog.Text = _log.ToString();
        LogScroll.ScrollToEnd();
    }

    private void UpdateProgress(InstallProgress progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateProgress(progress));
            return;
        }

        var text = string.IsNullOrWhiteSpace(progress.Detail)
            ? progress.Stage
            : $"{progress.Stage}：{progress.Detail}";
        if (!string.IsNullOrWhiteSpace(text))
            LabStage.Text = text;

        if (progress.Percent is { } percent)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = Math.Clamp(percent, 0, 100);
        }
        else
        {
            Bar.IsIndeterminate = true;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_finished)
        {
            Close();
            return;
        }

        BtnCancel.IsEnabled = false;
        BtnCancel.Content = "正在取消…";
        LabStage.Text = "正在取消，请稍候…";
        _cts.Cancel();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        if (!_finished)
            _cts.Cancel();
        Close();
    }

    private void Title_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    public static Task<(bool Ok, T? Result, string? Error)> RunAsync<T>(Window? owner, string title,
        Func<IProgress<InstallProgress>, CancellationToken, Task<T>> work)
    {
        var completion = new TaskCompletionSource<(bool, T?, string?)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new ProgressDialog(title) { Owner = owner };

        dialog.Loaded += async (_, _) =>
        {
            var progress = new Progress<InstallProgress>(dialog.UpdateProgress);
            try
            {
                var result = await Task.Run(() => work(progress, dialog._cts.Token));
                completion.TrySetResult((true, result, null));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetResult((false, default, "操作已取消"));
            }
            catch (Exception ex)
            {
                LogService.Error(ex.Message);
                completion.TrySetResult((false, default, ex.Message));
            }
            finally
            {
                dialog._finished = true;
                dialog.Close();
            }
        };

        dialog.ShowDialog();
        return completion.Task;
    }

    public static async Task<(bool Ok, string? Error)> RunAsync(Window? owner, string title,
        Func<IProgress<InstallProgress>, CancellationToken, Task> work)
    {
        var (ok, _, error) = await RunAsync(owner, title,
            async (progress, ct) =>
            {
                await work(progress, ct).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(true);
        return (ok, error);
    }
}
