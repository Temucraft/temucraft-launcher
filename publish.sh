#!/bin/sh
# Builds TemucraftLauncher.exe (Windows, one self-contained file) on Linux, the same as publish.bat on Windows.
# Needs the .NET 10 SDK: https://learn.microsoft.com/dotnet/core/install/linux
# Optional: ./publish.sh /var/www/wow-files   also copies the exe to <folder>/launcher/ for the self-check.
set -e
cd "$(dirname "$0")"
dotnet publish src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
[ -f launcher.env ] && cp launcher.env publish/launcher.env
if [ -n "$1" ]; then
  mkdir -p "$1/launcher"
  cp publish/TemucraftLauncher.exe "$1/launcher/"
  echo "Copied to $1/launcher/: re-run server/make-manifest.py so the self-check knows it."
fi
echo
echo "Done: publish/TemucraftLauncher.exe"
echo "Give friends the exe and a launcher.env."
