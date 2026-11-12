using System.Windows;
using System.Windows.Controls;
using ButchCrawler.App;

namespace ButchCrawler.Operator;

public sealed class HumanVerificationWindow : Window
{
    private readonly string _url;
    private readonly string? _userAgent;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    private readonly Button _continue = new() { Content = "Continue crawling", IsEnabled = false, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
    private InteractiveHumanVerificationSession? _session;
    private bool _closing;

    public bool VerificationSaved { get; private set; }

    public HumanVerificationWindow(string url, string? userAgent)
    {
        _url = url; _userAgent = userAgent;
        Title = "Human verification"; Width = 620; Height = 330; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; AppTheme.Apply(this);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock
        {
            Text = "Complete the site's verification", FontSize = 25,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "A separate app-controlled Microsoft Edge window will open. Enter the requested code, sign in, or click the site's verification box there. Return here only after the real page appears.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        panel.Children.Add(new TextBlock { Text = url, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Complete the requested step before continuing.", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        _status.Text = "Opening the site in Edge…"; panel.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _continue.SetResourceReference(StyleProperty, "PrimaryButton");
        _continue.Click += Continue_Click; buttons.Children.Add(_continue);
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 7, 14, 7) };
        cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        Content = panel;
        Loaded += OpenBrowserAsync;
        Closed += async (_, _) => { if (_session != null) await _session.DisposeAsync(); };
    }

    private async void OpenBrowserAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            _session = await InteractiveHumanVerificationSession.OpenAsync(_url, _userAgent);
            if (_closing) { await _session.DisposeAsync(); _session = null; return; }
            _status.Text = "Complete the verification in Edge. When the requested page is visible, return here and select Continue crawling.";
            _continue.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _status.Text = "The verification browser could not be opened.";
            MessageBox.Show(this, ex.GetBaseException().Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        _continue.IsEnabled = false; _status.Text = "Checking the page…";
        try
        {
            var result = await _session.SaveAsync();
            _status.Text = result.Message;
            if (!result.Success) { _continue.IsEnabled = true; return; }
            VerificationSaved = true; DialogResult = true;
        }
        catch (Exception ex)
        {
            _status.Text = "The browser session could not be saved."; _continue.IsEnabled = true;
            MessageBox.Show(this, ex.GetBaseException().Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }
}
