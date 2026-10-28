using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FastCopyManager;

public partial class FilePanel : UserControl
{
    private bool _loadingDrives;

    public string CurrentPath { get; private set; } = "";

    public FilePanel()
    {
        InitializeComponent();
        LoadDrives();
    }

    public ListView ListControl => List;

    public IReadOnlyList<FileItem> Selected =>
        List.SelectedItems.Cast<FileItem>().Where(i => !i.IsParent).ToList();

    public void LoadDrives()
    {
        _loadingDrives = true;
        DriveBox.ItemsSource = DriveInfo.GetDrives().Where(d => d.IsReady)
            .Select(d => $"{d.Name}  {d.VolumeLabel}").ToList();
        _loadingDrives = false;
    }

    public void Navigate(string path)
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
            CurrentPath = dir.FullName;
            PathText.Text = CurrentPath;
            List.ItemsSource = items;
            var back = items.FirstOrDefault(i => i.FullPath.Equals(previous, StringComparison.OrdinalIgnoreCase));
            List.SelectedItem = back ?? items.FirstOrDefault();
            if (List.SelectedItem is not null) List.ScrollIntoView(List.SelectedItem);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            MessageBox.Show(ex.Message, "Nelze otevřít", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void Refresh()
    {
        if (CurrentPath.Length > 0) Navigate(CurrentPath);
    }

    private void Open(FileItem item)
    {
        if (item.IsDir) Navigate(item.FullPath);
        else
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.FullPath) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Nelze spustit"); }
    }

    private void DriveBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDrives || DriveBox.SelectedItem is not string s) return;
        Navigate(s[..3]);
        List.Focus();
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is FileItem item) Open(item);
    }

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && List.SelectedItem is FileItem item) { Open(item); e.Handled = true; }
        else if (e.Key == Key.Back && Directory.GetParent(CurrentPath) is { } parent) { Navigate(parent.FullName); e.Handled = true; }
    }
}
