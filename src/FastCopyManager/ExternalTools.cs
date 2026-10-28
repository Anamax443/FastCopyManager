using System.IO;

namespace FastCopyManager;

/// <summary>
/// Vyhledání externích nástrojů pro formáty, které aplikace sama zapisovat neumí:
/// 7z (7-Zip) a RAR (WinRAR, formát RAR smí zapisovat jen jeho Rar.exe).
/// Pořadí pro 7z: složka Tools vedle exe, 7za.exe zabudovaný v aplikaci (rozbalí se do dočasné složky),
/// potom 7-Zip v Program Files a v PATH. Aplikace je tak z flashky použitelná i na počítači bez instalací.
/// </summary>
public static class ExternalTools
{
    public static string? Find7z() =>
        FindBesideExe(new[] { "7za.exe", "7z.exe" })
        ?? ExtractEmbedded7z()
        ?? FindInSystem(new[] { "7za.exe", "7z.exe" }, Path.Combine("7-Zip", "7z.exe"));

    public static string? FindRar() =>
        FindBesideExe(new[] { "Rar.exe" })
        ?? FindInSystem(new[] { "Rar.exe" }, Path.Combine("WinRAR", "Rar.exe"));

    private static string? FindBesideExe(string[] names)
    {
        foreach (var name in names)
        {
            foreach (var candidate in new[] { Path.Combine(AppPaths.ExeDirectory, "Tools", name), Path.Combine(AppPaths.ExeDirectory, name) })
                if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? FindInSystem(string[] names, string programFilesRelative)
    {
        var candidates = new List<string>();
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

    /// <summary>
    /// 7za.exe (7-Zip Extra) je zabudovaný v aplikaci a pro spuštění se musí rozbalit do souboru.
    /// Rozbalí se jednou do %TEMP%\FastCopyManager\7-Zip; stejně se přiloží i licence.
    /// </summary>
    private static string? ExtractEmbedded7z()
    {
        var assembly = typeof(ExternalTools).Assembly;
        using var exe = assembly.GetManifestResourceStream("7za.exe");
        if (exe is null) return null;

        var directory = Path.Combine(Path.GetTempPath(), "FastCopyManager", "7-Zip");
        var target = Path.Combine(directory, "7za.exe");
        try
        {
            Directory.CreateDirectory(directory);
            if (!File.Exists(target) || new FileInfo(target).Length != exe.Length)
            {
                var temporary = target + "." + Environment.ProcessId + ".tmp";
                using (var output = File.Create(temporary)) exe.CopyTo(output);
                File.Move(temporary, target, true);
            }

            var licenseTarget = Path.Combine(directory, "7-Zip-License.txt");
            using var license = assembly.GetManifestResourceStream("7-Zip-License.txt");
            if (license is not null && !File.Exists(licenseTarget))
            {
                using var output = File.Create(licenseTarget);
                license.CopyTo(output);
            }
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
