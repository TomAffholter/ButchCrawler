using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ButchCrawler.Operator;

public sealed class ProblemSeedCleanupWindow : Window
{
    private readonly ProblemSeedRepository _repository;
    private readonly ComboBox _action = new()
    {
        Width = 260, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Left,
        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly RadioButton _attemptRule = new() { Content = "Consecutive failed attempts at least:", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumericSpinner _attempts = new(5, 1, 1_000_000) { Width = 105, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly RadioButton _dateRule = new() { Content = "First failed attempt on or before:", VerticalAlignment = VerticalAlignment.Center };
    private readonly DatePicker _before = new()
    {
        Width = 160, Height = 34, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left,
        HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center,
        SelectedDate = DateTime.Today.AddDays(-90)
    };
    private readonly DataGrid _grid = new()
    {
        IsReadOnly = true, AutoGenerateColumns = true, CanUserSortColumns = true,
        SelectionMode = DataGridSelectionMode.Extended, CanUserAddRows = false, CanUserDeleteRows = false
    };
    private readonly TextBlock _status = new() { Text = "Choose a rule, then preview the matching seeds.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _busyText = new() { FontSize = 17, FontWeight = FontWeights.SemiBold };
    private readonly Border _busyPanel = new()
    {
        Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Color.FromRgb(232, 242, 255)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(91, 142, 211)), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10)
    };
    private readonly Button _previewButton = new() { Content = "Preview matches", Padding = new Thickness(13, 7, 13, 7) };
    private readonly Button _applyButton = new() { Content = "Apply to previewed seeds", Padding = new Thickness(13, 7, 13, 7), IsEnabled = false };
    private DataTable? _preview;
    private ProblemSeedCleanupCriteria? _previewCriteria;
    private bool _busy;

    public ProblemSeedCleanupWindow(ProblemSeedRepository repository)
    {
        _repository = repository;
        Title = "Clean up problem seeds"; Width = 1060; Height = 700; MinWidth = 820; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; AppTheme.Apply(this);

        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "Clean up problem seeds", FontSize = 26, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock
        {
            Text = "Nothing happens automatically. Preview the exact seeds first, sort the list by clicking any column heading, and then confirm the action.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12)
        });

        var choices = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(285) });
        choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        choices.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        choices.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        choices.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var actionLabel = new TextBlock { Text = "Action:", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        choices.Children.Add(actionLabel);
        _action.Items.Add("Disable matching seeds"); _action.Items.Add("Delete matching seeds permanently");
        var centeredChoice = new Style(typeof(ComboBoxItem));
        centeredChoice.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        centeredChoice.Setters.Add(new Setter(VerticalContentAlignmentProperty, VerticalAlignment.Center));
        _action.ItemContainerStyle = centeredChoice; _action.SelectedIndex = 0;
        var leftCenteredDateText = new Style(typeof(DatePickerTextBox));
        leftCenteredDateText.Setters.Add(new Setter(TextBox.TextAlignmentProperty, TextAlignment.Left));
        leftCenteredDateText.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        leftCenteredDateText.Setters.Add(new Setter(VerticalContentAlignmentProperty, VerticalAlignment.Center));
        leftCenteredDateText.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 0, 4, 0)));
        _before.Resources[typeof(DatePickerTextBox)] = leftCenteredDateText;
        _before.Loaded += (_, _) => AlignDateText();
        Grid.SetColumn(_action, 1); choices.Children.Add(_action);
        _attemptRule.Margin = new Thickness(0, 10, 0, 0); Grid.SetRow(_attemptRule, 1); choices.Children.Add(_attemptRule);
        _attempts.Margin = new Thickness(0, 10, 0, 0); Grid.SetRow(_attempts, 1); Grid.SetColumn(_attempts, 1); choices.Children.Add(_attempts);
        _dateRule.Margin = new Thickness(0, 10, 0, 0); Grid.SetRow(_dateRule, 2); choices.Children.Add(_dateRule);
        _before.Margin = new Thickness(0, 10, 0, 0); Grid.SetRow(_before, 2); Grid.SetColumn(_before, 1); choices.Children.Add(_before);
        heading.Children.Add(choices);
        heading.Children.Add(new TextBlock
        {
            Text = "“First failed attempt” means the beginning of the current uninterrupted failure streak. A successful crawl resets that date and the failure count.",
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(75, 96, 119)), Margin = new Thickness(0, 0, 0, 10)
        });
        var busyStack = new StackPanel(); busyStack.Children.Add(_busyText);
        busyStack.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 8, 0, 0) });
        _busyPanel.Child = busyStack; heading.Children.Add(_busyPanel);

        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var close = new Button { Content = "Close", IsCancel = true, Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(close, Dock.Right); bottom.Children.Add(close);
        _applyButton.Margin = new Thickness(10, 0, 0, 0); _applyButton.SetResourceReference(StyleProperty, "PrimaryButton");
        DockPanel.SetDock(_applyButton, Dock.Right); bottom.Children.Add(_applyButton);
        DockPanel.SetDock(_previewButton, Dock.Right); bottom.Children.Add(_previewButton);
        bottom.Children.Add(_status);

        _grid.AutoGeneratingColumn += (_, e) =>
        {
            e.Column.CanUserSort = true;
            e.Column.MinWidth = e.PropertyName switch
            {
                "Url" => 310, "Reason" => 250, "First failed attempt" or "Last failure" => 145, _ => 75
            };
            if (e.PropertyName == "Id") e.Column.Visibility = Visibility.Collapsed;
        };
        root.Children.Add(_grid);

        _attemptRule.Checked += (_, _) => UpdateRuleControls();
        _dateRule.Checked += (_, _) => UpdateRuleControls();
        _attempts.ValueChanged += (_, _) => InvalidatePreview();
        _before.SelectedDateChanged += (_, _) => InvalidatePreview();
        _action.SelectionChanged += (_, _) => InvalidatePreview();
        _previewButton.Click += async (_, _) => await PreviewAsync();
        _applyButton.Click += async (_, _) => await ApplyAsync();
        close.Click += (_, _) => Close();
        UpdateRuleControls();
    }

    private void UpdateRuleControls()
    {
        _attempts.IsEnabled = _attemptRule.IsChecked == true;
        _before.IsEnabled = _dateRule.IsChecked == true;
        InvalidatePreview();
    }

    private void AlignDateText()
    {
        _before.ApplyTemplate();
        if (_before.Template.FindName("PART_TextBox", _before) is not DatePickerTextBox textBox) return;
        textBox.TextAlignment = TextAlignment.Left;
        textBox.HorizontalContentAlignment = HorizontalAlignment.Left;
        textBox.VerticalContentAlignment = VerticalAlignment.Center;
        textBox.Padding = new Thickness(6, 0, 4, 0);
        textBox.Margin = new Thickness(0);
    }

    private void InvalidatePreview()
    {
        if (_preview == null) return;
        _preview = null; _previewCriteria = null; _grid.ItemsSource = null; _applyButton.IsEnabled = false;
        _status.Text = "The rule changed. Preview the matching seeds again.";
    }

    private ProblemSeedCleanupCriteria? ReadCriteria(out string error)
    {
        error = string.Empty;
        var failures = _attempts.Value;
        if (_before.SelectedDate is not { } selectedDate)
        { error = "Choose a date for the date rule."; return null; }
        if (selectedDate.Date > DateTime.Today)
        { error = "The failure date cannot be in the future."; return null; }
        var localExclusiveEnd = DateTime.SpecifyKind(selectedDate.Date.AddDays(1), DateTimeKind.Local);
        return new ProblemSeedCleanupCriteria(
            _action.SelectedIndex == 1 ? ProblemSeedCleanupAction.Delete : ProblemSeedCleanupAction.Disable,
            _dateRule.IsChecked == true ? ProblemSeedCleanupRule.FirstFailureDate : ProblemSeedCleanupRule.FailureCount,
            failures, localExclusiveEnd.ToUniversalTime());
    }

    private async Task PreviewAsync()
    {
        if (_busy) return;
        var criteria = ReadCriteria(out var error);
        if (criteria == null) { _status.Text = error; return; }
        await WithBusyAsync("Finding matching problem seeds…", async () =>
        {
            _preview = await _repository.PreviewCleanupAsync(criteria);
            _previewCriteria = criteria; _grid.ItemsSource = _preview.DefaultView;
            _applyButton.IsEnabled = _preview.Rows.Count > 0;
            _status.Text = _preview.Rows.Count == 0
                ? "No problem seeds match this rule. Nothing can be changed."
                : $"{_preview.Rows.Count:N0} matching seed(s). Review and sort the list, then choose Apply to continue.";
        });
    }

    private async Task ApplyAsync()
    {
        if (_busy || _preview == null || _previewCriteria == null || _preview.Rows.Count == 0) return;
        var ids = _preview.Rows.Cast<DataRow>().Select(row => Convert.ToInt32(row["Id"])).ToArray();
        var deleting = _previewCriteria.Action == ProblemSeedCleanupAction.Delete;
        var action = deleting ? "permanently delete" : "disable";
        var consequence = deleting
            ? "The seed records and their queued crawl work will be deleted. Previously collected pages and research will be retained. This cannot be undone."
            : "The seeds will remain in the database but will no longer be crawled. You can enable them again later.";
        if (MessageBox.Show(this,
            $"Are you sure you want to {action} {ids.Length:N0} previewed seed(s)?\n\n{consequence}",
            deleting ? "Permanently delete previewed seeds" : "Disable previewed seeds",
            MessageBoxButton.YesNo, deleting ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;

        await WithBusyAsync(deleting ? "Deleting the reviewed seeds…" : "Disabling the reviewed seeds…", async () =>
        {
            var affected = await _repository.ApplyCleanupAsync(_previewCriteria, ids);
            MessageBox.Show(this,
                $"{affected:N0} seed(s) were {(deleting ? "deleted" : "disabled")}." +
                (affected == ids.Length ? string.Empty : " Seeds that recovered or changed after the preview were safely skipped."),
                "Cleanup complete", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        });
    }

    private async Task WithBusyAsync(string message, Func<Task> operation)
    {
        if (_busy) return;
        _busy = true; _busyText.Text = message; _busyPanel.Visibility = Visibility.Visible;
        _previewButton.IsEnabled = _applyButton.IsEnabled = _grid.IsEnabled = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        await Dispatcher.Yield(DispatcherPriority.Background);
        try { await operation(); }
        catch (Exception ex)
        {
            _status.Text = "The cleanup operation could not be completed.";
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false; _busyPanel.Visibility = Visibility.Collapsed; _previewButton.IsEnabled = _grid.IsEnabled = true;
            _applyButton.IsEnabled = _preview != null && _preview.Rows.Count > 0; Cursor = null;
        }
    }
}

internal sealed class NumericSpinner : Grid
{
    private readonly TextBox _text = new() { TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly int _minimum;
    private readonly int _maximum;
    private int _value;
    private bool _updating;

    public event EventHandler? ValueChanged;
    public int Value
    {
        get => _value;
        set => SetValue(Math.Clamp(value, _minimum, _maximum));
    }

    public NumericSpinner(int value, int minimum, int maximum)
    {
        _minimum = minimum; _maximum = maximum; _value = Math.Clamp(value, minimum, maximum);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
        Children.Add(_text); Grid.SetColumn(_text, 0);

        var buttons = new Grid(); buttons.RowDefinitions.Add(new RowDefinition()); buttons.RowDefinitions.Add(new RowDefinition());
        Grid.SetColumn(buttons, 1); Children.Add(buttons);
        var up = SpinnerButton("▲", "Increase failed-attempt count");
        var down = SpinnerButton("▼", "Decrease failed-attempt count");
        Grid.SetRow(up, 0); Grid.SetRow(down, 1); buttons.Children.Add(up); buttons.Children.Add(down);
        up.Click += (_, _) => Value++;
        down.Click += (_, _) => Value--;

        _text.PreviewTextInput += (_, e) => e.Handled = e.Text.Any(character => !char.IsDigit(character));
        DataObject.AddPastingHandler(_text, (_, e) =>
        {
            if (!e.DataObject.GetDataPresent(DataFormats.Text) ||
                e.DataObject.GetData(DataFormats.Text) is not string pasted || pasted.Any(character => !char.IsDigit(character)))
                e.CancelCommand();
        });
        _text.TextChanged += (_, _) =>
        {
            if (_updating || !int.TryParse(_text.Text, out var parsed)) return;
            var next = Math.Clamp(parsed, _minimum, _maximum);
            if (next == _value) return;
            _value = next; ValueChanged?.Invoke(this, EventArgs.Empty);
        };
        _text.LostKeyboardFocus += (_, _) => SetValue(_value);
        _text.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Up) { Value++; e.Handled = true; }
            else if (e.Key == Key.Down) { Value--; e.Handled = true; }
        };
        PreviewMouseWheel += (_, e) => { Value += e.Delta > 0 ? 1 : -1; e.Handled = true; };
        SetValue(_value, notify: false);
        ToolTip = $"Choose a whole number from {_minimum:N0} to {_maximum:N0}. Use the arrows or Up/Down keys.";
    }

    private static RepeatButton SpinnerButton(string text, string toolTip) => new()
    {
        Content = text, FontSize = 8, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center, Delay = 400, Interval = 80, ToolTip = toolTip
    };

    private void SetValue(int value, bool notify = true)
    {
        var next = Math.Clamp(value, _minimum, _maximum);
        var changed = next != _value; _value = next;
        _updating = true; _text.Text = next.ToString(); _updating = false;
        if (changed && notify) ValueChanged?.Invoke(this, EventArgs.Empty);
    }
}
