using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SharpCompress.Archives;

namespace FastCopyManager;

public partial class FilePanel : UserControl
{
    private static string[] ArchiveExtensions => FileOps.ArchiveExtensions;

    private sealed record ArchiveRecord(string Key, bool IsDir, long Size, DateTime Modified);

    private List<ArchiveRecord> _archiveEntries = new();
    private string _archiveDir = "";   // "" = kořen archivu, jinak "slozka/podslozka/"

    /// <summary>Při procházení archivu je to složka, ve které archiv leží.</summary>
    public string CurrentPath { get; private set; } = "";

    public string? ArchiveFile { get; private set; }
    public bool InArchive => ArchiveFile is not null;

    public event EventHandler? PathChanged;

    /// <summary>Zpráva pro stavový řádek (například že zadaná cesta neexistuje).</summary>
    public event Action<string>? StatusMessage;

    // ---- cesta nad panelem: klik (nebo Ctrl+G) ji změní na editovatelné pole, jde zadat i síťovou cestu \\server\sdileni ----

    public bool IsEditingPath => PathEdit.Visibility == Visibility.Visible;

    public void BeginPathEdit()
    {
        if (QuickView) return;
        PathEdit.Text = CurrentPath;
        PathText.Visibility = Visibility.Collapsed;
        PathEdit.Visibility = Visibility.Visible;
        PathEdit.Focus();
        PathEdit.SelectAll();
    }

    private void EndPathEdit(bool refocusList)
    {
        PathEdit.Visibility = Visibility.Collapsed;
        PathText.Visibility = Visibility.Visible;
        if (refocusList) FocusList();
    }

    private void PathText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        BeginPathEdit();
        e.Handled = true;
    }

    private async void PathEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndPathEdit(true);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var text = PathEdit.Text;
            EndPathEdit(true);
            var error = await GoToAsync(text);
            if (error is not null) StatusMessage?.Invoke(error);
            FocusList();
        }
    }

    private void PathEdit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (IsEditingPath) EndPathEdit(false);   // klik jinam = zrušit úpravu
    }

    /// <summary>
    /// Přejde na zadanou cestu: absolutní, relativní k aktuální složce, "..", "\", disk (D:) i síťová (\\server\sdileni).
    /// Vrací chybovou zprávu, nebo null při úspěchu. Kontrola existence má časový limit, aby nedostupný server nezablokoval okno.
    /// </summary>
    public async Task<string?> GoToAsync(string argument)
    {
        argument = Environment.ExpandEnvironmentVariables((argument ?? "").Trim().Trim((char)34));
        if (argument.Length == 0) return null;
        if (argument == ".." && InArchive)
        {
            GoUp();
            return null;
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(argument, @"^[A-Za-z]:$")) argument += (char)92;

        string target;
        try
        {
            target = Path.GetFullPath(Path.Combine(CurrentPath, argument));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Loc.T("Cesta nenalezena: {0}", argument);
        }

        var exists = Task.Run(() => Directory.Exists(target));
        if (await Task.WhenAny(exists, Task.Delay(8000)) != exists)
            return Loc.T("Cesta nedostupná (vypršel čas): {0}", target);
        if (!await exists) return Loc.T("Cesta nenalezena: {0}", target);

        Navigate(target);
        return null;
    }

    /// <summary>Změna vybrané položky (druhý panel podle ní aktualizuje náhled).</summary>
    public event EventHandler? SelectionChangedOut;

    // ---- Quick view (Ctrl+Q): panel místo seznamu ukazuje náhled souboru z druhého panelu ----

    private const int PreviewBytes = 128 * 1024;

    public bool QuickView { get; private set; }

    public void SetQuickView(bool on)
    {
        if (QuickView == on) return;
        QuickView = on;
        List.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        PreviewBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        InfoText.Text = "";
        if (!on)
        {
            PreviewBox.Clear();
            Refresh();   // vrátí seznam a titulek s cestou
        }
    }

    public void ShowPreview(FileItem? item, bool fromArchive)
    {
        PathText.Text = " " + Loc.T("Náhled") + (item is { IsParent: false } ? ": " + item.Name : "") + " ";
        PreviewBox.ScrollToHome();
        if (item is null || item.IsParent) { PreviewBox.Text = ""; return; }

        var date = item.Modified == default ? "" : item.Modified.ToString("dd.MM.yyyy HH:mm");
        if (item.IsDir) { PreviewBox.Text = $"[{item.Name}]  <DIR>  {date}"; return; }
        if (fromArchive) { PreviewBox.Text = Loc.T("Náhled v archivu není dostupný. Rozbal soubor (F5)."); return; }

        try
        {
            byte[] bytes;
            bool truncated;
            using (var fs = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var length = (int)Math.Min(fs.Length, PreviewBytes);
                bytes = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = fs.Read(bytes, read, length - read);
                    if (n <= 0) break;
                    read += n;
                }
                truncated = fs.Length > PreviewBytes;
            }

            // Binární soubor se pozná podle nulových bajtů na začátku.
            var probe = Math.Min(bytes.Length, 4096);
            for (var i = 0; i < probe; i++)
            {
                if (bytes[i] != 0) continue;
                PreviewBox.Text = $"{item.Name}  {item.SizeText}  {date}" + Environment.NewLine + Environment.NewLine +
                                  Loc.T("Binární soubor (náhled: F3, v něm F4 = hex).");
                return;
            }

            var (encoding, hasBom) = ViewerWindow.DetectEncoding(bytes);
            var offset = hasBom ? encoding.GetPreamble().Length : 0;
            var text = encoding.GetString(bytes, offset, bytes.Length - offset);
            PreviewBox.Text = truncated ? text + Environment.NewLine + Loc.T("... (zobrazen jen začátek souboru)") : text;
        }
        catch (Exception ex)
        {
            PreviewBox.Text = ex.Message;
        }
    }

    /// <summary>Šipka na okraji seznamu: předá řízení oknu (druhý panel, menu, příkazový řádek).</summary>
    public event Action<NavDirection>? NavigateOut;

    public bool Brief { get; private set; }

    /// <summary>Přepíná mezi plným výpisem (název, velikost, datum) a výpisem ve více sloupcích.</summary>
    public void SetBrief(bool brief)
    {
        var refocus = IsKeyboardFocusWithin;
        Brief = brief;
        if (brief)
        {
            List.View = null;
            List.ItemContainerStyle = (Style)FindResource("NcItemBrief");
            List.ItemTemplate = (DataTemplate)FindResource("BriefTemplate");
            List.ItemsPanel = (ItemsPanelTemplate)FindResource("BriefPanel");
            ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
        }
        else
        {
            List.ClearValue(ItemsControl.ItemsPanelProperty);
            List.ItemTemplate = null;
            List.ItemContainerStyle = (Style)FindResource("NcItem");
            List.View = FullView;
            ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(List, ScrollBarVisibility.Auto);
        }
        ScrollToSelected(refocus);
    }

    /// <summary>Rolování až po dokončení rozložení, jinak se ve Brief režimu odroluje na špatné místo.</summary>
    private void ScrollToSelected(bool refocus = false) =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (List.SelectedItem is null) return;
            List.ScrollIntoView(List.SelectedItem);
            if (refocus && List.Visibility == Visibility.Visible) FocusList();
        });

    /// <summary>Fokus na vybraný řádek (ne na samotný seznam), aby první šipka hned pohnula kurzorem.</summary>
    public void FocusList()
    {
        if (List.Visibility != Visibility.Visible) return;
        if (List.SelectedItem is null) { List.Focus(); return; }

        var container = List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) as UIElement;
        if (container is null)
        {
            // řádek mimo viditelnou část ještě nemá vykreslený kontejner: nejdřív ho dorolovat
            List.ScrollIntoView(List.SelectedItem);
            List.UpdateLayout();
            container = List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) as UIElement;
        }
        if (container is not null) container.Focus();
        else List.Focus();
    }

    private int RowsPerColumn()
    {
        var probe = List.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
        var rowHeight = probe is { ActualHeight: > 0 } ? probe.ActualHeight : 20;
        return Math.Max(1, (int)(List.ActualHeight / rowHeight) - 1);
    }

    private void SelectIndex(int index)
    {
        List.SelectedIndex = index;
        List.ScrollIntoView(List.SelectedItem);
        (List.ItemContainerGenerator.ContainerFromIndex(index) as UIElement)?.Focus();
    }

    /// <summary>Šipky: uvnitř seznamu se pohybují, na okraji předají řízení dál. Vrací true, když klávesu zpracovala.</summary>
    private bool HandleArrow(Key key, bool repeat)
    {
        // Přechod do jiné části okna jen při novém stisku; při podržení klávesy se panel nepřeskakuje.
        void Out(NavDirection direction)
        {
            if (!repeat) NavigateOut?.Invoke(direction);
        }

        var count = List.Items.Count;
        var index = List.SelectedIndex;
        switch (key)
        {
            case Key.Up when index <= 0:
                Out(NavDirection.Up);
                return true;
            case Key.Down when index >= count - 1:
                Out(NavDirection.Down);
                return true;
            case Key.Left or Key.Right when !Brief || count == 0:
                Out(key == Key.Left ? NavDirection.Left : NavDirection.Right);
                return true;
            case Key.Left or Key.Right:
                var rows = RowsPerColumn();
                var column = index / rows;
                var lastColumn = (count - 1) / rows;
                if (key == Key.Left)
                {
                    if (column == 0) Out(NavDirection.Left);
                    else SelectIndex(index - rows);
                }
                else
                {
                    if (column >= lastColumn) Out(NavDirection.Right);
                    else SelectIndex(Math.Min(index + rows, count - 1));
                }
                return true;
        }
        return false;
    }

    public FilePanel()
    {
        InitializeComponent();
        List.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(Header_Click));
        UpdateHeaders();
    }

    // ---- řazení: klik na hlavičku nebo Ctrl+F3 až F7 ----

    public SortMode Sort { get; private set; } = SortMode.Name;
    public bool SortDescending { get; private set; }

    /// <summary>Řazení jako text pro uložení do nastavení, například "Size-" = podle velikosti sestupně.</summary>
    public string SortSpec
    {
        get => Sort + (SortDescending ? "-" : "");
        set
        {
            var text = value ?? "";
            SortDescending = text.EndsWith('-');
            Sort = Enum.TryParse<SortMode>(text.TrimEnd('-'), out var mode) ? mode : SortMode.Name;
            UpdateHeaders();
        }
    }

    /// <summary>Nastaví řazení; opakovaný výběr stejného řazení obrátí pořadí.</summary>
    public void SetSort(SortMode mode)
    {
        SortDescending = mode == Sort ? !SortDescending && mode != SortMode.Unsorted : mode is SortMode.Time or SortMode.Size;
        Sort = mode;
        UpdateHeaders();
        Refresh();
    }

    private void UpdateHeaders()
    {
        var arrow = SortDescending ? " ↓" : " ↑";
        FullView.Columns[0].Header = "Name" + (Sort == SortMode.Name ? arrow : "");
        FullView.Columns[1].Header = "Size" + (Sort == SortMode.Size ? arrow : "");
        FullView.Columns[2].Header = "Date" + (Sort == SortMode.Time ? arrow : "");
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column, Role: not GridViewColumnHeaderRole.Padding }) return;
        SetSort(FullView.Columns.IndexOf(column) switch { 1 => SortMode.Size, 2 => SortMode.Time, _ => SortMode.Name });
    }

    /// <summary>Složky jsou vždy před soubory. Podle velikosti a přípony se složky řadí podle názvu.</summary>
    private List<FileItem> Sorted(IEnumerable<FileItem> source, bool dirs)
    {
        var list = source.ToList();
        if (Sort == SortMode.Unsorted) return list;

        var cmp = StringComparer.CurrentCultureIgnoreCase;
        var mode = dirs && Sort is SortMode.Size or SortMode.Extension ? SortMode.Name : Sort;
        var desc = SortDescending && !(dirs && Sort is SortMode.Size or SortMode.Extension);

        IEnumerable<FileItem> Order<TKey>(Func<FileItem, TKey> key, IComparer<TKey>? comparer = null) =>
            desc
                ? list.OrderByDescending(key, comparer).ThenBy(i => i.Name, cmp)
                : list.OrderBy(key, comparer).ThenBy(i => i.Name, cmp);

        return (mode switch
        {
            SortMode.Time => Order(i => i.Modified),
            SortMode.Size => Order(i => i.Size),
            SortMode.Extension => Order(i => Path.GetExtension(i.Name), cmp),
            _ => Order(i => i.Name, cmp),
        }).ToList();
    }

    public ListView ListControl => List;

    public IReadOnlyList<FileItem> Selected =>
        List.SelectedItems.Cast<FileItem>().Where(i => !i.IsParent).ToList();

    public void Navigate(string path, string? select = null)
    {
        try
        {
            var refocus = IsKeyboardFocusWithin;
            var dir = new DirectoryInfo(path);
            var items = new List<FileItem>();
            if (dir.Parent is not null)
                items.Add(new FileItem { Name = "..", FullPath = dir.Parent.FullName, IsDir = true, IsParent = true });
            items.AddRange(Sorted(dir.EnumerateDirectories()
                .Select(d => new FileItem { Name = d.Name, FullPath = d.FullName, IsDir = true, Modified = d.LastWriteTime }), dirs: true));
            items.AddRange(Sorted(dir.EnumerateFiles()
                .Select(f => new FileItem { Name = f.Name, FullPath = f.FullName, Size = f.Length, Modified = f.LastWriteTime }), dirs: false));

            var previous = CurrentPath;
            ArchiveFile = null;
            CurrentPath = dir.FullName;
            PathText.Text = " " + CurrentPath + " ";
            List.ItemsSource = items;
            var back = items.FirstOrDefault(i => i.FullPath.Equals(select ?? previous, StringComparison.OrdinalIgnoreCase));
            List.SelectedItem = back ?? items.FirstOrDefault();
            ScrollToSelected(refocus);
            PathChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            MessageBox.Show(ex.Message, Loc.T("Nelze otevřít"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void Refresh()
    {
        // Výběr (i vícenásobný) se po obnovení zachová, aby ho nezrušilo dokončení kopírování běžícího na pozadí.
        var selected = List.SelectedItems.OfType<FileItem>().Where(i => !i.IsParent)
            .Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cursor = ((Keyboard.FocusedElement as ListViewItem)?.DataContext as FileItem)?.FullPath
                     ?? (List.SelectedItem as FileItem)?.FullPath;

        if (InArchive) ListArchive();
        else if (CurrentPath.Length > 0) Navigate(CurrentPath, select: cursor);

        if (selected.Count > 1)
        {
            foreach (var item in List.Items.OfType<FileItem>().Where(i => selected.Contains(i.FullPath)))
                if (!List.SelectedItems.Contains(item)) List.SelectedItems.Add(item);

            // kurzor ukazuje na jiný řádek než výběr: ten se do výběru nepřidává
            if (cursor is not null && !selected.Contains(cursor))
            {
                var extra = List.SelectedItems.OfType<FileItem>().FirstOrDefault(i => i.FullPath.Equals(cursor, StringComparison.OrdinalIgnoreCase));
                if (extra is not null) List.SelectedItems.Remove(extra);
            }
        }
    }

    /// <summary>Po zařazení úlohy zůstane vybraná jen položka pod kurzorem, takže jde hned vybírat další.</summary>
    public void CollapseSelection()
    {
        if (List.SelectedItems.Count <= 1) return;
        var cursor = (Keyboard.FocusedElement as ListViewItem)?.DataContext as FileItem ?? List.SelectedItem as FileItem;
        if (cursor is null) return;
        List.SelectedItems.Clear();
        List.SelectedItem = cursor;
        FocusList();
    }

    // ---- archivy ----

    private void OpenArchive(string file)
    {
        if (FileOps.IsTarGzName(file))
        {
            try
            {
                _archiveEntries = FileOps.ListTarGz(file)
                    .Select(r => new ArchiveRecord(r.Key, r.IsDir, r.Size, r.Mod))
                    .ToList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Loc.T("Nelze otevřít archiv"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ArchiveFile = file;
            _archiveDir = "";
            ListArchive();
            return;
        }
        try
        {
            using var archive = ArchiveFactory.OpenArchive(file);
            _archiveEntries = archive.Entries
                .Select(e => new ArchiveRecord(FileOps.NormalizeKey(e.Key ?? ""), e.IsDirectory,
                    e.IsDirectory ? 0 : e.Size, e.LastModifiedTime ?? default))
                .Where(r => r.Key.Length > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Loc.T("Nelze otevřít archiv"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ArchiveFile = file;
        _archiveDir = "";
        ListArchive();
    }

    private void ListArchive()
    {
        var refocus = IsKeyboardFocusWithin;
        var items = new List<FileItem> { new() { Name = "..", FullPath = "..", IsDir = true, IsParent = true } };
        var dirs = new SortedDictionary<string, DateTime>(StringComparer.CurrentCultureIgnoreCase);
        var files = new List<FileItem>();
        foreach (var r in _archiveEntries)
        {
            if (!r.Key.StartsWith(_archiveDir, StringComparison.Ordinal)) continue;
            var rest = r.Key[_archiveDir.Length..];
            if (rest.Length == 0) continue;
            var slash = rest.IndexOf('/');
            if (slash >= 0) dirs.TryAdd(rest[..slash], r.Modified);
            else if (r.IsDir) dirs.TryAdd(rest, r.Modified);
            else files.Add(new FileItem { Name = rest, FullPath = r.Key, Size = r.Size, Modified = r.Modified });
        }
        items.AddRange(Sorted(dirs.Select(d => new FileItem { Name = d.Key, FullPath = _archiveDir + d.Key, IsDir = true, Modified = d.Value }), dirs: true));
        items.AddRange(Sorted(files, dirs: false));

        PathText.Text = " " + ArchiveFile + "\\" + _archiveDir.TrimEnd('/').Replace('/', '\\') + " ";
        List.ItemsSource = items;
        List.SelectedItem = items.Count > 1 ? items[1] : items[0];
        ScrollToSelected(refocus);
        PathChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sestaví seznam souborů k rozbalení z vybraných položek (složky se rozvinou).</summary>
    public List<FileOps.ExtractEntry> BuildExtractPlan(IReadOnlyList<FileItem> items)
    {
        var plan = new List<FileOps.ExtractEntry>();
        foreach (var item in items)
        {
            if (!item.IsDir) { plan.Add(new FileOps.ExtractEntry(item.FullPath, item.Name)); continue; }
            var prefix = item.FullPath + "/";
            foreach (var r in _archiveEntries.Where(r => !r.IsDir && r.Key.StartsWith(prefix, StringComparison.Ordinal)))
                plan.Add(new FileOps.ExtractEntry(r.Key, item.Name + "/" + r.Key[prefix.Length..]));
        }
        return plan;
    }

    public void GoUp()
    {
        if (InArchive)
        {
            if (_archiveDir.Length == 0)
            {
                var file = ArchiveFile;
                Navigate(CurrentPath, select: file);
                return;
            }
            var trimmed = _archiveDir.TrimEnd('/');
            var idx = trimmed.LastIndexOf('/');
            _archiveDir = idx < 0 ? "" : trimmed[..(idx + 1)];
            ListArchive();
        }
        else if (Directory.GetParent(CurrentPath) is { } parent)
        {
            Navigate(parent.FullName);
        }
    }

    private void Open(FileItem item)
    {
        if (InArchive)
        {
            if (item.IsParent) GoUp();
            else if (item.IsDir) { _archiveDir = item.FullPath + "/"; ListArchive(); }
            return;
        }

        if (item.IsDir) Navigate(item.FullPath);
        else if (FileOps.IsArchiveName(item.Name))
            OpenArchive(item.FullPath);
        else
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.FullPath) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, Loc.T("Nelze spustit")); }
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InfoText.Text = List.SelectedItem is FileItem { IsParent: false } i
            ? $"{i.Name}  {i.SizeText}  {(i.Modified == default ? "" : i.Modified.ToString("d-M-yy H:mm"))}"
            : "";
        SelectionChangedOut?.Invoke(this, EventArgs.Empty);
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is FileItem item) Open(item);
    }

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && List.SelectedItem is FileItem item) { Open(item); e.Handled = true; }
        else if (e.Key == Key.Back) { GoUp(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            if (HandleArrow(e.Key, e.IsRepeat)) e.Handled = true;
        }
    }
}

public enum NavDirection { Left, Right, Up, Down }

public enum SortMode { Name, Extension, Time, Size, Unsorted }
