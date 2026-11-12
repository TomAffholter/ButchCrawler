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
                foreach (var clearKeywords in new[] { false, true })
                {
                    var window = new DatabaseResetWindow(new DatabaseResetService(""), clearKeywords, () => false);
                    var warning = (string)typeof(DatabaseResetWindow).GetField("_deleteWarning", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                    var cancel = (Button)typeof(DatabaseResetWindow).GetField("_cancel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                    check(warning.Contains("cannot be undone") && warning.Contains("Seed URLs") && warning.Contains("OpenAI key"), "reset warns about irreversible deletion and preserved keys, clearKeywords=" + clearKeywords);
                    check(clearKeywords ? warning.Contains("Seed Keywords will be permanently deleted") : warning.Contains("Seed Keywords and your OpenAI key will be kept"),
                        "reset warning explains keyword treatment, clearKeywords=" + clearKeywords);
                    check(cancel.IsCancel && cancel.IsEnabled, "reset provides enabled Cancel/Escape button");
                    var closed = false; window.Closed += (_, _) => closed = true;
                    window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    check(closed, "Escape closes reset without starting deletion, clearKeywords=" + clearKeywords);
                }
                var buttonWindow = new DatabaseResetWindow(new DatabaseResetService(""), false, () => false);
                var button = (Button)typeof(DatabaseResetWindow).GetField("_cancel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(buttonWindow)!;
                var buttonClosed = false; buttonWindow.Closed += (_, _) => buttonClosed = true;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(buttonClosed, "Cancel button closes reset without database access");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
}
