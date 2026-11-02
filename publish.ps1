# Produit un seul fichier autonome : publish\MaRogAlly.exe (aucune installation requise sur l'Ally).
# Usage : .\publish.ps1                      -> génère l'exe
#         .\publish.ps1 -Target "\\ROG-ALLY\MaRogAlly"   -> génère puis copie vers un dossier partagé de l'Ally
param([string]$Target)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "publish"

dotnet publish (Join-Path $root "src\MaRogAlly\MaRogAlly.csproj") -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw "La publication a échoué." }

$exe = Join-Path $out "MaRogAlly.exe"
Write-Host ("OK : {0} ({1:N1} Mo)" -f $exe, ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green

if ($Target) {
    # L'application en cours d'exécution verrouille l'exe : il faut la quitter (Réglages > Quitter) avant.
    New-Item -ItemType Directory -Force $Target | Out-Null
    Copy-Item $exe $Target -Force
    Write-Host "Copié vers $Target" -ForegroundColor Green
}
