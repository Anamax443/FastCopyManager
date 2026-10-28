using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FastCopyManager;

public enum PackFormat { Zip, TarGz, Tar }

/// <summary>Co si uživatel v dialogu Zabalit vybral. Name už obsahuje příponu podle formátu.</summary>
public sealed record PackRequest(string Name, PackFormat Format, CompressionLevel Level, string? Password);

internal static class DialogStyle
{
    public static readonly Brush Teal = new SolidColorBrush(Color.FromRgb(0x00, 0xAA, 0xAA));

    public static void Apply(Window dialog, Window owner, string title, double width)
    {
        dialog.Owner = owner;
        dialog.Title = title;
        dialog.Width = width;
        dialog.SizeToContent = SizeToContent.Height;
        dialog.ResizeMode = ResizeMode.NoResize;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        dialog.FontFamily = owner.FontFamily;
        dialog.FontSize = owner.FontSize;
        dialog.Background = Teal;
        TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);
    }
}

/// <summary>Dialog Zabalit (Alt+F5): název, formát, komprese a volitelné heslo.</summary>
public sealed class PackDialog : Window
{
    private readonly TextBox _name = new();
    private readonly ComboBox _format = new();
    private readonly ComboBox _level = new();
    private readonly PasswordBox _password = new();
    private readonly PasswordBox _repeat = new();
    private PackRequest? _result;

    public PackDialog(Window owner, string baseName)
    {
        DialogStyle.Apply(this, owner, Loc.T("Zabalit"), 560);

        _name.Text = baseName;
        _format.ItemsSource = new[] { "ZIP", "TAR.GZ", "TAR" };
        _format.SelectedIndex = 0;
        _level.ItemsSource = new[] { Loc.T("Bez komprese"), Loc.T("Rychlá"), Loc.T("Normální"), Loc.T("Nejlepší") };
        _level.SelectedIndex = 1;
        _format.SelectionChanged += (_, _) =>
        {
            var zip = _format.SelectedIndex == 0;
            _password.IsEnabled = _repeat.IsEnabled = zip;
            if (!zip) { _password.Clear(); _repeat.Clear(); }
        };

        var grid = new Grid { Margin = new Thickness(14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(string label, UIElement control)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(text, row);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            ((FrameworkElement)control).Margin = new Thickness(0, 4, 0, 4);
            ((FrameworkElement)control).VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(text);
            grid.Children.Add(control);
        }
        Row(Loc.T("Název archivu:"), _name);
        Row(Loc.T("Formát:"), _format);
        Row(Loc.T("Komprese:"), _level);
        Row(Loc.T("Heslo (jen ZIP, šifrování AES-256, nepovinné):"), _password);
        Row(Loc.T("Heslo znovu:"), _repeat);

        var ok = new Button { Content = "OK", Width = 90, IsDefault = true };
        var cancel = new Button { Content = Loc.T("Zrušit"), Width = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => Confirm();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var rowButtons = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(buttons, rowButtons);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    private void Confirm()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, Loc.T("Neplatný název archivu."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var format = (PackFormat)_format.SelectedIndex;
        var password = format == PackFormat.Zip && _password.Password.Length > 0 ? _password.Password : null;
        if (password is not null && password != _repeat.Password)
        {
            MessageBox.Show(this, Loc.T("Hesla se neshodují."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var extension = format switch { PackFormat.Zip => ".zip", PackFormat.TarGz => ".tar.gz", _ => ".tar" };
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name += extension;

        var level = _level.SelectedIndex switch
        {
            0 => CompressionLevel.NoCompression,
            2 => CompressionLevel.Optimal,
            3 => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Fastest,
        };
        _result = new PackRequest(name, format, level, password);
        DialogResult = true;
    }

    public PackRequest? Ask() => ShowDialog() == true ? _result : null;
}

/// <summary>Dotaz na heslo k zaheslovanému archivu.</summary>
public sealed class PasswordDialog : Window
{
    private readonly PasswordBox _box = new();

    private PasswordDialog(Window owner, string label)
    {
        DialogStyle.Apply(this, owner, Loc.T("Heslo"), 480);
        var ok = new Button { Content = "OK", Width = 90, IsDefault = true };
        var cancel = new Button { Content = Loc.T("Zrušit"), Width = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap });
        _box.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => _box.Focus();
    }

    /// <summary>Vrací zadané heslo, nebo null při zrušení.</summary>
    public static string? Ask(Window owner, string label)
    {
        var dialog = new PasswordDialog(owner, label);
        return dialog.ShowDialog() == true && dialog._box.Password.Length > 0 ? dialog._box.Password : null;
    }
}
