#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$ChooseBackend,
    [switch]$SetupOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-ApiProject([string]$ProjectFile) {
    try {
        if (-not (Test-Path -LiteralPath $ProjectFile -PathType Leaf)) {
            return $false
        }
        $item = Get-Item -LiteralPath $ProjectFile
        if ($item.Name -ne 'GeoCapture.Api.csproj') { return $false }
        $program = Join-Path $item.DirectoryName 'Program.cs'
        return ((Test-Path -LiteralPath $program -PathType Leaf) -and
            ([IO.File]::ReadAllText($item.FullName) -match 'Microsoft\.NET\.Sdk\.Web'))
    }
    catch { return $false }
}

function Find-ApiInFolder([string]$Folder) {
    foreach ($suffix in @('GeoCapture.Api.csproj', 'GeoCapture.Api\GeoCapture.Api.csproj')) {
        $candidate = Join-Path $Folder $suffix
        if (Test-ApiProject $candidate) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    return $null
}

function Select-ApiProject([string]$InitialFolder) {
    Add-Type -AssemblyName System.Windows.Forms
    $picker = New-Object System.Windows.Forms.FolderBrowserDialog
    $picker.Description = 'Valaszd ki a backend mappajat (GeoCapture.Api vagy a backend megoldas mappaja).'
    $picker.ShowNewFolderButton = $false
    if (Test-Path -LiteralPath $InitialFolder -PathType Container) {
        $picker.SelectedPath = $InitialFolder
    }
    try {
        while ($picker.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
            $selected = Find-ApiInFolder $picker.SelectedPath
            if ($null -ne $selected) { return $selected }
            [void][System.Windows.Forms.MessageBox]::Show(
                'Ebben a mappaban nincs GeoCapture.Api.csproj es Program.cs. Valaszd ki a sajat backend projekted mappajat.',
                'GeoCapture - backend kivalasztasa'
            )
        }
        throw 'A backend kivalasztasa megszakitva.'
    }
    finally { $picker.Dispose() }
}

function Get-RelativeProjectPath([string]$FlutterRoot, [string]$ProjectFile) {
    $fromRoot = [IO.Path]::GetPathRoot($FlutterRoot)
    $toRoot = [IO.Path]::GetPathRoot($ProjectFile)
    if (-not [string]::Equals($fromRoot, $toRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return $ProjectFile
    }
    $directoryUri = [Uri]($FlutterRoot.TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar)
    $projectUri = [Uri]$ProjectFile
    $relativeUri = $directoryUri.MakeRelativeUri($projectUri)
    return [Uri]::UnescapeDataString($relativeUri.OriginalString).Replace('/', '\')
}

function Get-SavedApiProject([string]$FlutterRoot, [string]$SettingsFile) {
    try {
        if (Test-Path -LiteralPath $SettingsFile -PathType Leaf) {
            $settings = Get-Content -LiteralPath $SettingsFile -Raw | ConvertFrom-Json
            $candidate = [string]$settings.apiProject
            if (-not [IO.Path]::IsPathRooted($candidate)) {
                $candidate = Join-Path $FlutterRoot $candidate
            }
            if (Test-ApiProject $candidate) {
                return [IO.Path]::GetFullPath($candidate)
            }
        }
    }
    catch { Write-Host 'A korabbi backend utvonal mar nem hasznalhato. Ujra megkeresem.' -ForegroundColor Yellow }
    return $null
}

function Find-NearbyApiProject([string]$FlutterRoot) {
    $parent = Split-Path -Parent $FlutterRoot
    $locations = @(
        (Join-Path $parent 'geo_capture_backend'),
        (Join-Path $parent 'GeoCapture.Backend'),
        (Join-Path $parent 'GeoCapture.Api'),
        (Join-Path $FlutterRoot 'backend')
    )
    $found = @(
        foreach ($location in $locations) {
            $project = Find-ApiInFolder $location
            if ($null -ne $project) { $project }
        }
    ) | Sort-Object -Unique
    $found = @($found)
    if ($found.Count -eq 1) { return $found[0] }
    return $null
}

function Confirm-WebRootWritable([string]$ProjectFile) {
    if (-not (Test-ApiProject $ProjectFile)) { throw 'Ervenytelen backend projekt.' }
    $webRoot = Join-Path (Split-Path -Parent $ProjectFile) 'wwwroot'
    [void][IO.Directory]::CreateDirectory($webRoot)
    $probe = Join-Path $webRoot ('.geocapture-write-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $stream = $null
    try {
        $stream = [IO.File]::Open($probe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
        if (Test-Path -LiteralPath $probe -PathType Leaf) {
            Remove-Item -LiteralPath $probe -Force
        }
    }
    return $webRoot
}

function Test-LocalApiPort {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync('localhost', 7043)
        return ($task.Wait(500) -and $client.Connected)
    }
    catch { return $false }
    finally { $client.Dispose() }
}

try {
    $flutterRoot = [IO.Path]::GetFullPath($PSScriptRoot)
    if (-not (Test-Path -LiteralPath (Join-Path $flutterRoot 'pubspec.yaml') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $flutterRoot 'lib\main.dart') -PathType Leaf)) {
        throw 'A fajlokat a Flutter projekt gyokerebe masold: oda, ahol a pubspec.yaml es a lib mappa van.'
    }

    $settingsFile = Join-Path $flutterRoot '.dart_tool\geocapture\launcher.json'
    $apiProject = $null
    if (-not $ChooseBackend) {
        $apiProject = Get-SavedApiProject $flutterRoot $settingsFile
        if ($null -eq $apiProject) { $apiProject = Find-NearbyApiProject $flutterRoot }
    }
    if ($null -eq $apiProject) {
        $apiProject = Select-ApiProject (Split-Path -Parent $flutterRoot)
    }

    while ($true) {
        try {
            $webRoot = Confirm-WebRootWritable $apiProject
            break
        }
        catch {
            Write-Host 'A kivalasztott backend wwwroot mappaja nem irhato:' -ForegroundColor Red
            Write-Host (Join-Path (Split-Path -Parent $apiProject) 'wwwroot')
            Write-Host $_.Exception.Message -ForegroundColor Yellow
            Write-Host 'Valassz masik, irhato backend projektmappat. Ha a helyes projekt is vedett, masold at egy sajat, irhato fejlesztoi mappaba.'
            $apiProject = Select-ApiProject (Split-Path -Parent $flutterRoot)
        }
    }

    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $settingsFile))
    @{ apiProject = (Get-RelativeProjectPath $flutterRoot $apiProject) } |
        ConvertTo-Json | Set-Content -LiteralPath $settingsFile -Encoding UTF8
    Write-Host ('Flutter: ' + $flutterRoot)
    Write-Host ('Backend: ' + $apiProject)
    if ($SetupOnly) {
        Write-Host 'A backend mappajat megjegyeztem.' -ForegroundColor Green
        exit 0
    }

    $flutterCommand = Get-Command flutter -ErrorAction SilentlyContinue
    if ($null -eq $flutterCommand) {
        throw 'A flutter parancs nem talalhato. A Flutter bin mappajanak a Windows PATH-ban kell szerepelnie. Ha most allitottad be, inditsd ujra az alkalmazast, amelybol ezt futtatod.'
    }
    $dotnetCommand = Get-Command dotnet -ErrorAction Stop
    $copyCommand = Get-Command robocopy.exe -ErrorAction Stop

    Push-Location -LiteralPath $flutterRoot
    try {
        Write-Host 'Flutter web forditasa...' -ForegroundColor Cyan
        & $flutterCommand.Source build web
        if ($LASTEXITCODE -ne 0) { throw 'A Flutter forditasa nem sikerult. Masolas nem tortent.' }

        $buildRoot = Join-Path $flutterRoot 'build\web'
        if (-not (Test-Path -LiteralPath (Join-Path $buildRoot 'index.html') -PathType Leaf)) {
            throw 'A webes build index.html fajlja hianyzik. Masolas nem tortent.'
        }

        Write-Host 'Webes fajlok masolasa...' -ForegroundColor Cyan
        & $copyCommand.Source $buildRoot $webRoot /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
        if ($LASTEXITCODE -ge 8) {
            throw 'A masolas nem sikerult. Ellenorizd a fenti fajlhibakat es a wwwroot irasi jogosultsagat.'
        }
    }
    finally { Pop-Location }

    $url = 'https://localhost:7043'
    if (Test-LocalApiPort) {
        Write-Host 'A 7043-as port mar foglalt. Uj backend peldanyt nem inditok.' -ForegroundColor Yellow
        Write-Host 'Ha a GeoCapture fut rajta, a frissitett webes fajlokat ugyanonnan eri el. Frissits a bongeszoben Ctrl+F5-tel.'
    }
    else {
        $apiDirectory = Split-Path -Parent $apiProject
        $escapedDirectory = $apiDirectory.Replace("'", "''")
        $escapedProject = $apiProject.Replace("'", "''")
        $escapedDotnet = $dotnetCommand.Source.Replace("'", "''")
        $serverCommand = "Set-Location -LiteralPath '$escapedDirectory'; & '$escapedDotnet' run --project '$escapedProject' --no-launch-profile --urls '$url'"
        $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($serverCommand))
        Write-Host 'Backend inditasa kulon ablakban...' -ForegroundColor Cyan
        $server = Start-Process powershell.exe -ArgumentList @('-NoLogo', '-NoProfile', '-NoExit', '-EncodedCommand', $encodedCommand) -PassThru
        $ready = $false
        $deadline = (Get-Date).AddSeconds(45)
        while ((Get-Date) -lt $deadline -and -not $server.HasExited) {
            if (Test-LocalApiPort) { $ready = $true; break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) {
            throw 'A backend 45 masodpercen belul nem kezdett figyelni a 7043-as porton. A hibat a megnyitott backend ablakban latod.'
        }
    }

    Start-Process $url
    Write-Host 'Kesz. A bongeszo megnyilt. Backend leallitasa: Ctrl+C a backend ablakaban.' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ''
    Write-Host ('HIBA: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
