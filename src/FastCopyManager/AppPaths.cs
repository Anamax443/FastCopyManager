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

    /// <summary>Podsložka pro nastavení, aby vedle exe nic dalšího neleželo.</summary>
    public static string DataDirectory => Path.Combine(ExeDirectory, "Data");
}
