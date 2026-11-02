# Publie une nouvelle version sur GitHub : les applications installées la proposeront ensuite dans leur menu.
# Usage : augmenter <Version> dans src\MaRogAlly\MaRogAlly.csproj, valider les changements (commit et push), puis .\release.ps1
param([string]$Notes = "")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\MaRogAlly\MaRogAlly.csproj"
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "Version introuvable dans le projet." }
$tag = "v$version"

if (git -C $root status --porcelain) { throw "Des changements ne sont pas validés : commit et push d'abord." }
if (gh release view $tag 2>$null) { throw "La version $tag existe déjà : augmente <Version> dans le projet." }

# Dossier à part : pas besoin de fermer l'application en cours d'utilisation.
$out = Join-Path ([IO.Path]::GetTempPath()) "MaRogAlly-release-$version"
dotnet publish $project -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw "La publication a échoué." }

$exe = Join-Path $out "MaRogAlly.exe"
gh release create $tag $exe --title "Ma ROG Ally $version" --notes $(if ($Notes) { $Notes } else { "Version $version" })
if ($LASTEXITCODE -ne 0) { throw "La création de la version a échoué." }
Write-Host "Version $tag publiée." -ForegroundColor Green
