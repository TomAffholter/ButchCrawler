using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ButchCrawler.Operator;

internal static class ResetDialogTests
{
    public static void Run(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var inputSource = new HwndSource(new HwndSourceParameters("ResetDialogTestInput") { Width = 1, Height = 1, WindowStyle = 0 });
                var window = new DatabaseResetWindow(new DatabaseResetService(""), () => false);
                var cancel = (Button)typeof(DatabaseResetWindow).GetField("_cancel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var keepKeywords = (RadioButton)typeof(DatabaseResetWindow).GetField("_keepKeywords", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var clearKeywords = (RadioButton)typeof(DatabaseResetWindow).GetField("_clearKeywords", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var acknowledgement = (CheckBox)typeof(DatabaseResetWindow).GetField("_understand", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                check(keepKeywords.Content.ToString()!.Contains("Keep Seed Keywords") && clearKeywords.Content.ToString()!.Contains("Clear Seed Keywords"),
                    "new-project screen offers an explicit keyword choice");
                check(acknowledgement.Content.ToString()!.Contains("verified safety backup"),
                    "new-project acknowledgement promises a verified safety backup before deletion");
                check(cancel.IsCancel && cancel.IsEnabled, "new-project screen provides enabled Cancel/Escape button");
                var closed = false; window.Closed += (_, _) => closed = true;
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                check(closed, "Escape closes new-project screen without database access");

                var buttonWindow = new DatabaseResetWindow(new DatabaseResetService(""), () => false);
                var button = (Button)typeof(DatabaseResetWindow).GetField("_cancel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(buttonWindow)!;
                var buttonClosed = false; buttonWindow.Closed += (_, _) => buttonClosed = true;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(buttonClosed, "Cancel button closes new-project screen without database access");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
}
