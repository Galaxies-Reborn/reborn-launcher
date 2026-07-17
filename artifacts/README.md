# Release Artifacts

Only torrent metadata and its release manifest are committed under
`artifacts/torrents`. Client payloads, published application files, and compiled
installers remain outside Git and are ignored by `.gitignore`.

Create the sanitized client payload and torrent with:

```powershell
.\scripts\New-ClientBundle.ps1
```

Build the self-contained launcher and Inno Setup installer with:

```powershell
.\scripts\Build-Release.ps1
```
