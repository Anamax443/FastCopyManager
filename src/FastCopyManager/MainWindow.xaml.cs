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
        LoadSettings();
        Closing += (_, _) => SaveSettings();
        LeftPanel.GotKeyboardFocus += (_, _) => { _active = LeftPanel; UpdatePrompt(); };
        RightPanel.GotKeyboardFocus += (_, _) => { _active = RightPanel; UpdatePrompt(); };
        LeftPanel.PathChanged += (_, _) => UpdatePrompt();
        RightPanel.PathChanged += (_, _) => UpdatePrompt();
        BuildFKeyBar();
        BuildMenus();
        UpdatePrompt();
        Loaded += (_, _) => LeftPanel.ListControl.Focus();
    }

    // ---- horní menu (F9 nebo klik) ----

    private sealed record MenuEntry(string Label, string Hot, Action Act);
    private sealed record MenuDef(string Title, MenuEntry[] Items);

    private MenuDef[] _menus = Array.Empty<MenuDef>();
    private readonly List<TextBlock> _menuHeaders = new();
    private int _menuIndex, _menuItem;

    private void BuildMenus()
    {
        var view = new MenuEntry("View", "F3", () => View_Click(this, new RoutedEventArgs()));
        var edit = new MenuEntry("Edit", "F4", () => Edit_Click(this, new RoutedEventArgs()));
        var copy = new MenuEntry("Copy", "F5", () => Copy_Click(this, new RoutedEventArgs()));
        var move = new MenuEntry("Rename or move", "F6", () => Move_Click(this, new RoutedEventArgs()));
        var pack = new MenuEntry("Pack to ZIP", "Alt-F5", () => Pack_Click(this, new RoutedEventArgs()));
        var join = new MenuEntry("Join parts", "Alt-F6", () => Join_Click(this, new RoutedEventArgs()));
        var mkdir = new MenuEntry("Make directory", "F7", () => MkDir_Click(this, new RoutedEventArgs()));
        var del = new MenuEntry("Delete", "F8", () => Delete_Click(this, new RoutedEventArgs()));
        var quit = new MenuEntry("Quit", "F10", Close);
        var driveL = new MenuEntry("Drive...", "Alt-F1", () => ChangeDrive(LeftPanel));
        var driveR = new MenuEntry("Drive...", "Alt-F2", () => ChangeDrive(RightPanel));
        var refresh = new MenuEntry("Re-read", "Ctrl-R", () => { LeftPanel.Refresh(); RightPanel.Refresh(); });
        var swap = new MenuEntry("Swap panels", "Ctrl-U", SwapPanels);

        _menus = new[]
        {
            new MenuDef("Left", new[] { new MenuEntry("Brief", "Ctrl-F1", () => LeftPanel.SetBrief(true)), new MenuEntry("Full", "Ctrl-F1", () => LeftPanel.SetBrief(false)), driveL, refresh }),
            new MenuDef("Files", new[] { view, edit, copy, move, pack, join, mkdir, del, quit }),
            new MenuDef("Disk", new[] { driveL, driveR }),
            new MenuDef("Commands", new[] { swap, refresh, new MenuEntry("Language CS/EN", "Ctrl-L", ToggleLanguage) }),
            new MenuDef("Right", new[] { new MenuEntry("Brief", "Ctrl-F2", () => RightPanel.SetBrief(true)), new MenuEntry("Full", "Ctrl-F2", () => RightPanel.SetBrief(false)), driveR, refresh }),
        };

        for (var i = 0; i < _menus.Length; i++)
        {
            var idx = i;
            var title = _menus[i].Title;
            var tb = new TextBlock { Foreground = System.Windows.Media.Brushes.Black, Margin = new Thickness(0, 0, 40, 0) };
            tb.Inlines.Add(new System.Windows.Documents.Run(title[..1]) { Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0x55)) });
            tb.Inlines.Add(title[1..]);
            tb.MouseLeftButtonDown += (_, e) => { if (!_busy) OpenMenu(idx); e.Handled = true; };
            _menuHeaders.Add(tb);
            MenuBar.Children.Add(tb);
        }
    }

    private void OpenMenu(int index)
    {
        _menuIndex = (index + _menus.Length) % _menus.Length;
        _menuItem = 0;
        RenderMenu();
        MenuPopup.PlacementTarget = _menuHeaders[_menuIndex];
        MenuPopup.IsOpen = true;
    }

    private void RenderMenu()
    {
        var teal = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xAA, 0xAA));
        var stack = new StackPanel { MinWidth = 280 };
        var items = _menus[_menuIndex].Items;
        for (var i = 0; i < items.Length; i++)
        {
            var idx = i;
            var row = new DockPanel
            {
                Background = i == _menuItem ? System.Windows.Media.Brushes.Black : teal,
                Margin = new Thickness(0),
            };
            var fg = i == _menuItem ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;
            var hot = new TextBlock { Text = items[i].Hot + " ", Foreground = fg };
            DockPanel.SetDock(hot, Dock.Right);
            row.Children.Add(hot);
            row.Children.Add(new TextBlock { Text = " " + items[i].Label, Foreground = fg });
            row.MouseLeftButtonUp += (_, _) => ExecuteMenu(idx);
            stack.Children.Add(row);
        }
        MenuPopup.Child = new Border
        {
            Background = teal, BorderBrush = System.Windows.Media.Brushes.Black, BorderThickness = new Thickness(2), Child = stack,
        };
    }

    private void ExecuteMenu(int item)
    {
        var act = _menus[_menuIndex].Items[item].Act;
        MenuPopup.IsOpen = false;
        Dispatcher.BeginInvoke(act);
    }

    private void MenuPopup_Closed(object? sender, EventArgs e) => _active.ListControl.Focus();

    private bool HandleMenuKey(Key key)
    {
        var items = _menus[_menuIndex].Items;
        switch (key)
        {
            case Key.Up: _menuItem = (_menuItem - 1 + items.Length) % items.Length; RenderMenu(); break;
            case Key.Down: _menuItem = (_menuItem + 1) % items.Length; RenderMenu(); break;
            case Key.Left: OpenMenu(_menuIndex - 1); break;
            case Key.Right: OpenMenu(_menuIndex + 1); break;
            case Key.Enter: ExecuteMenu(_menuItem); break;
            case Key.Escape or Key.F9: MenuPopup.IsOpen = false; break;
            default: return true;   // při otevřeném menu ostatní klávesy nepouštíme dál
        }
        return true;
    }

    private void SwapPanels()
    {
        var left = LeftPanel.CurrentPath;
        var right = RightPanel.CurrentPath;
        LeftPanel.Navigate(right);
        RightPanel.Navigate(left);
    }

    // ---- nastavení: poslední složky, cesty na flashce se ukládají relativně k disku s aplikací ----

    private sealed record Settings(string? Left, string? Right, bool Verify, bool LeftBrief = false, bool RightBrief = false, string? Lang = null);

    private static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static readonly string AppRoot = Path.GetPathRoot(AppContext.BaseDirectory) ?? "";
    private const string AppDrive = "<app>:";

    private static string ToPortable(string path) =>
        AppRoot.Length > 0 && path.StartsWith(AppRoot, StringComparison.OrdinalIgnoreCase)
            ? AppDrive + path[AppRoot.Length..]
            : path;

    private static string FromPortable(string path) =>
        path.StartsWith(AppDrive, StringComparison.Ordinal) ? AppRoot + path[AppDrive.Length..] : path;

    private void LoadSettings()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Settings? st = null;
        try { st = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)); }
        catch { /* první spuštění nebo poškozený soubor */ }

        Loc.Init(st?.Lang);
        ApplyLanguage();

        string Pick(string? saved) =>
            !string.IsNullOrEmpty(saved) && Directory.Exists(FromPortable(saved)) ? FromPortable(saved) : home;

        LeftPanel.Navigate(Pick(st?.Left));
        RightPanel.Navigate(Pick(st?.Right));
        if (st is not null)
        {
            VerifyBox.IsChecked = st.Verify;
            if (st.LeftBrief) LeftPanel.SetBrief(true);
            if (st.RightBrief) RightPanel.SetBrief(true);
        }
    }

    private void SaveSettings()
    {
        try
        {
            var st = new Settings(ToPortable(LeftPanel.CurrentPath), ToPortable(RightPanel.CurrentPath), VerifyBox.IsChecked == true, LeftPanel.Brief, RightPanel.Brief, Loc.Code);
            File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(st));
        }
        catch { /* flashka jen pro čtení, nic se neukládá */ }
    }

    private void ApplyLanguage()
    {
        VerifyBox.Content = Loc.T("Ověřit hashem");
        StatusText.Text = Loc.T("Připraveno");
    }

    private void ToggleLanguage()
    {
        Loc.Toggle();
        ApplyLanguage();
        StatusText.Text = Loc.T("Jazyk: čeština");
    }

    private void UpdatePrompt() => PromptText.Text = _active.CurrentPath.TrimEnd('\\') + "\\>";

    /// <summary>Spodní lišta F-kláves jako v Norton Commanderu: číslo bíle na černé, popisek černě na azurové.</summary>
    private void BuildFKeyBar()
    {
        var keys = new (string Num, string Label, Action? Act)[]
        {
            ("1", "", null), ("2", "", null),
            ("3", "View", () => View_Click(this, new RoutedEventArgs())),
            ("4", "Edit", () => Edit_Click(this, new RoutedEventArgs())),
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
        var letter = Prompt(Loc.T("Disk"), Loc.T("Písmeno disku ({0}):", drives));
        if (string.IsNullOrWhiteSpace(letter)) return;
        var path = letter.Trim().TrimEnd(':', '\\')[..1].ToUpperInvariant() + ":\\";
        if (Directory.Exists(path)) panel.Navigate(path);
        else StatusText.Text = Loc.T("Disk {0} není dostupný.", path);
    }

    private FilePanel Other => _active == LeftPanel ? RightPanel : LeftPanel;

    private CancellationTokenSource? _cts;
    private CopyStats? _stats;
    private bool _busy;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (MenuPopup.IsOpen)
        {
            HandleMenuKey(e.Key);
            e.Handled = true;
            return;
        }
        if (!_busy && Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.F1 or Key.F2)
        {
            var panel = e.Key == Key.F1 ? LeftPanel : RightPanel;
            panel.SetBrief(!panel.Brief);
            e.Handled = true;
            return;
        }
        if (!_busy && Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.U or Key.R or Key.L)
        {
            if (e.Key == Key.L) ToggleLanguage();
            else if (e.Key == Key.U) SwapPanels();
            else { LeftPanel.Refresh(); RightPanel.Refresh(); }
            e.Handled = true;
            return;
        }
        if (_busy)
        {
            if (e.Key == Key.Escape) _cts?.Cancel();
            else if (e.Key is Key.P or Key.Pause) _stats?.TogglePause();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.System && e.SystemKey == Key.F6)
        {
            Join_Click(this, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.System && e.SystemKey == Key.F5)
        {
            Pack_Click(this, e);
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
            case Key.F3: View_Click(this, e); break;
            case Key.F4: Edit_Click(this, e); break;
            case Key.F5: Copy_Click(this, e); break;
            case Key.F6: Move_Click(this, e); break;
            case Key.F7: MkDir_Click(this, e); break;
            case Key.F8 or Key.Delete: Delete_Click(this, e); break;
            case Key.F9: OpenMenu(0); break;
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
        if (Other.InArchive)
        {
            StatusText.Text = Loc.T("Do archivu nelze kopírovat. ZIP vytvoříš přes Alt+F5.");
            return;
        }
        if (_active.InArchive && move)
        {
            StatusText.Text = Loc.T("Z archivu lze jen kopírovat (rozbalit).");
            return;
        }
        var dest = Other.CurrentPath;
        if (!_active.InArchive && dest.Equals(_active.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = Loc.T("Zdroj a cíl jsou stejná složka.");
            return;
        }

        var source = _active;
        var plan = source.InArchive ? source.BuildExtractPlan(items) : null;
        var conflicts = plan is not null ? FileOps.ExtractConflicts(plan, dest) : FileOps.Conflicts(items, dest);
        var overwrite = false;
        if (conflicts)
        {
            var r = MessageBox.Show(Loc.T("Některé položky v cíli už existují. Přepsat?") + Environment.NewLine + Loc.T("Ano = přepsat, Ne = přeskočit."),
                Loc.T("Konflikt"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            overwrite = r == MessageBoxResult.Yes;
        }

        var split = false;
        if (plan is null && FileOps.NeedsSplit(items, dest))
        {
            var msg = Loc.T("Některé soubory jsou větší než 4 GB a cíl je FAT32.") + Environment.NewLine +
                      Loc.T("Rozdělit je na části po 2 GB (.001, .002 ...)? Spojit je půjde přes Files > Join parts.");
            if (MessageBox.Show(msg, "FAT32", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            split = true;
        }

        var opt = new CopyOptions(overwrite, VerifyBox.IsChecked == true && plan is null, split);
        if (plan is not null)
        {
            var archive = source.ArchiveFile!;
            await RunOperation(Loc.T("Rozbaluji"), false, (st, ct) => Task.Run(() => FileOps.Extract(archive, plan, dest, opt, st, ct), ct), items.Count, source);
        }
        else
        {
            await RunOperation(move ? Loc.T("Přesouvám") : Loc.T("Kopíruji"), opt.Verify, async (st, ct) =>
            {
                await FileOps.CopyAsync(items, dest, opt, st, ct);
                if (move) await Task.Run(() => FileOps.Delete(items, recycle: false));
            }, items.Count, source);
        }
    }

    private async void Pack_Click(object sender, RoutedEventArgs e)
    {
        var items = _active.Selected;
        if (items.Count == 0) return;
        if (_active.InArchive || Other.InArchive)
        {
            StatusText.Text = Loc.T("Balit lze jen mezi běžnými složkami.");
            return;
        }
        var folder = Path.GetFileName(_active.CurrentPath.TrimEnd('\\'));
        var suggested = (items.Count == 1 ? Path.GetFileNameWithoutExtension(items[0].Name) : (folder.Length > 0 ? folder : "archiv")) + ".zip";
        var name = Prompt(Loc.T("Vytvořit ZIP"), Loc.T("Název archivu:"), suggested);
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name += ".zip";
        var zipPath = Path.Combine(Other.CurrentPath, name);
        if (File.Exists(zipPath) &&
            MessageBox.Show(Loc.T("{0} už existuje. Přepsat?", name), Loc.T("Potvrzení"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var source = _active;
        await RunOperation(Loc.T("Balím"), false, (st, ct) => Task.Run(() => FileOps.PackZip(items, zipPath, st, ct), ct), items.Count, source);
    }

    private void View_Click(object sender, RoutedEventArgs e) => OpenViewer(edit: false);
    private void Edit_Click(object sender, RoutedEventArgs e) => OpenViewer(edit: true);

    private void OpenViewer(bool edit)
    {
        if (_active.InArchive) { StatusText.Text = Loc.T("Soubory z archivu nejdřív rozbal (F5)."); return; }
        if (_active.ListControl.SelectedItem is not FileItem { IsDir: false, IsParent: false } item) return;
        try
        {
            new ViewerWindow(item.FullPath, edit, this).ShowDialog();
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("Chyba: ") + ex.Message;
        }
        _active.Refresh();
        _active.ListControl.Focus();
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        if (_active.InArchive || Other.InArchive)
        {
            StatusText.Text = Loc.T("Spojovat lze jen mezi běžnými složkami.");
            return;
        }
        var sel = _active.Selected;
        if (sel.Count != 1 || FileOps.SplitBaseName(sel[0].Name) is not { } baseName)
        {
            StatusText.Text = Loc.T("Vyber jednu část rozděleného souboru (.001, .002 ...).");
            return;
        }
        var destFile = Path.Combine(Other.CurrentPath, Path.GetFileName(baseName));
        if (File.Exists(destFile) &&
            MessageBox.Show(Loc.T("{0} už existuje. Přepsat?", Path.GetFileName(destFile)), Loc.T("Potvrzení"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var first = sel[0].FullPath;
        await RunOperation(Loc.T("Spojuji"), false, (st, ct) => Task.Run(() => FileOps.Join(first, destFile, st, ct), ct), 1, _active);
    }

    /// <summary>Společný průběh: stav, rychlost, zbývající čas, Esc, závěrečné shrnutí.</summary>
    private async Task RunOperation(string verb, bool verify, Func<CopyStats, CancellationToken, Task> work, int count, FilePanel focusBack)
    {
        var stats = new CopyStats();
        _stats = stats;
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
            StatusText.Text = (stats.Paused ? Loc.T("[PAUZA] ") : "") + $"{verb}: {Path.GetFileName(stats.Current)}  {100.0 * done / total:0}%  {speed / 1048576:0} MB/s  " + Loc.T("zbývá") + $" {left:mm\\:ss}  " + Loc.T("(P = pauza, Esc = zrušit)");
        };
        timer.Start();

        try
        {
            await work(stats, _cts.Token);
            var mb = stats.Done / (verify ? 2.0 : 1.0) / 1048576;
            StatusText.Text = Loc.T("Hotovo: {0} položek, {1:0.#} MB za {2:0.0} s ({3:0} MB/s)", count, mb, clock.Elapsed.TotalSeconds, mb / Math.Max(clock.Elapsed.TotalSeconds, 0.001)) + (verify ? Loc.T(", ověřeno") : "") + ".";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Loc.T("Zrušeno.");
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("Chyba: ") + ex.Message;
        }
        finally
        {
            timer.Stop();
            _busy = false;
            _cts = null;
            LeftPanel.Refresh();
            RightPanel.Refresh();
            focusBack.ListControl.Focus();
        }
    }

    private void MkDir_Click(object sender, RoutedEventArgs e)
    {
        if (_active.InArchive) { StatusText.Text = Loc.T("V archivu nelze měnit."); return; }
        var name = Prompt(Loc.T("Nová složka"), Loc.T("Název složky:"));
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(_active.CurrentPath, name.Trim()));
            _active.Refresh();
        }
        catch (Exception ex) { StatusText.Text = Loc.T("Chyba: ") + ex.Message; }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_active.InArchive) { StatusText.Text = Loc.T("V archivu nelze měnit."); return; }
        var items = _active.Selected;
        if (items.Count == 0) return;
        var recycle = FileOps.HasRecycleBin(_active.CurrentPath);
        var what = items.Count == 1 ? items[0].Name : Loc.T("{0} položek", items.Count);
        var where = recycle ? Loc.T("do koše") : Loc.T("NATRVALO (tento disk nemá koš)");
        if (MessageBox.Show(Loc.T("Smazat {0} {1}?", what, where), Loc.T("Potvrzení"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            FileOps.Delete(items, recycle);
            StatusText.Text = Loc.T("Smazáno.");
        }
        catch (Exception ex) { StatusText.Text = Loc.T("Chyba: ") + ex.Message; }
        _active.Refresh();
    }

    private string? Prompt(string title, string label, string initial = "")
    {
        var box = new TextBox { Margin = new Thickness(0, 6, 0, 12), Text = initial };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true };
        var cancel = new Button { Content = Loc.T("Zrušit"), Width = 80, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
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
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? box.Text : null;
    }
}
