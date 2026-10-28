namespace FastCopyManager;

public sealed class FileItem
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDir { get; init; }
    public bool IsParent { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }

    public string Display => IsDir ? $"[{Name}]" : Name;
    public string SizeText => IsParent ? "" : IsDir ? "<DIR>" : FormatSize(Size);
    public string DateText => IsParent ? "" : Modified.ToString("dd.MM.yyyy HH:mm");

    private static string FormatSize(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} kB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.#} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:0.##} GB",
    };
}
