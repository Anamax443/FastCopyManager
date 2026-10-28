using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FastCopyManager;

public partial class MainWindow : Window
{
    private static readonly string BaseDir = AppContext.BaseDirectory;
    private static readonly string SettingsPath = Path.Combine(BaseDir, "settings.json");

    private static readonly string[] ExeCandidates =
    {
        Path.Combine(BaseDir, "FastCopy", "FastCopy.exe"),
        Path.Combine(BaseDir, "..", "FastCopy", "FastCopy.exe"),
        @"C:\Program Files\FastCopy\FastCopy.exe",
        @"C:\Program Files (x86)\FastCopy\FastCopy.exe",
        @"C:\Program Files\FastCopy64\FastCopy.exe",
    };

    public MainWindow()
    {
        InitializeComponent();
        ExeBox.Text = ExeCandidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists) ?? "";
        LoadSettings();
        Closing += (_, _) => SaveSettings();
    }

    private record Settings(string? Exe, string? Source, string? Target, int Mode, bool Verify);

    private void LoadSettings()
    {
        try
        {
            var st = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            if (st is null) return;
            if (!string.IsNullOrEmpty(st.Exe) && File.Exists(st.Exe)) ExeBox.Text = st.Exe;
            SourceBox.Text = st.Source ?? "";
            TargetBox.Text = st.Target ?? "";
            ModeBox.SelectedIndex = Math.Clamp(st.Mode, 0, ModeBox.Items.Count - 1);
            VerifyBox.IsChecked = st.Verify;
        }
        catch { /* první spuštění nebo poškozený soubor */ }
    }

    private void SaveSettings()
    {
        try
        {
            var st = new Settings(ExeBox.Text, SourceBox.Text, TargetBox.Text, ModeBox.SelectedIndex, VerifyBox.IsChecked == true);
            File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(st));
        }
        catch { /* flashka jen pro čtení, nic se neukládá */ }
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "FastCopy.exe|FastCopy.exe|Programy|*.exe" };
        if (dlg.ShowDialog() == true) ExeBox.Text = dlg.FileName;
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e) => Pick(SourceBox);
    private void BrowseTarget_Click(object sender, RoutedEventArgs e) => Pick(TargetBox);

    private static void Pick(TextBox box)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() == true) box.Text = dlg.FolderName;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var exe = ExeBox.Text.Trim();
        var source = SourceBox.Text.Trim();
        var target = TargetBox.Text.Trim();

        if (!File.Exists(exe)) { Warn("Zadej platnou cestu k FastCopy.exe."); return; }
        if (!Directory.Exists(source)) { Warn("Zdrojová složka neexistuje."); return; }
        if (target.Length == 0) { Warn("Zadej cílovou složku."); return; }

        var mode = (string)((ComboBoxItem)ModeBox.SelectedItem).Tag;
        if (mode is "Sync" or "Move" &&
            MessageBox.Show($"Režim {mode} může mazat soubory. Pokračovat?", "Potvrzení",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add($"/cmd={mode}");
        if (VerifyBox.IsChecked == true) psi.ArgumentList.Add("/verify");
        psi.ArgumentList.Add("/auto_close");
        psi.ArgumentList.Add("/log");
        psi.ArgumentList.Add("/to=" + target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        psi.ArgumentList.Add(source);

        Append($"> {Path.GetFileName(exe)} {string.Join(' ', psi.ArgumentList)}");
        StartButton.IsEnabled = false;
        StatusText.Text = "Probíhá…";
        try
        {
            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync();
            Append($"Hotovo, kód ukončení {proc.ExitCode}.");
            StatusText.Text = proc.ExitCode == 0 ? "Hotovo" : $"Chyba ({proc.ExitCode})";
        }
        catch (Exception ex)
        {
            Append("Chyba: " + ex.Message);
            StatusText.Text = "Chyba";
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    private void Append(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private static void Warn(string msg) =>
        MessageBox.Show(msg, "FastCopyManager", MessageBoxButton.OK, MessageBoxImage.Information);
}
