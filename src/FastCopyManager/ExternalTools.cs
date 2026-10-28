using System.IO;

namespace FastCopyManager;

/// <summary>
/// Vyhledání externích nástrojů pro formáty, které aplikace sama zapisovat neumí:
/// 7z (7-Zip) a RAR (WinRAR, formát RAR smí zapisovat jen jeho Rar.exe).
/// Pořadí: nejdřív složka Tools vedle aplikace (přenosně z flashky, bez instalace), pak Program Files a PATH.
/// S aplikací se dodává 7za.exe (7-Zip Extra) ve složce Tools.
/// </summary>
public static class ExternalTools
{
    public static string? Find7z() =>
        Find(new[] { "7za.exe", "7z.exe" }, Path.Combine("7-Zip", "7z.exe"));

    public static string? FindRar() =>
        Find(new[] { "Rar.exe" }, Path.Combine("WinRAR", "Rar.exe"));

    private static string? Find(string[] names, string programFilesRelative)
    {
        var candidates = new List<string>();
        foreach (var name in names)
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "Tools", name));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, name));
        }
        foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            var folder = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(folder)) candidates.Add(Path.Combine(folder, programFilesRelative));
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try { candidates.Add(Path.Combine(dir.Trim((char)34), name)); }
                catch (ArgumentException) { /* neplatná položka v PATH */ }
            }
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}
