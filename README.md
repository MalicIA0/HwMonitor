# HW Monitor

Mini-moniteur matériel pour Windows, toujours au premier plan : **CPU / GPU** (température + charge), **RAM**, **résolution** de rendu et **FPS** de l'application au premier plan.

## Télécharger

Va dans **[Releases](../../releases/latest)**, télécharge `HwMonitor-X.Y.Z-win-x64.zip`, décompresse-le dans un dossier et lance `HwMonitor.exe`.
`PresentMon.exe` doit rester à côté de `HwMonitor.exe` (mesure des FPS).

- L'appli demande les droits **administrateur** (lecture des capteurs) : c'est normal.
- Pour la température CPU, installe le pilote **[PawnIO](https://pawnio.eu)**.

Détails d'utilisation : [LISEZ-MOI.txt](LISEZ-MOI.txt).

## Compiler depuis les sources

Prérequis : [.NET 9 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet publish HwMonitor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o bin\publish
```

Ou `build.ps1` (build de test) / `deploy.ps1` (build + déploiement local).

## Crédits

- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0)
- [Intel PresentMon](https://github.com/GameTechDev/PresentMon) (MIT) — `PresentMon.exe` redistribué tel quel

## Licence

[MIT](LICENSE)
