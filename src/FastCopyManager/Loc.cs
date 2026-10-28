using System.Globalization;

namespace FastCopyManager;

/// <summary>
/// Jednoduchá lokalizace CS/EN. V kódu zůstává český text jako klíč, v angličtině se nahradí
/// překladem ze slovníku; když překlad chybí, použije se čeština. Parametry se vkládají přes {0}, {1} ...
/// </summary>
public static class Loc
{
    public static bool English { get; private set; }
    public static string Code => English ? "en" : "cs";

    /// <summary>Uložený jazyk, jinak podle jazyka Windows (česky jen na české lokalizaci).</summary>
    public static void Init(string? saved) =>
        English = saved is not null
            ? saved.Equals("en", StringComparison.OrdinalIgnoreCase)
            : !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("cs", StringComparison.OrdinalIgnoreCase);

    public static void Toggle() => English = !English;

    public static string T(string cs, params object[] args)
    {
        var text = English && En.TryGetValue(cs, out var en) ? en : cs;
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }

    private static readonly Dictionary<string, string> En = new()
    {
        // stavový řádek a obecné
        ["Připraveno"] = "Ready",
        ["Ověřit hashem"] = "Verify by hash",
        ["Chyba: "] = "Error: ",
        ["Zrušeno."] = "Cancelled.",
        ["Smazáno."] = "Deleted.",
        ["Zrušit"] = "Cancel",
        ["Potvrzení"] = "Confirm",
        ["Konflikt"] = "Conflict",
        ["Disk"] = "Drive",
        ["Nelze otevřít"] = "Cannot open",
        ["Nelze otevřít archiv"] = "Cannot open archive",
        ["Nelze spustit"] = "Cannot run",
        ["Nelze uložit"] = "Cannot save",
        ["[PAUZA] "] = "[PAUSED] ",
        ["zbývá"] = "left",
        ["(P = pauza, Esc = zrušit)"] = "(P = pause, Esc = cancel)",
        [", ověřeno"] = ", verified",
        ["Hotovo: {0} položek, {1:0.#} MB za {2:0.0} s ({3:0} MB/s)"] = "Done: {0} items, {1:0.#} MB in {2:0.0} s ({3:0} MB/s)",
        ["{0} položek"] = "{0} items",

        // operace
        ["Kopíruji"] = "Copying",
        ["Přesouvám"] = "Moving",
        ["Rozbaluji"] = "Extracting",
        ["Balím"] = "Packing",
        ["Spojuji"] = "Joining",

        // dialogy a zprávy v hlavním okně
        ["Písmeno disku ({0}):"] = "Drive letter ({0}):",
        ["Disk {0} není dostupný."] = "Drive {0} is not available.",
        ["Do archivu nelze kopírovat. ZIP vytvoříš přes Alt+F5."] = "Cannot copy into an archive. Create a ZIP with Alt+F5.",
        ["Z archivu lze jen kopírovat (rozbalit)."] = "From an archive you can only copy (extract).",
        ["Zdroj a cíl jsou stejná složka."] = "Source and destination are the same folder.",
        ["Některé položky v cíli už existují. Přepsat?"] = "Some items already exist in the destination. Overwrite?",
        ["Ano = přepsat, Ne = přeskočit."] = "Yes = overwrite, No = skip.",
        ["Některé soubory jsou větší než 4 GB a cíl je FAT32."] = "Some files are larger than 4 GB and the destination is FAT32.",
        ["Rozdělit je na části po 2 GB (.001, .002 ...)? Spojit je půjde přes Files > Join parts."] = "Split them into 2 GB parts (.001, .002 ...)? You can join them with Files > Join parts.",
        ["Balit lze jen mezi běžnými složkami."] = "Packing works only between regular folders.",
        ["Zabalit"] = "Pack",
        ["Náhled"] = "Quick view",
        ["Náhled v archivu není dostupný. Rozbal soubor (F5)."] = "Preview inside an archive is not available. Extract the file (F5).",
        ["Binární soubor (náhled: F3, v něm F4 = hex)."] = "Binary file (preview: F3, then F4 = hex).",
        ["Špatné heslo nebo poškozený archiv."] = "Wrong password or damaged archive.",
        ["Formát:"] = "Format:",
        ["Komprese:"] = "Compression:",
        ["Bez komprese"] = "Store",
        ["Rychlá"] = "Fast",
        ["Normální"] = "Normal",
        ["Nejlepší"] = "Best",
        ["Heslo (nepovinné; ZIP, 7z a RAR, AES-256):"] = "Password (optional; ZIP, 7z and RAR, AES-256):",
        ["nenalezen 7-Zip"] = "7-Zip not found",
        ["nenalezen WinRAR"] = "WinRAR not found",
        ["Nenalezen 7-Zip (7z.exe). Nainstaluj 7-Zip nebo dej 7z.exe a 7z.dll do složky Tools vedle aplikace."] = "7-Zip (7z.exe) not found. Install 7-Zip or put 7z.exe and 7z.dll into a Tools folder next to the application.",
        ["RAR umí vytvářet jen WinRAR (Rar.exe, placený se zkušební dobou). Nainstaluj WinRAR nebo dej Rar.exe do složky Tools vedle aplikace."] = "Only WinRAR (Rar.exe, paid with a trial period) can create RAR archives. Install WinRAR or put Rar.exe into a Tools folder next to the application.",
        ["Chyba nástroje {0}: {1}"] = "Tool {0} failed: {1}",
        ["Heslo znovu:"] = "Repeat password:",
        ["Heslo"] = "Password",
        ["Neplatný název archivu."] = "Invalid archive name.",
        ["Hesla se neshodují."] = "The passwords do not match.",
        ["Zadej heslo k archivu:"] = "Enter the archive password:",
        ["Probíhá kopírování. Opravdu ukončit?"] = "A copy is in progress. Really quit?",
        ["Fronta vyprázdněna."] = "Queue cleared.",
        ["ve frontě"] = "queued",
        ["Při probíhající operaci nelze mazat ani vytvářet složky."] = "Cannot delete or create folders while an operation is running.",
        ["Rozbalovat lze jen do běžné složky."] = "Extracting works only into a regular folder.",
        ["Vyber archiv (.zip, .rar, .7z, .tar)."] = "Select an archive (.zip, .rar, .7z, .tar).",
        ["Název archivu:"] = "Archive name:",
        ["{0} už existuje. Přepsat?"] = "{0} already exists. Overwrite?",
        ["Soubory z archivu nejdřív rozbal (F5)."] = "Extract files from the archive first (F5).",
        ["Spojovat lze jen mezi běžnými složkami."] = "Joining works only between regular folders.",
        ["Vyber jednu část rozděleného souboru (.001, .002 ...)."] = "Select one part of a split file (.001, .002 ...).",
        ["V archivu nelze měnit."] = "Archives are read-only.",
        ["Nová složka"] = "New folder",
        ["Název složky:"] = "Folder name:",
        ["Smazat {0} {1}?"] = "Delete {0} {1}?",
        ["do koše"] = "to the Recycle Bin",
        ["NATRVALO (tento disk nemá koš)"] = "PERMANENTLY (this drive has no Recycle Bin)",
        ["Jazyk: čeština"] = "Language: English",

        // jádro (chyby)
        ["Cílová složka neexistuje: {0}"] = "Destination folder does not exist: {0}",
        ["Do cíle nelze zapisovat (jen pro čtení nebo chybí oprávnění): {0}"] = "Cannot write to the destination (read-only or no permission): {0}",
        ["V cíli není dost místa: potřeba {0} MB, volno {1} MB."] = "Not enough space in the destination: need {0} MB, free {1} MB.",
        ["V cíli není dost místa: potřeba {0} MB."] = "Not enough space in the destination: need {0} MB.",
        ["Ověření selhalo (hash se neshoduje): {0}"] = "Verification failed (hash mismatch): {0}",
        ["Vyber soubor s příponou .001, .002 ..."] = "Select a file with the extension .001, .002 ...",
        ["Nenalezena první část (.001)."] = "First part (.001) not found.",
        ["Spojený soubor by měl přes 4 GB a cíl je FAT32."] = "The joined file would exceed 4 GB and the destination is FAT32.",
        ["Nebezpečná cesta v archivu: {0}"] = "Unsafe path in archive: {0}",
        ["Nelze kopírovat složku sama do sebe."] = "Cannot copy a folder into itself.",
        ["Soubor {0} je větší než 4 GB a cíl je FAT32."] = "File {0} is larger than 4 GB and the destination is FAT32.",

        // prohlížeč / editor
        ["Soubor je pro editor příliš velký (limit {0} MB)."] = "The file is too large for the editor (limit {0} MB).",
        ["F2 Uložit    Esc Zavřít"] = "F2 Save    Esc Close",
        ["F4 Text/Hex    Esc Zavřít"] = "F4 Text/Hex    Esc Close",
        ["... (zobrazen jen začátek souboru)"] = "... (only the beginning of the file is shown)",
        ["  (jen prvních 4 MB)"] = "  (first 4 MB only)",
        ["  *změněno*"] = "  *modified*",
        ["Soubor je změněný. Uložit?"] = "The file has been modified. Save?",
    };
}
