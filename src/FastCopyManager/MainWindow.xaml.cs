using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FastCopyManager;

public partial class MainWindow : Window
{
    private FilePanel _active;

    public MainWindow()
    {
        InitializeComponent();
        _active = LeftPanel;
        var start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        LeftPanel.Navigate(start);
        RightPanel.Navigate(start);
        LeftPanel.GotKeyboardFocus += (_, _) => { _active = LeftPanel; UpdatePrompt(); };
        RightPanel.GotKeyboardFocus += (_, _) => { _active = RightPanel; UpdatePrompt(); };
        LeftPanel.PathChanged += (_, _) => UpdatePrompt();
        RightPanel.PathChanged += (_, _) => UpdatePrompt();
        BuildFKeyBar();
        UpdatePrompt();
        Loaded += (_, _) => LeftPanel.ListControl.Focus();
    }

    private void UpdatePrompt() => PromptText.Text = _active.CurrentPath.TrimEnd('\\') + "\\>";

    /// <summary>Spodní lišta F-kláves jako v Norton Commanderu: číslo bíle na černé, popisek černě na azurové.</summary>
    private void BuildFKeyBar()
    {
        var keys = new (string Num, string Label, Action? Act)[]
        {
            ("1", "", null), ("2", "", null), ("3", "", null), ("4", "", null),
            ("5", "Copy", () => Copy_Click(this, new RoutedEventArgs())),
            ("6", "RenMov", () => Move_Click(this, new RoutedEventArgs())),
            ("7", "Mkdir", () => MkDir_Click(this, new RoutedEventArgs())),
            ("8", "Delete", () => Delete_Click(this, new RoutedEventArgs())),
            ("9", "", null),
            ("10", "Quit", Close),
        };
        foreach (var (num, label, act) in keys)
        {
            var cell = new DockPanel();
            cell.Children.Add(new TextBlock { Text = num, Foreground = System.Windows.Media.Brushes.LightGray, Background = System.Windows.Media.Brushes.Black });
            cell.Children.Add(new TextBlock
            {
                Text = label, Foreground = System.Windows.Media.Brushes.Black,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xAA, 0xAA)),
            });
            if (act is not null)
                cell.MouseLeftButtonUp += (_, _) => { if (!_busy) act(); };
            FKeyBar.Children.Add(cell);
        }
    }

    private void ChangeDrive(FilePanel panel)
    {
        var drives = string.Join(", ", DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name.TrimEnd('\\')));
        var letter = Prompt("Disk", $"Písmeno disku ({drives}):");
        if (string.IsNullOrWhiteSpace(letter)) return;
        var path = letter.Trim().TrimEnd(':', '\\')[..1].ToUpperInvariant() + ":\\";
        if (Directory.Exists(path)) panel.Navigate(path);
        else StatusText.Text = $"Disk {path} není dostupný.";
    }

    private FilePanel Other => _active == LeftPanel ? RightPanel : LeftPanel;

    private CancellationTokenSource? _cts;
    private bool _busy;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_busy)
        {
            if (e.Key == Key.Escape) _cts?.Cancel();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.System && e.SystemKey is Key.F1 or Key.F2)
        {
            ChangeDrive(e.SystemKey == Key.F1 ? LeftPanel : RightPanel);
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Tab: Switch_Click(this, e); break;
            case Key.F5: Copy_Click(this, e); break;
            case Key.F6: Move_Click(this, e); break;
            case Key.F7: MkDir_Click(this, e); break;
            case Key.F8 or Key.Delete: Delete_Click(this, e); break;
            case Key.F10: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Switch_Click(object sender, RoutedEventArgs e) => Other.ListControl.Focus();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private async void Copy_Click(object sender, RoutedEventArgs e) => await Transfer(move: false);
    private async void Move_Click(object sender, RoutedEventArgs e) => await Transfer(move: true);

    private async Task Transfer(bool move)
    {
        var items = _active.Selected;
        if (items.Count == 0) return;
        var dest = Other.CurrentPath;
        if (dest.Equals(_active.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Zdroj a cíl jsou stejná složka.";
            return;
        }

        var overwrite = false;
        if (FileOps.Conflicts(items, dest))
        {
            var r = MessageBox.Show("Některé položky v cíli už existují. Přepsat?\nAno = přepsat, Ne = přeskočit.",
                "Konflikt", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            overwrite = r == MessageBoxResult.Yes;
        }

        var verb = move ? "Přesouvám" : "Kopíruji";
        var source = _active;
        var stats = new CopyStats();
        var opt = new CopyOptions(overwrite, VerifyBox.IsChecked == true);
        _cts = new CancellationTokenSource();
        _busy = true;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastDone = 0;
        double lastSec = 0, speed = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            var done = stats.Done;
            var sec = clock.Elapsed.TotalSeconds;
            var inst = (done - lastDone) / Math.Max(sec - lastSec, 0.001);
            speed = speed == 0 ? inst : speed * 0.7 + inst * 0.3;
            lastDone = done; lastSec = sec;
            var total = Math.Max(stats.Total, 1);
            var left = speed > 1 ? TimeSpan.FromSeconds((total - done) / speed) : TimeSpan.Zero;
            StatusText.Text = $"{verb}: {Path.GetFileName(stats.Current)}  {100.0 * done / total:0}%  {speed / 1048576:0} MB/s  zbývá {left:mm\\:ss}  (Esc = zrušit)";
        };
        timer.Start();

        try
        {
            await FileOps.CopyAsync(items, dest, opt, stats, _cts.Token);
            if (move) await Task.Run(() => FileOps.Delete(items, recycle: false));
            var mb = stats.Done / (opt.Verify ? 2.0 : 1.0) / 1048576;
            StatusText.Text = $"Hotovo: {items.Count} položek, {mb:0.#} MB za {clock.Elapsed.TotalSeconds:0.0} s ({mb / Math.Max(clock.Elapsed.TotalSeconds, 0.001):0} MB/s){(opt.Verify ? ", ověřeno" : "")}.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Zrušeno.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Chyba: " + ex.Message;
        }
        finally
        {
            timer.Stop();
            _busy = false;
            _cts = null;
            LeftPanel.Refresh();
            RightPanel.Refresh();
            source.ListControl.Focus();
        }
    }

    private void MkDir_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("Nová složka", "Název složky:");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(_active.CurrentPath, name.Trim()));
            _active.Refresh();
        }
        catch (Exception ex) { StatusText.Text = "Chyba: " + ex.Message; }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var items = _active.Selected;
        if (items.Count == 0) return;
        var recycle = FileOps.HasRecycleBin(_active.CurrentPath);
        var what = items.Count == 1 ? items[0].Name : $"{items.Count} položek";
        var where = recycle ? "do koše" : "NATRVALO (tento disk nemá koš)";
        if (MessageBox.Show($"Smazat {what} {where}?", "Potvrzení", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            FileOps.Delete(items, recycle);
            StatusText.Text = "Smazáno.";
        }
        catch (Exception ex) { StatusText.Text = "Chyba: " + ex.Message; }
        _active.Refresh();
    }

    private string? Prompt(string title, string label)
    {
        var box = new TextBox { Margin = new Thickness(0, 6, 0, 12) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true };
        var cancel = new Button { Content = "Zrušit", Width = 80, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var win = new Window
        {
            Title = title, Content = panel, Width = 360, SizeToContent = SizeToContent.Height,
            Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => box.Focus();
        return win.ShowDialog() == true ? box.Text : null;
    }
}
