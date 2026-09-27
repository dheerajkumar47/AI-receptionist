# Converts this project's user-secrets into the JSON that Azure App Service accepts in
# Environment variables -> App settings -> Advanced edit. Usage (from the repo folder):
#   powershell -ExecutionPolicy Bypass -File scripts\export-appsettings.ps1 -AppUrl https://<app>.azurewebsites.net
# The output contains your secrets: paste it into Azure only, never share it.
param([Parameter(Mandatory = $true)][string]$AppUrl)

$skip = @('App:PublicBaseUrl', 'ConnectionStrings:Receptionist', 'Media:Directory', 'Microsoft365:TokenCachePath')
$lines = dotnet user-secrets list --project src/AiReceptionist.Web
if ($LASTEXITCODE -ne 0) {
    Write-Error "Could not read user-secrets (see the message above; usually the .NET 10 SDK is not installed). Nothing was copied."
    exit 1
}
$settings = [ordered]@{}
foreach ($line in $lines) {
    $i = $line.IndexOf(' = ')
    if ($i -lt 1) { continue }
    $key = $line.Substring(0, $i).Trim()
    if ($skip -contains $key) { continue }
    $settings[$key.Replace(':', '__')] = $line.Substring($i + 3)
}
$settings['App__PublicBaseUrl'] = $AppUrl.TrimEnd('/')
$settings['ASPNETCORE_ENVIRONMENT'] = 'Production'

$items = @($settings.GetEnumerator() | ForEach-Object { [ordered]@{ name = $_.Key; value = $_.Value; slotSetting = $false } })
$json = ConvertTo-Json -InputObject $items -Depth 3
$json | Set-Clipboard
Write-Host "$($settings.Count) settings copied to the clipboard. Paste them in Azure: Environment variables -> Advanced edit."
if (-not $settings.Contains('Admin__Password')) { Write-Warning 'Admin__Password is not set: add a strong password before going live.' }
