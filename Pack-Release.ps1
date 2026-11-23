[CmdletBinding()]
param([string] $DotNet = 'dotnet')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Keep the repository and binary release inventories explicit.
$sourceFiles = @(
    '.gitignore', 'README.md', 'THIRD-PARTY-NOTICES.txt', 'Pack-Release.ps1',
    'docs/media/banner.png', 'docs/media/translation-panel.png',
    'docs/media/settings.png', 'docs/media/language-picker.png',
    'RTF.csproj', 'app.manifest',
    'Crystal.ico', 'Crystal.png', 'StandaloneCrystal.png', 'MineralGrain.png', 'TrayCrystal.ico',
    'App.xaml', 'App.xaml.cs', 'AiTranslationClient.cs', 'DesktopInput.cs',
    'MarkdownDocumentRenderer.cs', 'MarkdownService.cs', 'MarkdownTextView.cs', 'MarkdownTheme.cs',
    'Native.cs', 'OverlayPreferences.cs', 'OverlayStoryboard.cs', 'OverlayWindow.cs',
    'PanelTextScrollViewer.cs', 'ProseLayout.cs', 'QuietMineral.xaml',
    'SearchPicker.cs', 'SelectionIcon.xaml', 'SelectionIcon.xaml.cs', 'SelectionMonitor.cs',
    'SettingsTabControl.cs', 'SettingsWindow.xaml', 'SettingsWindow.xaml.cs',
    'StartupErrorWindow.xaml', 'StartupErrorWindow.xaml.cs',
    'TranslationLanguages.cs', 'TranslationPanel.Editor.cs', 'TranslationPanel.xaml',
    'TranslationPanel.xaml.cs', 'TranslationService.cs', 'TranslationSettings.cs', 'TrayExitMenu.cs'
)
$runtimeFiles = @('RTF.exe', 'RTF.dll', 'RTF.deps.json', 'RTF.runtimeconfig.json', 'Markdig.dll')

function Assert-NoSensitiveStrings([string] $Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $patterns = @(
        '(?i)[A-Z]:[\\/](?:Users|Documents and Settings)[\\/]|/(?:home|Users)/[^/\s]+/',
        '(?i)-----BEGIN (?:[A-Z]+ )*PRIVATE KEY-----',
        '(?i)\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AIza[A-Za-z0-9_-]{35}|AKIA[A-Z0-9]{16})\b',
        '(?i)https?://[^/\s:@]+:[^/\s@]+@'
    )
    # Include both alignments so compiled UTF-16 strings are checked as well.
    $representations = @([Text.Encoding]::UTF8.GetString($bytes))
    if ($bytes.Length -gt 2) {
        $representations += [Text.Encoding]::Unicode.GetString($bytes)
        $representations += [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)
    }
    foreach ($content in $representations) {
        if ([IO.Path]::GetFileName($Path) -eq 'Markdig.dll') {
            # The published dependency embeds this public CI path in its debug directory.
            $ciPath = '/home/' + 'runner/work/markdig/markdig/src/Markdig/obj/Release/net10.0/Markdig.pdb'
            $content = $content.Replace($ciPath, '')
        }
        foreach ($pattern in $patterns) {
            if ($content -match $pattern) {
                throw "A possible credential or private path was found in $([IO.Path]::GetFileName($Path))."
            }
        }
    }
}

function Copy-CheckedFile([string] $From, [string] $To, [string] $Name) {
    $parent = Split-Path -Parent $Name
    if ($parent) {
        $directory = $From
        foreach ($segment in ($parent -split '[/\\]')) {
            $directory = Join-Path $directory $segment
            if ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Expected an ordinary directory: $Name"
            }
        }
    }
    $item = Get-Item -LiteralPath (Join-Path $From $Name)
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Expected an ordinary file: $Name"
    }
    Assert-NoSensitiveStrings $item.FullName
    if ($parent) {
        [IO.Directory]::CreateDirectory((Join-Path $To $parent)) | Out-Null
    }
    Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $To $Name)
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$dotnetPath = (Get-Command $DotNet -CommandType Application).Source
$bin = [IO.Directory]::CreateDirectory((Join-Path $PSScriptRoot 'bin')).FullName
$work = Join-Path $bin ('pack-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $work 'source'
$publish = Join-Path $work 'publish'
$runtime = Join-Path $work 'runtime'

try {
    [IO.Directory]::CreateDirectory($source) | Out-Null
    [IO.Directory]::CreateDirectory($runtime) | Out-Null
    foreach ($name in $sourceFiles) {
        Copy-CheckedFile $PSScriptRoot $source $name
    }
    # Zip before invoking MSBuild, so restore and build outputs cannot enter the source archive.
    $sourceZip = Join-Path $work 'RTF-source.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($source, $sourceZip)

    $publishArgs = @(
        'publish', (Join-Path $source 'RTF.csproj'), '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'false', '-o', $publish, '-p:DebugType=None',
        '-p:DebugSymbols=false', "-p:PathMap=$source=/_/rtf"
    )
    & $dotnetPath @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "Release publish failed with exit code $LASTEXITCODE." }

    foreach ($name in $runtimeFiles) {
        Copy-CheckedFile $publish $runtime $name
    }
    Copy-CheckedFile $source $runtime 'THIRD-PARTY-NOTICES.txt'
    $binaryZip = Join-Path $work 'RTF-Windows-x64.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($runtime, $binaryZip)

    $destination = [IO.Directory]::CreateDirectory((Join-Path $PSScriptRoot 'release')).FullName
    foreach ($archive in @($sourceZip, $binaryZip)) {
        Copy-Item -LiteralPath $archive -Destination (Join-Path $destination ([IO.Path]::GetFileName($archive))) -Force
        Write-Output (Join-Path $destination ([IO.Path]::GetFileName($archive)))
    }
}
finally {
    if (Test-Path -LiteralPath $work) {
        $resolved = (Resolve-Path -LiteralPath $work).ProviderPath
        if ([IO.Path]::GetDirectoryName($resolved) -eq $bin -and [IO.Path]::GetFileName($resolved) -match '^pack-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
