using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ButchCrawler.Operator;

internal static class ExitUiTests
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunCase(false);
                RunCase(true);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new Exception("Exit UI checks timed out.");
        if (failure != null) throw new Exception("Exit UI checks failed.", failure);
    }

    private static void RunCase(bool running)
    {
        using var process = running ? Process.Start(new ProcessStartInfo(
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60")
            { UseShellExecute = false, CreateNoWindow = true }) : null;
        var childId = process?.Id;
        var window = new MainWindow();
        typeof(MainWindow).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, process);
        var frame = new DispatcherFrame();
        var closed = false;
        window.Closed += (_, _) => { closed = true; frame.Continue = false; };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += (_, _) => { frame.Continue = false; };
        try
        {
            window.Show();
            timer.Start();
            if (running) window.Close();
            else ((MenuItem)window.FindName("ExitMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.PushFrame(frame);
            if (!closed) throw new Exception("Window did not close.");
            if (process != null)
            {
                // MainWindow disposes its process handle; independently verify its PID ended.
                try
                {
                    using var remaining = Process.GetProcessById(childId!.Value);
                    if (!remaining.HasExited) throw new Exception("Test child remained running.");
                }
                catch (ArgumentException) { /* PID no longer exists. */ }
            }
            Console.WriteLine(running ? "PASS: window close stops owned process and closes" : "PASS: Settings Exit closes idle window without recursive Closing error");
        }
        finally
        {
            timer.Stop();
            if (process != null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
        }
    }
}
