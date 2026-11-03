#!/bin/sh
set -eu

repo=/repo
iss="$repo/installer/agwinterm.iss"
stage="$repo/installer/stage-portable"

app_version=$(sed -n 's/^#define AppVersion "\(.*\)"/\1/p' "$iss" | head -1)
if [ -z "$app_version" ]; then
  echo "AppVersion not found in installer/agwinterm.iss" >&2
  exit 1
fi

git_sha="${GIT_SHA:-}"
if [ -z "$git_sha" ]; then
  echo "GIT_SHA build-arg is required (short commit id for artifact names)" >&2
  exit 1
fi

rm -rf "$stage"
mkdir -p "$stage/tools" /out /releases
cp /helpers/* "$stage/tools/"

echo "== publish embedded CLI and WSL helpers =="
dotnet publish "$repo/src/Agwinterm.Ctl/Agwinterm.Ctl.csproj" \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none \
  -p:Version="$app_version" \
  -o "$stage/tools"
(cd "$stage/tools" && zip -j "$stage/tools.zip" agwintermctl.exe agwinterm-hook.exe agwinterm-hook-linux-x64 agwinterm-hook-linux-arm64)

echo "== publish single-file portable exe (${app_version}) =="
dotnet publish "$repo/src/Agwinterm.Win32/Agwinterm.Win32.csproj" \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:SatelliteResourceLanguages=en \
  -p:DebugType=none \
  -p:Version="$app_version" \
  -p:PortableToolsArchive="$stage/tools.zip" \
  -o "$stage"

exe="$stage/Agwinterm.Win32.exe"
if [ ! -f "$exe" ]; then
  echo "publish produced no Agwinterm.Win32.exe" >&2
  exit 1
fi

portable_name="agwinterm-portable-${app_version}-g${git_sha}-win-x64.exe"
short_name="agwinterm-${app_version}-g${git_sha}-win-x64.exe"
dst="/out/$portable_name"

cp "$exe" "$dst"
cp -r "$stage/tools" /out/
size_mb=$(du -m "$dst" | cut -f1)
echo "== done: $dst (${size_mb} MB) =="

cp "$dst" "/releases/$portable_name"
cp "$dst" "/releases/$short_name"
echo "== local release: /releases/$portable_name =="
echo "== local release: /releases/$short_name =="
