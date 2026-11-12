using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ButchCrawler.App;

namespace ButchCrawler.Operator;

public sealed class OpenAiKeyWindow : Window
{
    private readonly OpenAiKeyStore _store;
    private readonly TextBox _key = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 95, MaxHeight = 150,
        IsUndoEnabled = false, SpellCheck = { IsEnabled = false } };
    private readonly Button _decrypt = new() { Content = "Decrypt and edit", Padding = new Thickness(12, 7, 12, 7) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) };
    private bool _editing;
    private string _original = string.Empty;
    private readonly Func<OpenAiFeatureSelection?>? _addedKeyPrompt;
    private readonly Func<MessageBoxResult>? _removedKeyPrompt;

    public OpenAiKeyWindow(OpenAiKeyStore? store = null, Func<OpenAiFeatureSelection?>? addedKeyPrompt = null,
        Func<MessageBoxResult>? removedKeyPrompt = null)
    {
        AppTheme.Apply(this);
        _store = store ?? new OpenAiKeyStore();
        _addedKeyPrompt = addedKeyPrompt;
        _removedKeyPrompt = removedKeyPrompt;
        Title = "OpenAI API key"; Width = 650; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "OpenAI API key", FontSize = 20, FontWeight = FontWeights.SemiBold });
        _key.Text = _store.ReadEncrypted();
        panel.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(_key.Text)
            ? "Enter or paste your OpenAI key. It will be encrypted and saved when you close this window."
            : "The encrypted value is read-only. Choose Decrypt and edit to view or change the key. Changes save encrypted when you close this window.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) });
        panel.Children.Add(_key);
        _status.Text = "Encrypted and locked.";
        panel.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _decrypt.Click += (_, _) => Unlock(); buttons.Children.Add(_decrypt);
        var close = new Button { Content = "Save and close", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(10, 0, 0, 0) };
        close.SetResourceReference(StyleProperty, "PrimaryButton");
        close.Click += (_, _) => Close(); buttons.Children.Add(close); panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = "Saved with Windows encryption in your private user-data folder, outside the program and Git project. Restart the crawler after changes. Adding or removing a key lets you choose which optional AI features are enabled. Ordinary crawling and collection search do not require a key.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        Content = panel;
        if (string.IsNullOrEmpty(_key.Text)) EnableEditing(isNew: true);
        ContentRendered += (_, _) => { if (_editing) FocusEditor(); };
        Closing += (_, e) =>
        {
            if (!_editing) return;
            try
            {
                var edited = _key.Text.Trim();
                if (edited == _original) return;
                OpenAiFeatureSelection? features = null;
                if (_original.Length == 0 && edited.Length > 0)
                    features = _addedKeyPrompt?.Invoke() ?? (IsLoaded ? PromptForAddedKey() : null);
                else if (_original.Length > 0 && edited.Length == 0)
                {
                    var choice = _removedKeyPrompt?.Invoke() ?? (IsLoaded ? PromptForRemovedKey() : MessageBoxResult.No);
                    if (choice == MessageBoxResult.Cancel)
                    {
                        e.Cancel = true;
                        _status.Text = "Key deletion canceled. The saved key and AI settings are unchanged.";
                        return;
                    }
                    if (choice == MessageBoxResult.Yes) features = OpenAiFeatureSelection.Disabled;
                }
                _store.Save(edited, features);
                _original = edited;
            }
            catch
            {
                e.Cancel = true;
                _status.Text = "Could not save the key. Check file access and ensure the key contains no spaces or line breaks, then close again.";
            }
        };
        Closed += (_, _) => { _key.Clear(); _original = string.Empty; };
    }

    private OpenAiFeatureSelection PromptForAddedKey()
    {
        var choice = new AiFeatureSetupWindow { Owner = this };
        return choice.ShowDialog() == true ? choice.Selection : OpenAiFeatureSelection.Disabled;
    }

    private MessageBoxResult PromptForRemovedKey() => MessageBox.Show(this,
        "Without an OpenAI key, new AI research, transcription, and indexing cannot run. Previously saved results and transcripts will remain available.\n\n" +
        "Choose Yes to delete the key and turn off all paid AI features.\nChoose No to delete only this saved key (for example, if an advanced configuration supplies another key).",
        "Delete OpenAI key and turn off AI?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);

    private void Unlock()
    {
        try
        {
            var encrypted = _key.Text;
            _original = string.IsNullOrEmpty(encrypted) ? string.Empty : OpenAiKeyStore.Decrypt(encrypted);
        }
        catch
        {
            _status.Text = "Cannot decrypt this key. Open it using the Windows account and computer that saved it. The stored key has not changed.";
            return;
        }
        _key.Text = _original;
        EnableEditing(isNew: false);
        FocusEditor();
    }

    private void EnableEditing(bool isNew)
    {
        _key.IsReadOnly = false;
        _key.IsEnabled = true;
        _key.Focusable = true;
        _editing = true;
        _decrypt.IsEnabled = false;
        _status.Text = isNew
            ? "No saved key. Enter or paste your OpenAI key, then close to save encrypted."
            : "Decrypted — you can now edit. Close this window to save encrypted.";
    }

    private void FocusEditor()
    {
        // Run after the clicked button has finished processing focus changes.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!_editing || !IsVisible) return;
            FocusManager.SetFocusedElement(this, _key);
            Keyboard.Focus(_key);
            _key.Focus();
            _key.SelectAll();
        }));
    }
}
