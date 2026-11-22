@echo off
rem Builds the launcher as one self-contained file per platform in publish\<platform>\ (needs the .NET 10 SDK):
rem   windows\TemucraftLauncher.exe, linux\TemucraftLauncher, macos-intel\TemucraftLauncher, macos-arm\TemucraftLauncher
rem "3 - Build launcher" copies them to 5) Release\launcher\<platform>\ for the self-check and self update.
setlocal
for %%P in ("win-x64 windows" "linux-x64 linux" "osx-x64 macos-intel" "osx-arm64 macos-arm") do call :one %%~P || exit /b 1
if exist launcher.env copy /y launcher.env publish\windows\launcher.env >nul
echo.
echo Done: publish\windows, publish\linux, publish\macos-intel, publish\macos-arm
echo Give friends the file for their system and a launcher.env next to it.
exit /b 0

:one
echo === %2 (%1) ===
dotnet publish src\Launcher.App\Launcher.App.csproj -c Release -r %1 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\%2 || exit /b 1
del /q publish\%2\*.pdb 2>nul
exit /b 0
