# FastCopyManager

Dvoupanelový správce souborů pro Windows (WPF, .NET 10) ve stylu Norton Commanderu, s vlastním kopírovacím jádrem. Žádný cizí program.

## Ovládání

| Klávesa | Akce |
|---|---|
| Tab | přepnout panel |
| Enter / Backspace | otevřít (složku, archiv) / o úroveň výš |
| F3 | prohlížeč (text, F4 přepne na hex) |
| F4 | editor (F2 uloží, Esc zavře) |
| F5 | kopírovat do druhého panelu (v archivu rozbalit) |
| Alt+F5 | zabalit vybrané do ZIPu |
| F6 | přesunout |
| Alt+F6 | spojit rozdělený soubor (.001, .002 ...) |
| F7 | nová složka |
| F8 / Delete | smazat (do koše; na flashce natrvalo, s upozorněním) |
| F9 | menu nahoře (šipky, Enter, Esc) |
| F10 | konec |
| Alt+F1 / Alt+F2 | změnit disk v levém / pravém panelu |
| Ctrl+F1 / Ctrl+F2 | přepnout levý / pravý panel mezi plným výpisem a více sloupci (Brief) |
| Ctrl+U / Ctrl+R | prohodit panely / znovu načíst |
| Ctrl+L | přepnout jazyk CS / EN (také menu Commands > Language) |
| P / Esc | během kopírování pozastavit / zrušit |

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

## Soubory nad 4 GB na FAT32

Při kopírování souboru nad 4 GB na FAT32 se aplikace zeptá, jestli ho rozdělit na části po 2 GB (`nazev.001`, `.002` ...). Zpět se spojí přes Files > Join parts (Alt+F6). Dělení i spojení jsou vlastním kódem, s volitelným ověřením hashem.

## Nastavení

Poslední složky obou panelů, režim Brief a přepínač ověření se ukládají do `settings.json` vedle exe. Cesty na stejném disku, odkud aplikace běží, se ukládají relativně, takže po změně písmene flashky platí dál. Na flashce jen pro čtení se nastavení prostě neuloží.

## Jazyk (CS / EN)

Aplikace mluví česky nebo anglicky. Při prvním spuštění se vybere podle jazyka Windows (česky jen na české lokalizaci, jinak anglicky), přepíná se přes Ctrl+L a volba se uloží do `settings.json`. Texty jsou v kódu česky a anglický překlad je ve slovníku `Loc.cs`; chybí-li překlad, použije se čeština. Názvy menu a F-kláves zůstávají anglicky jako v originálním Nortonu.
