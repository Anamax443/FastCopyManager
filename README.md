# FastCopyManager

Windows GUI (WPF, .NET 10) nad [FastCopy](https://fastcopy.jp/) CLI. Vybereš zdroj, cíl a režim (Diff / Sync / Move / NoExistDiff), volitelně ověření, a aplikace spustí `FastCopy.exe` a hlídá jeho průběh.

## Build

```powershell
cd src\FastCopyManager
dotnet build -c Release
```

## Publikace a SmartScreen

Aby Windows nehlásil „nebezpečná aplikace“:

- publikuje se **framework-dependent** (bez single-file self-extract, bez UPX/packerů, ty spouští antiviry),
- exe se podepíše; pro vlastní stroje stačí self-signed certifikát,
- soubor vytvořený lokálně nemá Mark-of-the-Web, takže SmartScreen se spouští hlavně u stažených souborů.

```powershell
.\tools\publish.ps1            # publish + podpis self-signed certifikátem
```

Pro distribuci cizím lidem je jediná trvalá cesta důvěryhodný (placený) podpisový certifikát; ten tu záměrně nepoužíváme.
