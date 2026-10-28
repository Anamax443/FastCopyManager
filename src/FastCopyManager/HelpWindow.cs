using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FastCopyManager;

/// <summary>Nápověda (F1): přehled kláves v češtině nebo angličtině podle zvoleného jazyka.</summary>
public sealed class HelpWindow : Window
{
    private const string Cs = @"F1          Nápověda
F3          Prohlížeč (v něm F4 = text / hex)
F4          Editor (F2 = uložit)
F5          Kopírovat do druhého panelu (v archivu rozbalit)
F6          Přesunout
F7          Nová složka
F8, Delete  Smazat
F9          Menu nahoře
F10         Konec

Alt+F5      Zabalit vybrané (dialog: ZIP / 7z / RAR / TAR.GZ / TAR, komprese, heslo)
            7z zapisuje zabudovaný 7za.exe; RAR jen když je k dispozici WinRAR (Rar.exe)
Alt+F9      Rozbalit vybraný archiv (zip, rar, 7z, tar) do druhého panelu
Alt+F6      Spojit rozdělený soubor (.001, .002 ...)
Alt+F1/F2   Změnit disk v levém / pravém panelu

Ctrl+F1/F2  Plný výpis / více sloupců (levý / pravý panel)
Ctrl+F3     Řadit podle názvu        Ctrl+F4  podle přípony
Ctrl+F5     Řadit podle času         Ctrl+F6  podle velikosti
Ctrl+F7     Neřadit                  (opakování obrátí pořadí)
Ctrl+Q      Náhled souboru (txt, md, log ...) ve druhém panelu; Tab nebo šipka ho zavře
Ctrl+U      Prohodit panely
Ctrl+R      Znovu načíst
Ctrl+L      Jazyk CS / EN

Tab         Druhý panel (také šipky vlevo / vpravo)
Alt (klepnout) nebo Alt+Nahoru   Horní menu odkudkoli (znovu Alt = zpět)
Šipka nahoru z prvního řádku   horní menu (Enter rozbalí, šipka dolů zpět do panelu)
Šipka dolů z posledního řádku  příkazový řádek a dolní lišta
Enter       Otevřít složku; na .zip .rar .7z .tar otevře archiv jako složku
Backspace   O úroveň výš
P / Esc     Během kopírování pozastavit / zrušit (Shift+Esc zruší i frontu)
F5, F6, Alt+F5, Alt+F9 při běžící operaci zařadí další úlohu do fronty
Alt (podržet) Dolní lišta ukáže příkazy na Alt

Esc zavře nápovědu.";

    private const string En = @"F1          Help
F3          Viewer (inside it F4 = text / hex)
F4          Editor (F2 = save)
F5          Copy to the other panel (inside an archive: extract)
F6          Move
F7          New folder
F8, Delete  Delete
F9          Top menu
F10         Quit

Alt+F5      Pack selected items (dialog: ZIP / 7z / RAR / TAR.GZ / TAR, compression, password)
            7z is written by the built-in 7za.exe; RAR only when WinRAR (Rar.exe) is available
Alt+F9      Unpack the selected archive (zip, rar, 7z, tar) into the other panel
Alt+F6      Join a split file (.001, .002 ...)
Alt+F1/F2   Change drive in the left / right panel

Ctrl+F1/F2  Full list / multi-column list (left / right panel)
Ctrl+F3     Sort by name             Ctrl+F4  by extension
Ctrl+F5     Sort by time             Ctrl+F6  by size
Ctrl+F7     Unsorted                 (repeating reverses the order)
Ctrl+Q      Quick view of the file (txt, md, log ...) in the other panel; Tab or an arrow closes it
Ctrl+U      Swap panels
Ctrl+R      Re-read
Ctrl+L      Language CS / EN

Tab         Other panel (also Left / Right arrows)
Alt (tap) or Alt+Up   Top menu from anywhere (Alt again = back)
Up arrow on the first row    top menu (Enter opens it, Down returns to the panel)
Down arrow on the last row   command line and bottom bar
Enter       Open a folder; on .zip .rar .7z .tar opens the archive like a folder
Backspace   Go up one level
P / Esc     Pause / cancel while copying (Shift+Esc also clears the queue)
F5, F6, Alt+F5, Alt+F9 while an operation runs add the next job to the queue
Alt (hold)  The bottom bar shows the Alt commands

Esc closes this help.";

    public HelpWindow(Window owner)
    {
        Owner = owner;
        FontFamily = owner.FontFamily;
        FontSize = owner.FontSize;
        Width = 900;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = "Help";
        var blue = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0xAA));
        var cyan = new SolidColorBrush(Color.FromRgb(0x00, 0xAA, 0xAA));
        Background = blue;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var bar = new Border
        {
            Background = cyan,
            Child = new TextBlock { Text = "FastCopyManager - Help", Foreground = Brushes.Black, Padding = new Thickness(6, 1, 6, 1) },
        };
        DockPanel.SetDock(bar, Dock.Top);

        var box = new TextBox
        {
            Text = Loc.English ? En : Cs,
            IsReadOnly = true,
            Background = blue,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var dock = new DockPanel();
        dock.Children.Add(bar);
        dock.Children.Add(box);
        Content = dock;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.F1 or Key.F10) { Close(); e.Handled = true; }
        };
        Loaded += (_, _) => box.Focus();
    }
}
