param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'Assets\history-crawler-main-screen.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$operatorDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'ButchCrawler.Operator\bin\Debug\net8.0-windows'
$operatorAssembly = Join-Path $operatorDirectory 'ButchCrawler.Operator.dll'
if (-not (Test-Path -LiteralPath $operatorAssembly)) {
    throw "Build the operator project before rendering the brochure screen: $operatorAssembly"
}

$previousDirectory = [Environment]::CurrentDirectory
[Environment]::CurrentDirectory = $operatorDirectory
try {
    [System.Reflection.Assembly]::LoadFrom($operatorAssembly) | Out-Null
    $windowType = [Type]::GetType('ButchCrawler.Operator.MainWindow, ButchCrawler.Operator', $true)
    $window = [Activator]::CreateInstance($windowType)
    $flags = [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Instance

    function Set-MainText([string]$Name, [string]$Value) {
        $control = $windowType.GetField($Name, $flags).GetValue($window)
        $control.Text = $Value
    }

    Set-MainText 'UrlCountText' '3,446'
    Set-MainText 'CompletedCountText' '3,345'
    Set-MainText 'RetrievedPagesText' '1,474'
    Set-MainText 'FailedCountText' '1,970'
    Set-MainText 'LinkCountText' '64,336'
    Set-MainText 'MatchCountText' '498'
    Set-MainText 'RetryCountText' '107'
    Set-MainText 'ErrorCountText' '32'
    Set-MainText 'SeedProgressText' '2,226 of 78,057 Processed'
    Set-MainText 'SeedSuccessfulText' '1,474 Successful'
    Set-MainText 'SeedAttentionText' '752 Needs Attention'
    Set-MainText 'DiscoveredUrlsText' 'Discovered URLs 64,336'
    Set-MainText 'LocalFileProgressText' 'Local files 1,382 of 2,040'
    Set-MainText 'ProcessedTypesText' 'HTML 1,474  •  PDF 286  •  JPEG 93  •  MDB 12  •  CSV 48  •  JSON 31'
    Set-MainText 'CurrentUrlText' 'Current URL: https://archive.example.org/records'

    $setRunning = $windowType.GetMethod('SetRunning', $flags)
    $setRunning.Invoke($window, @($true)) | Out-Null

    $content = [System.Windows.FrameworkElement]$window.Content
    $width = [int]($window.Width - 32)
    $height = [int]($window.Height - 50)
    $content.Measure([System.Windows.Size]::new($width, $height))
    $content.Arrange([System.Windows.Rect]::new(0, 0, $width, $height))
    $content.UpdateLayout()

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $width, $height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($content)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))

    $directory = Split-Path $OutputPath -Parent
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    $stream = [System.IO.File]::Create($OutputPath)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
    Write-Output $OutputPath
}
finally {
    [Environment]::CurrentDirectory = $previousDirectory
}
