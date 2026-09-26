# sqlcli sürüm kontrolü — kurulu araç, kaynaktaki son KOD commit'inden mi derlendi?
# v2.3+ kurulumlarda aynı iş: `sqlcli surum --kaynak <klasör> [--uzak]`. Bu betik, `surum`
# komutu OLMAYAN eski kurulumları (<= 2.2) da ölçebilmek için duruyor.
# Kurulu .exe, .NET SDK'nın eklediği commit kimliğini sürüm bilgisinde taşır: 2.2.0+<sha>.
# Yalnız doküman değiştiren commit'ler "geride" sayılmaz (kod aynıysa araç güncel).
# Çıkış: 0 = güncel · 1 = geride (yeniden kur) · 2 = kontrol edilemedi
param(
    [string]$Kaynak = (Split-Path -Parent $PSScriptRoot),   # varsayılan: bu betiğin bulunduğu klon
    [switch]$Uzak      # kaynağı önce uzaktan getir (fetch) ve uzağın ilerisinde/gerisinde mi söyle
)
$ErrorActionPreference = "Stop"
try {
    $exe = (Get-Command sqlcli -ErrorAction Stop).Source
    $surum = (Get-Item $exe).VersionInfo.ProductVersion          # ör. 2.2.0+64cf4b50...
    if ($surum -notmatch '\+([0-9a-f]{7,40})') { Write-Output "KONTROL EDİLEMEDİ: sürüm bilgisinde commit yok ($surum)"; exit 2 }
    $kurulu = $Matches[1]
    $git = @("-c", "safe.directory=*", "-C", $Kaynak)

    if ($Uzak) {
        & git @git fetch -q
        $ab = (& git @git rev-list --left-right --count "HEAD...@{u}") -split '\s+'
        if ($ab[1] -ne "0") { Write-Output "UYARI: kaynak kopya uzaktan $($ab[1]) commit geride (git pull gerekli)" }
    }

    $sonKod = & git @git log -1 --format=%H -- '*.cs' '*.csproj' '*.props' '*.json' ':!**/sqlcli.json'
    & git @git cat-file -e "$kurulu^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { Write-Output "KONTROL EDİLEMEDİ: kurulu commit $kurulu kaynakta yok"; exit 2 }

    $geride = & git @git log --oneline "$kurulu..HEAD" -- '*.cs' '*.csproj' '*.props' '*.json' ':!**/sqlcli.json'
    if ($geride) {
        Write-Output "GERİDE: kurulu $($kurulu.Substring(0,7)), kaynakta $(@($geride).Count) kod commit'i daha var:"
        $geride | ForEach-Object { Write-Output "  $_" }
        Write-Output "Yeniden kur: dotnet pack `"$Kaynak`" -o `"$Kaynak\nupkg`"; dotnet tool update -g SqlCli --add-source `"$Kaynak\nupkg`""
        exit 1
    }
    Write-Output "GÜNCEL: sqlcli $surum — son kod commit'i $($sonKod.Substring(0,7)) kurulu sürüme dahil ($exe)"
    exit 0
}
catch { Write-Output "KONTROL EDİLEMEDİ: $($_.Exception.Message)"; exit 2 }
