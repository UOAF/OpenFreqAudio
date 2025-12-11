#/bin/bash
mkdir -p publish
mkdir -p publish/linux
mkdir -p publish/windows

dotnet publish --os linux -c Release /p:PublishSingleFile=true /p:PublishTrimmed=true /p:IncludeNativeLibrariesForSelfExtract=true --self-contained
dotnet publish --os win -c Release /p:PublishSingleFile=true /p:PublishTrimmed=true /p:IncludeNativeLibrariesForSelfExtract=true --self-contained

cp Assets/common/* publish/linux
cp Assets/common/* publish/windows

cp Assets/linux/* publish/linux
cp Assets/windows/* publish/windows

cp bin/Release/net9.0/linux-x64/publish/BMSAudioSim publish/linux
cp bin/Release/net9.0/win-x64/publish/BMSAudioSim.exe publish/windows
cp README.md publish/

