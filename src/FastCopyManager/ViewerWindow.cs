using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FastCopyManager;

/// <summary>Prohlížeč (F3: text / hex na F4) a jednoduchý editor (F4 v hlavním okně, F2 uloží).</summary>
public sealed class ViewerWindow : Window
{
    private const int MaxTextBytes = 4 * 1024 * 1024;
    private const int MaxHexBytes = 512 * 1024;

    private static readonly Encoding Cp1250;

    static ViewerWindow()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1250 = Encoding.GetEncoding(1250);
    }

    private readonly string _path;
    private readonly bool _edit;
    private readonly byte[] _bytes;
    private readonly bool _truncated;
    private readonly Encoding _encoding;
    private readonly bool _hasBom;
    private readonly TextBox _box;
    private readonly TextBlock _title;
    private bool _hex;
    private bool _dirty;

    private static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0xAA));
    private static readonly Brush Cyan = new SolidColorBrush(Color.FromRgb(0x00, 0xAA, 0xAA));
    private static readonly Brush Light = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));

    public ViewerWindow(string path, bool edit, Window owner)
    {
        _path = path;
        _edit = edit;
        Owner = owner;
        FontFamily = owner.FontFamily;
        FontSize = owner.FontSize;
        Width = 900;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Blue;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var info = new FileInfo(path);
        if (edit && info.Length > MaxTextBytes)
            throw new IOException(Loc.T("Soubor je pro editor příliš velký (limit {0} MB).", MaxTextBytes / 1048576));

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var len = (int)Math.Min(info.Length, MaxTextBytes);
            _bytes = new byte[len];
            var read = 0;
            while (read < len)
            {
                var n = fs.Read(_bytes, read, len - read);
                if (n <= 0) break;
                read += n;
            }
        }
        _truncated = info.Length > MaxTextBytes;
        (_encoding, _hasBom) = DetectEncoding(_bytes);

        _title = new TextBlock { Foreground = System.Windows.Media.Brushes.Black, Padding = new Thickness(6, 1, 6, 1) };
        var titleBar = new Border { Background = Cyan, Child = _title };
        DockPanel.SetDock(titleBar, Dock.Top);

        var keys = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.Black,
            Padding = new Thickness(6, 1, 6, 1),
            Text = edit ? Loc.T("F2 Uložit    Esc Zavřít") : Loc.T("F4 Text/Hex    Esc Zavřít"),
        };
        var keyBar = new Border { Background = Cyan, Child = keys };
        DockPanel.SetDock(keyBar, Dock.Bottom);

        _box = new TextBox
        {
            Background = Blue,
            Foreground = Light,
            CaretBrush = System.Windows.Media.Brushes.White,
            SelectionBrush = Cyan,
            BorderThickness = new Thickness(0),
            AcceptsReturn = true,
            AcceptsTab = true,
            IsReadOnly = !edit,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var dock = new DockPanel();
        dock.Children.Add(titleBar);
        dock.Children.Add(keyBar);
        dock.Children.Add(_box);
        Content = dock;

        ShowText();
        PreviewKeyDown += OnKeyDown;
        Closing += OnClosing;
        Loaded += (_, _) => _box.Focus();
    }

    private static (Encoding, bool) DetectEncoding(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (new UTF8Encoding(true), true);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (new UnicodeEncoding(false, true), true);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (new UnicodeEncoding(true, true), true);
        try
        {
            new UTF8Encoding(false, true).GetCharCount(b);
            return (new UTF8Encoding(false), false);
        }
        catch (DecoderFallbackException)
        {
            // Pro českou starší data je nejpravděpodobnější Windows-1250.
            return (Cp1250, false);
        }
    }

    private void ShowText()
    {
        _hex = false;
        var offset = _hasBom ? _encoding.GetPreamble().Length : 0;
        _box.Text = _encoding.GetString(_bytes, offset, _bytes.Length - offset);
        _box.FontFamily = FontFamily;
        _dirty = false;
        UpdateTitle();
    }

    private void ShowHex()
    {
        _hex = true;
        var n = Math.Min(_bytes.Length, MaxHexBytes);
        var sb = new StringBuilder(n / 16 * 78 + 64);
        for (var off = 0; off < n; off += 16)
        {
            sb.Append(off.ToString("X8")).Append("  ");
            var line = Math.Min(16, n - off);
            for (var i = 0; i < 16; i++)
            {
                sb.Append(i < line ? _bytes[off + i].ToString("X2") : "  ").Append(i == 7 ? "  " : " ");
            }
            sb.Append(' ');
            for (var i = 0; i < line; i++)
            {
                var c = _bytes[off + i];
                sb.Append(c >= 32 && c < 127 ? (char)c : '.');
            }
            sb.Append('\n');
        }
        if (n < _bytes.Length || _truncated) sb.Append(Loc.T("... (zobrazen jen začátek souboru)"));
        _box.Text = sb.ToString();
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var mode = _edit ? "Edit" : _hex ? "Hex" : "Text";
        var enc = _edit || !_hex ? $"  [{_encoding.WebName}]" : "";
        var trunc = _truncated && !_edit ? Loc.T("  (jen prvních 4 MB)") : "";
        _title.Text = $"{Path.GetFileName(_path)}  {mode}{enc}{trunc}{(_dirty ? Loc.T("  *změněno*") : "")}";
        Title = _title.Text;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || (!_edit && e.Key is Key.F3 or Key.F10))
        {
            Close();
            e.Handled = true;
        }
        else if (!_edit && e.Key == Key.F4)
        {
            if (_hex) ShowText(); else ShowHex();
            e.Handled = true;
        }
        else if (_edit && (e.Key == Key.F2 || (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)))
        {
            Save();
            e.Handled = true;
        }
        else if (_edit)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_dirty) { _dirty = true; UpdateTitle(); }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void Save()
    {
        try
        {
            var body = _encoding.GetBytes(_box.Text);
            var data = _hasBom ? _encoding.GetPreamble().Concat(body).ToArray() : body;
            var tmp = _path + ".fcm.tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, _path, true);
            _dirty = false;
            UpdateTitle();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("Nelze uložit"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_edit || !_dirty) return;
        var r = MessageBox.Show(this, Loc.T("Soubor je změněný. Uložit?"), "Editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) e.Cancel = true;
        else if (r == MessageBoxResult.Yes) Save();
    }
}
