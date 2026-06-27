<#
.SYNOPSIS
    Builds, signs, and registers the Dataverse File Manager sparse package, granting package
    identity to the unpackaged Explorer host exe (a prerequisite for cfapi / sync-root registration).

.DESCRIPTION
    Steps performed:
      1. Ensure a self-signed dev code-signing cert (CN=DataverseFileManager.Dev) exists; create it
         in CurrentUser\My if missing, and (elevated) trust it in LocalMachine\TrustedPeople.
      2. dotnet publish the host exe (win-x64) -> external location.
      3. Stage the sparse layout (AppxManifest.xml + Assets logos) and pack it with MakeAppx.
      4. Sign the .msix with SignTool using the dev cert.
      5. Register it for the current user with Add-AppxPackage -ExternalLocation <publish dir>.

    Trusting the cert (step 1b) and is the only part that needs an elevated shell. Re-run elevated
    once; subsequent runs are fine unelevated.

.PARAMETER Unregister
    Remove the package instead of installing it.
#>
[CmdletBinding()]
param(
    # Tear down only: unregister the sync root, then remove the package.
    [switch]$Unregister,
    # Full reset: tear everything down, then rebuild + reinstall from scratch.
    [switch]$Full
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$PackageName  = 'DataverseFileManager'
$Publisher    = 'CN=DataverseFileManager.Dev'
$here         = $PSScriptRoot
$projectDir   = Split-Path $here -Parent
$projectFile  = Join-Path $projectDir 'DataverseFileManager.Explorer.csproj'
$publishDir   = Join-Path $projectDir 'bin\publish'
$stageDir     = Join-Path $here 'stage'
$msixPath     = Join-Path $here 'DataverseFileManager.sparse.msix'
$exePath      = Join-Path $publishDir 'DataverseFileManager.Explorer.exe'

# Tear down in the safe order: drop the sync-root node (owned by the package identity) BEFORE
# removing the package, so no orphan entries linger under SyncRootManager.
function Remove-Everything {
    if (Test-Path $exePath) {
        & $exePath unregister 2>$null   # StorageProviderSyncRootManager.Unregister
    }
    Get-AppxPackage -Name $PackageName | Remove-AppxPackage -ErrorAction SilentlyContinue
    Write-Host "Tore down sync root + package '$PackageName' (if present)." -ForegroundColor Green
}

if ($Unregister) {
    Remove-Everything
    return
}

if ($Full) {
    Remove-Everything   # then fall through to a clean rebuild + reinstall
}

# --- Locate Windows SDK tools (MakeAppx, SignTool) ---
function Find-SdkTool([string]$name) {
    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin"
    )
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $hit = Get-ChildItem -Path $root -Recurse -Filter $name -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -match '\\x64\\' } |
               Sort-Object FullName -Descending | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    throw "Could not find $name in the Windows SDK. Install the Windows 10/11 SDK."
}
$makeAppx = Find-SdkTool 'makeappx.exe'
$signTool = Find-SdkTool 'signtool.exe'
Write-Host "MakeAppx: $makeAppx"
Write-Host "SignTool: $signTool"

# --- 1. Dev signing cert ---
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher } | Select-Object -First 1
if (-not $cert) {
    Write-Host "Creating self-signed dev cert $Publisher ..." -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher `
        -KeyUsage DigitalSignature -FriendlyName 'Dataverse File Manager Dev' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}
Write-Host "Cert thumbprint: $($cert.Thumbprint)"

# Trust the cert (LocalMachine) so Add-AppxPackage accepts the signature. Needs elevation.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
            ).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
$trusted = Test-Path "Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint)"
if (-not $trusted) {
    if (-not $isAdmin) {
        throw "The dev cert is not trusted yet. Re-run this script ONCE in an ELEVATED PowerShell to import it into LocalMachine\TrustedPeople."
    }
    $tmpCer = Join-Path $env:TEMP 'dvfm-dev.cer'
    Export-Certificate -Cert $cert -FilePath $tmpCer | Out-Null
    Import-Certificate -FilePath $tmpCer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    Remove-Item $tmpCer -Force
    Write-Host "Imported dev cert into LocalMachine\TrustedPeople." -ForegroundColor Green
}

# --- 2. Publish the host exe to the external location ---
Write-Host "Publishing host exe -> $publishDir ..." -ForegroundColor Yellow
dotnet publish $projectFile -c Release -r win-x64 --self-contained false -o $publishDir | Out-Null

# --- 3. Stage + pack the sparse package ---
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $stageDir 'Assets') -Force | Out-Null
Copy-Item (Join-Path $here 'AppxManifest.xml') $stageDir
Copy-Item (Join-Path $projectDir 'Assets\StoreLogo.png')        (Join-Path $stageDir 'Assets')
Copy-Item (Join-Path $projectDir 'Assets\Square150x150Logo.png') (Join-Path $stageDir 'Assets')
Copy-Item (Join-Path $projectDir 'Assets\Square44x44Logo.png')   (Join-Path $stageDir 'Assets')

if (Test-Path $msixPath) { Remove-Item $msixPath -Force }
& $makeAppx pack /d $stageDir /p $msixPath /nv /o
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed ($LASTEXITCODE)." }

# --- 4. Sign ---
& $signTool sign /fd SHA256 /a /sha1 $cert.Thumbprint $msixPath
if ($LASTEXITCODE -ne 0) { throw "SignTool failed ($LASTEXITCODE)." }

# --- 5. Register (sparse: package payload + external exe location) ---
Add-AppxPackage -Path $msixPath -ExternalLocation $publishDir
Write-Host ""
Write-Host "Sparse package registered. The exe at:" -ForegroundColor Green
Write-Host "    $publishDir\DataverseFileManager.Explorer.exe"
Write-Host "now runs with package identity. Launch it (or via the Start menu entry) to register the sync root."
