#/bin/bash
mkdir -p publish
mkdir -p publish/linux
mkdir -p publish/windows

dotnet publish --os linux -c Release /p:PublishSingleFile=true /p:PublishTrimmed=true /p:IncludeNativeLibrariesForSelfExtract=true --self-contained
dotnet publish --os win -c Release /p:PublishSingleFile=true /p:PublishTrimmed=true /p:IncludeNativeLibrariesForSelfExtract=true --self-contained

cp bin/Release/net10.0/linux-x64/publish/* publish/linux
cp bin/Release/net10.0/win-x64/publish/* publish/windows
cp ../README.md publish/
cd publish && zip -r bmsaudiosim.zip *
