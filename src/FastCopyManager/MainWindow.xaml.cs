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
        LeftPanel.GotKeyboardFocus += (_, _) => { _active = LeftPanel; UpdatePrompt(); SetZone(Zone.Panels); };
        RightPanel.GotKeyboardFocus += (_, _) => { _active = RightPanel; UpdatePrompt(); SetZone(Zone.Panels); };
        LeftPanel.PathChanged += (_, _) => UpdatePrompt();
        RightPanel.PathChanged += (_, _) => UpdatePrompt();
        LeftPanel.NavigateOut += d => OnPanelNavigateOut(LeftPanel, d);
        RightPanel.NavigateOut += d => OnPanelNavigateOut(RightPanel, d);
        CommandBox.GotKeyboardFocus += (_, _) => SetZone(Zone.CommandLine);
        PreviewMouseDown += Window_PreviewMouseDown;
        PreviewTextInput += Window_PreviewTextInput;
        Deactivated += (_, _) => { if (MenuPopup.IsOpen) CloseMenu(false); };
        Activated += (_, _) =>
        {
            if (!_refreshOnActivate || _busy) return;
            _refreshOnActivate = false;
            LeftPanel.Refresh();
            RightPanel.Refresh();
        };
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
        var unpack = new MenuEntry("Unpack archive", "F1", () => Unpack_Click(this, new RoutedEventArgs()));
        var pack = new MenuEntry("Pack to ZIP", "F2", () => Pack_Click(this, new RoutedEventArgs()));
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
            new MenuDef("Left", new[] { new MenuEntry("Brief", "Ctrl-F1", () => LeftPanel.SetBrief(true)), new MenuEntry("Full", "Ctrl-F1", () => LeftPanel.SetBrief(false)) }.Concat(SortEntries(LeftPanel)).Concat(new[] { driveL, refresh }).ToArray()),
            new MenuDef("Files", new[] { view, edit, copy, move, unpack, pack, join, mkdir, del, quit }),
            new MenuDef("Disk", new[] { driveL, driveR }),
            new MenuDef("Commands", new[] { swap, refresh, new MenuEntry("Language CS/EN", "Ctrl-L", ToggleLanguage) }),
            new MenuDef("Right", new[] { new MenuEntry("Brief", "Ctrl-F2", () => RightPanel.SetBrief(true)), new MenuEntry("Full", "Ctrl-F2", () => RightPanel.SetBrief(false)) }.Concat(SortEntries(RightPanel)).Concat(new[] { driveR, refresh }).ToArray()),
        };

        for (var i = 0; i < _menus.Length; i++)
        {
            var idx = i;
            var title = _menus[i].Title;
            var tb = new TextBlock { Foreground = System.Windows.Media.Brushes.Black, Margin = new Thickness(0, 0, 20, 0), Padding = new Thickness(6, 0, 6, 0) };
            tb.Inlines.Add(new System.Windows.Documents.Run(title[..1]) { Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0x55)) });
            tb.Inlines.Add(title[1..]);
            tb.MouseLeftButtonDown += (_, e) =>
            {
                if (!_busy)
                {
                    if (MenuPopup.IsOpen && _menuIndex == idx) CloseMenu(false);
                    else OpenMenu(idx);
                }
                e.Handled = true;
            };
            _menuHeaders.Add(tb);
            MenuBar.Children.Add(tb);
        }
    }

    private static MenuEntry[] SortEntries(FilePanel panel) => new[]
    {
        new MenuEntry("Name", "Ctrl-F3", () => panel.SetSort(SortMode.Name)),
        new MenuEntry("Extension", "Ctrl-F4", () => panel.SetSort(SortMode.Extension)),
        new MenuEntry("Time", "Ctrl-F5", () => panel.SetSort(SortMode.Time)),
        new MenuEntry("Size", "Ctrl-F6", () => panel.SetSort(SortMode.Size)),
        new MenuEntry("Unsorted", "Ctrl-F7", () => panel.SetSort(SortMode.Unsorted)),
    };

    private void OpenMenu(int index)
    {
        _menuIndex = (index + _menus.Length) % _menus.Length;
        _menuItem = 0;
        _menuBarIndex = _menuIndex;
        HighlightMenuBar(_menuIndex);
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

    private void CloseMenu(bool keepMenuBar)
    {
        _keepMenuBar = keepMenuBar;
        MenuPopup.IsOpen = false;
    }

    private void MenuPopup_Closed(object? sender, EventArgs e)
    {
        var keep = _keepMenuBar;
        _keepMenuBar = false;
        if (keep)
        {
            SetZone(Zone.MenuBar);
            Keyboard.Focus(this);
        }
        else
        {
            SetZone(Zone.Panels);
            _active.ListControl.Focus();
        }
    }

    private bool HandleMenuKey(Key key)
    {
        var items = _menus[_menuIndex].Items;
        switch (key)
        {
            case Key.Up: if (_menuItem == 0) CloseMenu(true); else { _menuItem--; RenderMenu(); } break;
            case Key.Down: _menuItem = (_menuItem + 1) % items.Length; RenderMenu(); break;
            case Key.Left: OpenMenu(_menuIndex - 1); break;
            case Key.Right: OpenMenu(_menuIndex + 1); break;
            case Key.Enter: ExecuteMenu(_menuItem); break;
            case Key.Escape or Key.F9: CloseMenu(false); break;
            default: return true;   // při otevřeném menu ostatní klávesy nepouštíme dál
        }
        return true;
    }

    // ---- zóny: panely, horní menu, příkazový řádek, dolní lišta (šipky mezi nimi) ----

    private enum Zone { Panels, MenuBar, CommandLine, FKeys }

    private static readonly System.Windows.Media.Brush FkTeal =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xAA, 0xAA));

    private Zone _zone = Zone.Panels;
    private int _menuBarIndex;
    private int _fkIndex = 4;   // F5 Copy
    private readonly List<(TextBlock Label, Action? Act)> _fkeys = new();
    private bool _refreshOnActivate;
    private bool _keepMenuBar;

    private void SetZone(Zone zone)
    {
        _zone = zone;
        HighlightMenuBar(zone == Zone.MenuBar ? _menuBarIndex : (MenuPopup.IsOpen ? _menuIndex : -1));
        HighlightFKeys(zone == Zone.FKeys ? _fkIndex : -1);
    }

    private void HighlightMenuBar(int index)
    {
        for (var i = 0; i < _menuHeaders.Count; i++)
        {
            var on = i == index;
            _menuHeaders[i].Background = on ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.Transparent;
            _menuHeaders[i].Foreground = on ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;
        }
    }

    private void HighlightFKeys(int index)
    {
        for (var i = 0; i < _fkeys.Count; i++)
            _fkeys[i].Label.Background = i == index ? System.Windows.Media.Brushes.White : FkTeal;
    }

    private void FocusPanels()
    {
        SetZone(Zone.Panels);
        _active.ListControl.Focus();
    }

    private void OnPanelNavigateOut(FilePanel panel, NavDirection direction)
    {
        switch (direction)
        {
            case NavDirection.Left when panel == RightPanel: LeftPanel.ListControl.Focus(); break;
            case NavDirection.Right when panel == LeftPanel: RightPanel.ListControl.Focus(); break;
            case NavDirection.Up:
                _menuBarIndex = panel == LeftPanel ? 0 : _menus.Length - 1;
                SetZone(Zone.MenuBar);
                Keyboard.Focus(this);
                break;
            case NavDirection.Down: CommandBox.Focus(); break;
        }
    }

    /// <summary>Klávesy mimo panely (šipky, Enter, Esc). Vrací true, když je zpracovala.</summary>
    private bool HandleZoneKey(KeyEventArgs e)
    {
        switch (_zone)
        {
            case Zone.MenuBar:
                switch (e.Key)
                {
                    case Key.Left: MoveMenuBar(-1); return true;
                    case Key.Right: MoveMenuBar(1); return true;
                    case Key.Down or Key.Enter: OpenMenu(_menuBarIndex); return true;
                    case Key.Up or Key.Escape: FocusPanels(); return true;
                }
                return false;

            case Zone.CommandLine:
                switch (e.Key)
                {
                    case Key.Up: FocusPanels(); return true;
                    case Key.Escape: CommandBox.Clear(); FocusPanels(); return true;
                    case Key.Down: SetZone(Zone.FKeys); Keyboard.Focus(this); return true;
                    case Key.Enter: RunCommand(); return true;
                }
                return false;

            case Zone.FKeys:
                switch (e.Key)
                {
                    case Key.Left: MoveFKey(-1); return true;
                    case Key.Right: MoveFKey(1); return true;
                    case Key.Up: Keyboard.Focus(CommandBox); return true;
                    case Key.Escape: FocusPanels(); return true;
                    case Key.Enter:
                        var act = _fkeys[_fkIndex].Act;
                        FocusPanels();
                        if (act is not null) Dispatcher.BeginInvoke(act);
                        return true;
                }
                return false;
        }
        return false;
    }

    private void MoveMenuBar(int delta)
    {
        _menuBarIndex = (_menuBarIndex + delta + _menus.Length) % _menus.Length;
        HighlightMenuBar(_menuBarIndex);
    }

    private void MoveFKey(int delta)
    {
        var enabled = Enumerable.Range(0, _fkeys.Count).Where(i => _fkeys[i].Act is not null).ToList();
        var pos = enabled.IndexOf(_fkIndex);
        _fkIndex = enabled[(pos + delta + enabled.Count) % enabled.Count];
        HighlightFKeys(_fkIndex);
    }

    /// <summary>Příkazový řádek: spustí příkaz v cmd.exe v aktuální složce a nechá okno otevřené (pause).</summary>
    private void RunCommand()
    {
        var text = CommandBox.Text.Trim();
        if (text.Length == 0) { FocusPanels(); return; }
        try
        {
            var quote = ((char)34).ToString();
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = "/c " + quote + text + " & pause" + quote,
                WorkingDirectory = Directory.Exists(_active.CurrentPath) ? _active.CurrentPath : Environment.CurrentDirectory,
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
            CommandBox.Clear();
            _refreshOnActivate = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("Chyba: ") + ex.Message;
        }
        FocusPanels();
    }

    /// <summary>Psaní v panelu se přesměruje do příkazového řádku, jako v Norton Commanderu.</summary>
    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_busy || MenuPopup.IsOpen || _zone != Zone.Panels || e.Text.Length != 1) return;
        var c = e.Text[0];
        if (!(char.IsLetterOrDigit(c) || c == (char)92 || "._-:~/$%&()+=,;@!#".IndexOf(c) >= 0)) return;
        CommandBox.Focus();
        CommandBox.AppendText(e.Text);
        CommandBox.CaretIndex = CommandBox.Text.Length;
        e.Handled = true;
    }

    /// <summary>Klik mimo rozbalené menu ho zavře (popup se zavírá ručně, aby nemizel hned po otevření).</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!MenuPopup.IsOpen) return;
        if (e.OriginalSource is DependencyObject src &&
            (IsInside(src, MenuPopup.Child) || _menuHeaders.Any(h => IsInside(src, h))))
            return;
        CloseMenu(false);
    }

    private static bool IsInside(DependencyObject node, DependencyObject? root)
    {
        for (DependencyObject? d = node; d is not null;
             d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == root) return true;
        return false;
    }

    private void SwapPanels()
    {
        var left = LeftPanel.CurrentPath;
        var right = RightPanel.CurrentPath;
        LeftPanel.Navigate(right);
        RightPanel.Navigate(left);
    }

    // ---- nastavení: poslední složky, cesty na flashce se ukládají relativně k disku s aplikací ----

    private sealed record Settings(string? Left, string? Right, bool Verify, bool LeftBrief = false, bool RightBrief = false, string? Lang = null, string? LeftSort = null, string? RightSort = null);

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

        LeftPanel.SortSpec = st?.LeftSort ?? "";
        RightPanel.SortSpec = st?.RightSort ?? "";
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
            var st = new Settings(ToPortable(LeftPanel.CurrentPath), ToPortable(RightPanel.CurrentPath), VerifyBox.IsChecked == true, LeftPanel.Brief, RightPanel.Brief, Loc.Code, LeftPanel.SortSpec, RightPanel.SortSpec);
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
            ("1", "Unpack", () => Unpack_Click(this, new RoutedEventArgs())),
            ("2", "Pack", () => Pack_Click(this, new RoutedEventArgs())),
            ("3", "View", () => View_Click(this, new RoutedEventArgs())),
            ("4", "Edit", () => Edit_Click(this, new RoutedEventArgs())),
            ("5", "Copy", () => Copy_Click(this, new RoutedEventArgs())),
            ("6", "RenMov", () => Move_Click(this, new RoutedEventArgs())),
            ("7", "Mkdir", () => MkDir_Click(this, new RoutedEventArgs())),
            ("8", "Delete", () => Delete_Click(this, new RoutedEventArgs())),
            ("9", "PullDn", () => OpenMenu(0)),
            ("10", "Quit", Close),
        };
        foreach (var (num, label, act) in keys)
        {
            var cell = new DockPanel();
            cell.Children.Add(new TextBlock { Text = num, Foreground = System.Windows.Media.Brushes.LightGray, Background = System.Windows.Media.Brushes.Black });
            var lbl = new TextBlock { Text = label, Foreground = System.Windows.Media.Brushes.Black, Background = FkTeal };
            cell.Children.Add(lbl);
            _fkeys.Add((lbl, act));
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
        if (!_busy && Keyboard.Modifiers == ModifierKeys.None && HandleZoneKey(e))
        {
            e.Handled = true;
            return;
        }
        if (!_busy && Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.F3 or Key.F4 or Key.F5 or Key.F6 or Key.F7)
        {
            _active.SetSort(e.Key switch
            {
                Key.F3 => SortMode.Name,
                Key.F4 => SortMode.Extension,
                Key.F5 => SortMode.Time,
                Key.F6 => SortMode.Size,
                _ => SortMode.Unsorted,
            });
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
        if (e.Key == Key.System && e.SystemKey == Key.F9)
        {
            Unpack_Click(this, e);
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
            case Key.F1: Unpack_Click(this, e); break;
            case Key.F2: Pack_Click(this, e); break;
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

    /// <summary>Rozbalí vybrané archivy do druhého panelu, každý do složky podle názvu archivu.</summary>
    private async void Unpack_Click(object sender, RoutedEventArgs e)
    {
        if (_active.InArchive)
        {
            await Transfer(move: false);   // uvnitř archivu = rozbalit vybrané položky
            return;
        }
        if (Other.InArchive)
        {
            StatusText.Text = Loc.T("Rozbalovat lze jen do běžné složky.");
            return;
        }
        var archives = _active.Selected.Where(i => !i.IsDir && FileOps.IsArchiveName(i.Name)).ToList();
        if (archives.Count == 0)
        {
            StatusText.Text = Loc.T("Vyber archiv (.zip, .rar, .7z, .tar).");
            return;
        }

        var dest = Other.CurrentPath;
        var plans = new List<(string Archive, string Target, List<FileOps.ExtractEntry> Plan)>();
        try
        {
            foreach (var a in archives)
                plans.Add((a.FullPath, Path.Combine(dest, Path.GetFileNameWithoutExtension(a.Name)), FileOps.PlanAll(a.FullPath)));
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("Chyba: ") + ex.Message;
            return;
        }

        var overwrite = false;
        if (plans.Any(p => FileOps.ExtractConflicts(p.Plan, p.Target)))
        {
            var r = MessageBox.Show(Loc.T("Některé položky v cíli už existují. Přepsat?") + Environment.NewLine + Loc.T("Ano = přepsat, Ne = přeskočit."),
                Loc.T("Konflikt"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            overwrite = r == MessageBoxResult.Yes;
        }

        var opt = new CopyOptions(overwrite, false);
        var source = _active;
        await RunOperation(Loc.T("Rozbaluji"), false, (st, ct) => Task.Run(() =>
        {
            foreach (var p in plans)
            {
                Directory.CreateDirectory(p.Target);
                FileOps.Extract(p.Archive, p.Plan, p.Target, opt, st, ct);
            }
        }, ct), archives.Count, source);
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
