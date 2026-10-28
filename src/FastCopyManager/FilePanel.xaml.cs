using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SharpCompress.Archives;

namespace FastCopyManager;

public partial class FilePanel : UserControl
{
    private static readonly string[] ArchiveExtensions = { ".zip", ".rar", ".7z", ".tar" };

    private sealed record ArchiveRecord(string Key, bool IsDir, long Size, DateTime Modified);

    private List<ArchiveRecord> _archiveEntries = new();
    private string _archiveDir = "";   // "" = kořen archivu, jinak "slozka/podslozka/"

    /// <summary>Při procházení archivu je to složka, ve které archiv leží.</summary>
    public string CurrentPath { get; private set; } = "";

    public string? ArchiveFile { get; private set; }
    public bool InArchive => ArchiveFile is not null;

    public event EventHandler? PathChanged;

    public bool Brief { get; private set; }

    /// <summary>Přepíná mezi plným výpisem (název, velikost, datum) a výpisem ve více sloupcích.</summary>
    public void SetBrief(bool brief)
    {
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
        ScrollToSelected();
    }

    /// <summary>Rolování až po dokončení rozložení, jinak se ve Brief režimu odroluje na špatné místo.</summary>
    private void ScrollToSelected() =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (List.SelectedItem is not null) List.ScrollIntoView(List.SelectedItem);
        });

    /// <summary>V režimu Brief šipky vlevo/vpravo skáčou o celý sloupec.</summary>
    private void MoveByColumn(int direction)
    {
        if (List.Items.Count == 0) return;
        var probe = List.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
        var rowHeight = probe is { ActualHeight: > 0 } ? probe.ActualHeight : 20;
        var rows = Math.Max(1, (int)(List.ActualHeight / rowHeight) - 1);
        var index = Math.Clamp(List.SelectedIndex + direction * rows, 0, List.Items.Count - 1);
        List.SelectedIndex = index;
        List.ScrollIntoView(List.SelectedItem);
        (List.ItemContainerGenerator.ContainerFromIndex(index) as UIElement)?.Focus();
    }

    public FilePanel()
    {
        InitializeComponent();
    }

    public ListView ListControl => List;

    public IReadOnlyList<FileItem> Selected =>
        List.SelectedItems.Cast<FileItem>().Where(i => !i.IsParent).ToList();

    public void Navigate(string path, string? select = null)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            var items = new List<FileItem>();
            if (dir.Parent is not null)
                items.Add(new FileItem { Name = "..", FullPath = dir.Parent.FullName, IsDir = true, IsParent = true });
            items.AddRange(dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(d => new FileItem { Name = d.Name, FullPath = d.FullName, IsDir = true, Modified = d.LastWriteTime }));
            items.AddRange(dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(f => new FileItem { Name = f.Name, FullPath = f.FullName, Size = f.Length, Modified = f.LastWriteTime }));

            var previous = CurrentPath;
            ArchiveFile = null;
            CurrentPath = dir.FullName;
            PathText.Text = " " + CurrentPath + " ";
            List.ItemsSource = items;
            var back = items.FirstOrDefault(i => i.FullPath.Equals(select ?? previous, StringComparison.OrdinalIgnoreCase));
            List.SelectedItem = back ?? items.FirstOrDefault();
            ScrollToSelected();
            PathChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            MessageBox.Show(ex.Message, Loc.T("Nelze otevřít"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void Refresh()
    {
        if (InArchive) ListArchive();
        else if (CurrentPath.Length > 0) Navigate(CurrentPath);
    }

    // ---- archivy ----

    private void OpenArchive(string file)
    {
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
        items.AddRange(dirs.Select(d => new FileItem { Name = d.Key, FullPath = _archiveDir + d.Key, IsDir = true, Modified = d.Value }));
        items.AddRange(files.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase));

        PathText.Text = " " + ArchiveFile + "\\" + _archiveDir.TrimEnd('/').Replace('/', '\\') + " ";
        List.ItemsSource = items;
        List.SelectedItem = items.Count > 1 ? items[1] : items[0];
        ScrollToSelected();
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

    private void GoUp()
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
        else if (ArchiveExtensions.Contains(Path.GetExtension(item.Name), StringComparer.OrdinalIgnoreCase))
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
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is FileItem item) Open(item);
    }

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && List.SelectedItem is FileItem item) { Open(item); e.Handled = true; }
        else if (e.Key == Key.Back) { GoUp(); e.Handled = true; }
        else if (Brief && e.Key is Key.Left or Key.Right && Keyboard.Modifiers == ModifierKeys.None)
        {
            MoveByColumn(e.Key == Key.Left ? -1 : 1);
            e.Handled = true;
        }
    }
}
