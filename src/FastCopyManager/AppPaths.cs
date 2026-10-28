using System.IO;

namespace FastCopyManager;

/// <summary>
/// Cesty vázané na umístění exe. U aplikace zabalené do jednoho souboru ukazuje AppContext.BaseDirectory
/// do dočasné složky, proto se vychází z cesty k procesu.
/// </summary>
public static class AppPaths
{
    /// <summary>Složka, ve které leží spuštěný exe (u flashky její kořen).</summary>
    public static string ExeDirectory { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>
    /// Kam se ukládá nastavení. Výchozí je profil uživatele na daném počítači (%LOCALAPPDATA%\FastCopyManager),
    /// takže vedle exe nic nevznikne. Kdo chce nastavení nosit s sebou, vytvoří vedle exe složku Data:
    /// pokud existuje, použije se ona (přenosný režim).
    /// </summary>
    public static string SettingsDirectory { get; } =
        Directory.Exists(Path.Combine(ExeDirectory, "Data"))
            ? Path.Combine(ExeDirectory, "Data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastCopyManager");
}
