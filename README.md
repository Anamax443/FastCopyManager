# FastCopyManager

Dvoupanelový správce souborů pro Windows (WPF, .NET 10) ve stylu Norton Commanderu, s vlastním kopírovacím jádrem. Žádný cizí program.

## Ovládání

| Klávesa | Akce |
|---|---|
| Tab | přepnout panel |
| Enter / Backspace | otevřít (složku, archiv) / o úroveň výš |
| F1 | nápověda (přehled kláves) |
| F3 | prohlížeč (text, F4 přepne na hex) |
| F4 | editor (F2 uloží, Esc zavře) |
| F5 | kopírovat do druhého panelu (v archivu rozbalit) |
| Alt+F5 | zabalit vybrané do ZIPu |
| Alt+F9 | rozbalit vybraný archiv (zip, rar, 7z, tar) do druhého panelu |
| F6 | přesunout |
| Alt+F6 | spojit rozdělený soubor (.001, .002 ...) |
| F7 | nová složka |
| F8 / Delete | smazat (do koše; na flashce natrvalo, s upozorněním) |
| F9 | menu nahoře (šipky, Enter, Esc) |
| F10 | konec |
| Alt+F1 / Alt+F2 | změnit disk nebo zadat síťovou cestu v levém / pravém panelu |
| Ctrl+G nebo klik na cestu | zadat cestu nad panelem, i síťovou (`\\server\sdileni`); Enter přejde, Esc zruší |
| Ctrl+F1 / Ctrl+F2 | přepnout levý / pravý panel mezi plným výpisem a více sloupci (Brief) |
| Ctrl+Q | náhled souboru (txt, md, log, kód ...) ve druhém panelu; Tab nebo šipka ho zavře |
| Ctrl+U / Ctrl+R | prohodit panely / znovu načíst |
| Ctrl+L | přepnout jazyk CS / EN (také menu Commands > Language) |
| P / Esc | během kopírování pozastavit / zrušit |

## Kopírovací jádro

`FileOps.cs`: bufferované proudové kopírování (1 MB), zápis do dočasného `.fcm.tmp` a přejmenování až po dokončení (přerušená kopie nenechá napůl zapsaný soubor), zachování data změny, kontrola limitu 4 GB na FAT32.

## Build a publikace

```powershell
cd src\FastCopyManager
dotnet build -c Release
.\tools\publish.ps1     # jeden soubor publish\FastCopyManager.exe (cca 64 MB), nic dalšího
```

- Výsledkem je **jediný soubor `FastCopyManager.exe`**: .NET, knihovny, písmo i 7za.exe jsou uvnitř. Na flashku stačí zkopírovat ten jeden soubor, na cílovém počítači se nic neinstaluje.
- Při spuštění se nativní části WPF (cca 8 MB) rozbalí do `%TEMP%\.net\FastCopyManager` a 7za.exe (až při prvním použití 7z) do `%TEMP%\FastCopyManager\7-Zip`. Jinde se na počítači nic nezapisuje. První start trvá o chvíli déle (kolem 1-3 s), další jsou rychlé.
- Vedle exe se nevytváří nic: nastavení se ukládá do profilu uživatele na daném počítači (`%LOCALAPPDATA%\FastCopyManager\settings.json`). Kdo chce nastavení nosit s sebou na flashce, vytvoří vedle exe prázdnou složku `Data`; pokud existuje, použije se ona (přenosný režim).
- Exe je nepodepsané a velké, takže ho některé antiviry nebo firemní zásady mohou zablokovat. Z FAT/exFAT flashky se obvykle spouští bez značky „staženo z internetu“, takže SmartScreen většinou mlčí. Volitelný self-signed podpis (`-Sign`) je důvěryhodný jen na stroji, kde vznikl.

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

Poslední složky obou panelů, režim Brief, řazení, jazyk a přepínač ověření se ukládají do `%LOCALAPPDATA%\FastCopyManager\settings.json`, nebo do `Data\settings.json` vedle exe, když tam složka `Data` existuje (přenosný režim, nastavení putuje s flashkou). Cesty na stejném disku, odkud aplikace běží, se ukládají relativně, takže po změně písmene flashky platí dál. Když se do cílové složky nedá zapisovat, nastavení se prostě neuloží.

## Jazyk (CS / EN)

Aplikace mluví česky nebo anglicky. Při prvním spuštění se vybere podle jazyka Windows (česky jen na české lokalizaci, jinak anglicky), přepíná se přes Ctrl+L a volba se uloží do `settings.json`. Texty jsou v kódu česky a anglický překlad je ve slovníku `Loc.cs`; chybí-li překlad, použije se čeština. Názvy menu a F-kláves zůstávají anglicky jako v originálním Nortonu.

Podržení **Alt** přepne dolní lištu na příkazy na Alt (Pack, Join, Unpack, změna disku).

## Balení a heslo

Alt+F5 otevře dialog: název, formát (ZIP, TAR.GZ nebo TAR), komprese (bez, rychlá, normální, nejlepší) a volitelné heslo. Heslo jde jen u ZIPu a používá šifrování AES-256 (Průzkumník Windows takové ZIPy neotevře, funguje 7-Zip a WinRAR). Při rozbalení zaheslovaného archivu se aplikace na heslo zeptá.

## Fronta

Když běží kopírování a dáš další F5, F6, Alt+F5 nebo Alt+F9, nová úloha se zařadí do fronty a poběží po dokončení předchozí. Počet čekajících úloh je vidět ve stavovém řádku. Esc zruší běžící úlohu, Shift+Esc zruší i frontu, P pozastaví. Za běhu se dá dál procházet panely; mazání a vytváření složek je po dobu operace zakázané.

### 7z a RAR při balení

ZIP (včetně hesla AES-256), TAR a TAR.GZ zapisuje aplikace sama. **7z** zapisuje **7za.exe** (7-Zip Extra), který je zabudovaný přímo v exe, takže funguje z flashky na jakémkoli počítači, bez instalace. **RAR** je uzavřený formát, který smí vytvářet jen WinRAR (`Rar.exe`, placený se zkušební dobou); ten aplikace neobsahuje.

- 7z: použije se `Tools\7za.exe` vedle exe (když existuje), jinak zabudovaný 7za.exe, nakonec nainstalovaný 7-Zip.
- RAR: jen když je k dispozici `Rar.exe` (složka `Tools` vedle exe, WinRAR v Program Files nebo v PATH). Jinak dialog napíše, co chybí. Na cizím počítači bez WinRARu RAR vytvořit nelze; použij 7z nebo ZIP.
- Rozbalování a prohlížení ZIP, RAR, 7z, TAR a TAR.GZ funguje vždy, bez externího nástroje.
- U 7z a RAR se heslo předává nástroji na příkazové řádce, takže je po dobu balení viditelné ostatním procesům téhož uživatele.

### Použitý software třetích stran

- **7-Zip Extra 26.05** (`7za.exe`), Copyright (C) 1999-2026 Igor Pavlov, licence GNU LGPL, zdrojový kód a informace: https://www.7-zip.org/. Binárka je nezměněná a zabudovaná v exe; licenční text je v `third_party/7-Zip/License.txt` a při rozbalení se ukládá vedle 7za.exe jako `7-Zip-License.txt`. Původ a otisk balíčku: `third_party/7-Zip/SOURCE.txt`.
- Písmo MxPlus IBM VGA 8x16 (CC BY-SA 4.0), knihovny SharpCompress (MIT) a SharpZipLib (MIT).
