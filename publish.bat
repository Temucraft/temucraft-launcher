@echo off
rem Builds TemucraftLauncher.exe as one self-contained file in publish\ (needs the .NET 10 SDK)
dotnet publish src\Launcher.App\Launcher.App.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish || exit /b 1
if exist launcher.env copy /y launcher.env publish\launcher.env >nul
echo.
echo Done: publish\TemucraftLauncher.exe
echo Give friends the exe and a launcher.env; put the exe in launcher\ on the server too, for the self-check.
