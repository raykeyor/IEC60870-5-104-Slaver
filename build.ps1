param(
    [switch]$BuildCpp,
    [switch]$BuildCSharp,
    [switch]$Run,
    [switch]$All
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if ($All) { $BuildCpp = $true; $BuildCSharp = $true; $Run = $true }

# ─── Build C++ DLL ──────────────────────────────────────

if ($BuildCpp) {
    Write-Host "`n=== Building C++ iec104_core.dll ===" -ForegroundColor Cyan
    
    $nativeDir = Join-Path $root "native"
    $buildDir = Join-Path $nativeDir "build"
    
    if (-not (Test-Path $buildDir)) {
        New-Item -ItemType Directory -Path $buildDir | Out-Null
    }
    
    Push-Location $buildDir
    try {
        cmake .. -G "Visual Studio 17 2022" -A x64
        if ($LASTEXITCODE -ne 0) { throw "CMake configure failed" }
        
        cmake --build . --config Release
        if ($LASTEXITCODE -ne 0) { throw "CMake build failed" }
        
        # Copy DLL to C# project output
        $dllSrc = Join-Path $buildDir "Release\iec104_core.dll"
        if (Test-Path $dllSrc) {
            $csharpDir = Join-Path $root "IEC104Simulator"
            Copy-Item $dllSrc $csharpDir -Force
            Write-Host "  DLL copied to IEC104Simulator/" -ForegroundColor Green
        }
    } finally {
        Pop-Location
    }
    
    Write-Host "  C++ build complete!" -ForegroundColor Green
}

# ─── Build C# ──────────────────────────────────────────

if ($BuildCSharp) {
    Write-Host "`n=== Building C# IEC104Simulator ===" -ForegroundColor Cyan
    
    $csharpDir = Join-Path $root "IEC104Simulator"
    Push-Location $csharpDir
    try {
        dotnet build -c Release
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
    } finally {
        Pop-Location
    }
    
    Write-Host "  C# build complete!" -ForegroundColor Green
}

# ─── Run ───────────────────────────────────────────────

if ($Run) {
    Write-Host "`n=== Starting IEC104 Slave Simulator ===" -ForegroundColor Cyan
    
    $csharpDir = Join-Path $root "IEC104Simulator"
    
    # Ensure DLL is in project directory
    $dllPath = Join-Path $csharpDir "iec104_core.dll"
    if (-not (Test-Path $dllPath)) {
        $releaseDll = Join-Path $root "native\build\Release\iec104_core.dll"
        if (Test-Path $releaseDll) {
            Copy-Item $releaseDll $csharpDir -Force
        } else {
            Write-Host "  WARNING: iec104_core.dll not found. Build C++ first!" -ForegroundColor Yellow
        }
    }
    
    Push-Location $csharpDir
    try {
        dotnet run -c Release
    } finally {
        Pop-Location
    }
}

if (-not ($BuildCpp -or $BuildCSharp -or $Run)) {
    Write-Host @"

IEC104 Slave Simulator - Build Script
======================================

Usage:
  .\build.ps1 -BuildCpp      Build C++ native DLL
  .\build.ps1 -BuildCSharp   Build C# project
  .\build.ps1 -Run           Run the application
  .\build.ps1 -All           Build all and run

Prerequisites:
  - CMake 3.15+
  - Visual Studio 2022 (C++ workload)
  - .NET 8 SDK

"@
}
