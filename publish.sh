#!/bin/sh
# Builds TemucraftLauncher.exe (Windows, one self-contained file) on Linux, the same as publish.bat on Windows.
# Needs the .NET 10 SDK: https://learn.microsoft.com/dotnet/core/install/linux
# Optional: ./publish.sh /var/www/wow-files   also copies the exe to <folder>/launcher/ for the self-check, and
#           server/make-manifest.py next to that folder.
set -e
cd "$(dirname "$0")"
dotnet publish src/Launcher.App/Launcher.App.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
[ -f launcher.env ] && cp launcher.env publish/launcher.env
if [ -n "$1" ]; then
  mkdir -p "$1/launcher"
  cp publish/TemucraftLauncher.exe "$1/launcher/"
  # keep the server's manifest tool the same version as this launcher (upload.py runs <folder>/../make-manifest.py)
  cp server/make-manifest.py "$1/../make-manifest.py"
  echo "Copied to $1/launcher/: re-run server/make-manifest.py so the self-check knows it."
fi
echo
echo "Done: publish/TemucraftLauncher.exe"
echo "Give friends the exe and a launcher.env."
