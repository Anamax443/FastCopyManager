# FastCopyManager

Dvoupanelový správce souborů pro Windows (WPF, .NET 10) ve stylu Norton Commanderu, s vlastním kopírovacím jádrem. Žádný cizí program.

## Ovládání

| Klávesa | Akce |
|---|---|
| Tab | přepnout panel |
| Enter / Backspace | otevřít / o úroveň výš |
| F5 | kopírovat do druhého panelu |
| F6 | přesunout |
| F7 | nová složka |
| F8 / Delete | smazat (do koše; na flashce natrvalo, s upozorněním) |
| F10 | konec |

## Kopírovací jádro

`FileOps.cs`: bufferované proudové kopírování (1 MB), zápis do dočasného `.fcm.tmp` a přejmenování až po dokončení (přerušená kopie nenechá napůl zapsaný soubor), zachování data změny, kontrola limitu 4 GB na FAT32.

## Build a publikace

```powershell
cd src\FastCopyManager
dotnet build -c Release
.\tools\publish.ps1     # přenosný self-contained build do publish\ + podpis
```

- publikuje se self-contained do složky, bez single-file a bez packerů (ty spouští antiviry),
- z FAT/exFAT flashky se exe spouští bez značky „staženo z internetu“, takže SmartScreen většinou mlčí,
- self-signed certifikát z `publish.ps1` je důvěryhodný jen na stroji, kde vznikl; trvalé řešení pro cizí PC je placený podpisový certifikát.

## Písmo a licence

Vzhled napodobuje Norton Commander (modrý podklad, azurový rámeček, žlutý kurzor, DOSové písmo). Použité písmo **MxPlus IBM VGA 8x16** je z balíku *The Ultimate Oldschool PC Font Pack* (VileR, https://int10h.org/oldschool-pc-fonts/), licence CC BY-SA 4.0, viz `src/FastCopyManager/Fonts/LICENSE-font-CC-BY-SA-4.0.txt`.

## Archivy

- **Enter na .zip / .rar / .7z / .tar** otevře archiv jako složku, Backspace nebo `..` ze kořene vrátí do složky s archivem.
- **F5** v archivu rozbalí vybrané soubory a složky do druhého panelu (vlastním kopírovacím cyklem, s kontrolou cesty proti podvrženým archivům).
- **Alt+F5** zabalí vybrané soubory a složky do ZIPu v druhém panelu.
- RAR a 7z jen čtení a rozbalení; vytvářet jde jen ZIP (RAR je uzavřený formát). Do archivu se zapisovat nedá.
- Čtení archivů zajišťuje knihovna SharpCompress (MIT).
