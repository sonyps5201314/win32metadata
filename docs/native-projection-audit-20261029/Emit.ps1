$ErrorActionPreference = 'Stop'
$repo = 'L:\Open_Code\win32metadata'
$version = [Reflection.AssemblyName]::GetAssemblyName("$PSScriptRoot\before.winmd").Version.ToString()
$arguments = @(
    '--sourceDir', "$PSScriptRoot\input", '--arch', 'crossarch', '--version', $version,
    '--outputFileName', "$PSScriptRoot\after.winmd", '--validateGeneratedTypedefs',
    '--autoTypes', "$repo\generation\WinSDK\autoTypes.json",
    "@$repo\generation\WinSDK\emitter.settings.rsp",
    "@$repo\generation\WinSDK\requiredNamespacesForNames.rsp",
    "@$repo\generation\WinSDK\documentationMappings.rsp",
    "@$PSScriptRoot\input\emitter.functionPointerFixups.generated.rsp",
    "@$PSScriptRoot\input\enumsMakeFlags.generated.rsp", "@$PSScriptRoot\input\enumsRemap.rsp"
)
$arguments | ConvertTo-Json | Set-Content -LiteralPath "$PSScriptRoot\emit-arguments.json"
$watch = [Diagnostics.Stopwatch]::StartNew()
& "$repo\bin\Release\net10.0\ClangSharpSourceToWinmd.exe" @arguments 2>&1 | Tee-Object -FilePath "$PSScriptRoot\emit.log"
$result = @{ ExitCode = $LASTEXITCODE; Seconds = $watch.Elapsed.TotalSeconds }
$result | ConvertTo-Json | Set-Content -LiteralPath "$PSScriptRoot\emit-result.json"
if ($result.ExitCode -ne 0) { throw '生成失败' }
