#!/bin/sh
# Builds the driver and the test tool on Linux (.NET SDK, net48 reference assemblies) and zips them.
set -e
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 PATH="$HOME/.dotnet:$PATH"
dotnet build -c Release SxTest/SxTest.csproj
out=dist/Sonix290Camera
rm -rf dist && mkdir -p $out
cp SxTest/bin/Release/net48/ASCOM.Sonix290.Camera.dll SxTest/bin/Release/net48/ASCOM.Sonix290.Camera.pdb \
   SxTest/bin/Release/net48/SxTest.exe SxTest/bin/Release/net48/SxTest.exe.config package/* $out/
cp ../README.md $out/ && cp ../LICENSE $out/LICENSE.txt
for f in $out/*.cmd $out/*.vbs $out/*.md $out/*.txt; do sed -i 's/\r\?$/\r/' "$f"; done
(cd dist && zip -qr Sonix290Camera.zip Sonix290Camera)
ls -l dist/Sonix290Camera.zip
