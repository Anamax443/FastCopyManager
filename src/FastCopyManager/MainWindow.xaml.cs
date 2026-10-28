using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FastCopyManager;

public partial class MainWindow : Window
{
    private static readonly string[] ExeCandidates =
    {
        @"C:\Program Files\FastCopy\FastCopy.exe",
        @"C:\Program Files (x86)\FastCopy\FastCopy.exe",
        @"C:\Program Files\FastCopy64\FastCopy.exe",
    };

    public MainWindow()
    {
        InitializeComponent();
        ExeBox.Text = ExeCandidates.FirstOrDefault(File.Exists) ?? "";
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
