[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$StarterDatabaseDirectory,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "artifacts")
)

$ErrorActionPreference = "Stop"
$solutionRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$starterRoot = (Resolve-Path $StarterDatabaseDirectory).Path
$starterBackup = Join-Path $starterRoot "HistoryCrawlerStarter.bak"
$seedExport = Join-Path $starterRoot "HistoryCrawlerStarterSeeds.json"
if (-not (Test-Path -LiteralPath $starterBackup -PathType Leaf)) {
    throw "HistoryCrawlerStarter.bak was not found. Use Settings > Prepare clean starter database first."
}
if (-not (Test-Path -LiteralPath $seedExport -PathType Leaf)) {
    throw "HistoryCrawlerStarterSeeds.json was not found. Use Settings > Prepare clean starter database first."
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not $resolvedOutput.StartsWith($solutionRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "For safety, the release output must be inside the History Crawler solution folder."
}
$stage = Join-Path $resolvedOutput "HistoryCrawler"
$workerPublish = Join-Path $resolvedOutput "worker-publish"
$operatorPublish = Join-Path $resolvedOutput "operator-publish"
foreach ($path in @($stage, $workerPublish, $operatorPublish)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    New-Item -ItemType Directory -Path $path | Out-Null
}

dotnet publish (Join-Path $solutionRoot "ButchCrawler.App\ButchCrawler.App.csproj") `
    --configuration Release --runtime win-x64 --self-contained true --output $workerPublish `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "The crawler publish step failed." }

dotnet publish (Join-Path $solutionRoot "ButchCrawler.Operator\ButchCrawler.Operator.csproj") `
    --configuration Release --runtime win-x64 --self-contained true --output $operatorPublish `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "The desktop application publish step failed." }

Copy-Item -Path (Join-Path $workerPublish "*") -Destination $stage -Recurse -Force
Copy-Item -Path (Join-Path $operatorPublish "*") -Destination $stage -Recurse -Force
$publishedOperator = Join-Path $stage "ButchCrawler.Operator.exe"
if (-not (Test-Path -LiteralPath $publishedOperator -PathType Leaf)) {
    throw "The published History Crawler desktop program was not found."
}
Move-Item -LiteralPath $publishedOperator -Destination (Join-Path $stage "HistoryCrawler.exe") -Force
$databaseFolder = Join-Path $stage "Database"
New-Item -ItemType Directory -Path $databaseFolder | Out-Null
Copy-Item -LiteralPath $starterBackup -Destination $databaseFolder
Copy-Item -LiteralPath $seedExport -Destination $databaseFolder

$documentationFolder = Join-Path $solutionRoot "Documentation"
$releaseDocumentation = Join-Path $stage "Documentation"
New-Item -ItemType Directory -Path $releaseDocumentation | Out-Null
foreach ($documentName in @("History_Crawler_User_Manual.docx", "History_Crawler_Research_Brochure.docx")) {
    $documentPath = Join-Path $documentationFolder $documentName
    if (-not (Test-Path -LiteralPath $documentPath -PathType Leaf)) {
        throw "Required release document was not found: $documentName"
    }
    Copy-Item -LiteralPath $documentPath -Destination $releaseDocumentation
}

$settingsPath = Join-Path $stage "appsettings.json"
$settingsText = Get-Content -LiteralPath $settingsPath -Raw
if ($settingsText -match '"(ApiKey|EncryptedApiKey)"\s*:\s*"[^\"]+"' -or $settingsText -match 'sk-[A-Za-z0-9_-]{12,}') {
    throw "A possible API key was found in the published settings. The package was not created."
}

$readme = @"
History Crawler

1. Install Microsoft SQL Server LocalDB before opening History Crawler.
2. Open HistoryCrawler.exe.
3. Follow the first-time setup guide. It installs the clean starter database but never starts a crawl.
4. Review Seed URLs and Seed Keywords under Settings.
5. Choose Start crawler only when you are ready.

AI is off by default. No OpenAI key is included in this package.
The bundled database contains Seed URLs, Seed Keywords, and schema-version records only.
"@
Set-Content -LiteralPath (Join-Path $stage "README FIRST.txt") -Value $readme -Encoding UTF8

$zipPath = Join-Path $resolvedOutput "HistoryCrawler-win-x64.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -LiteralPath $stage -DestinationPath $zipPath -CompressionLevel Optimal

Write-Output "Release package created: $zipPath"
Write-Output "Unpacked release folder: $stage"
