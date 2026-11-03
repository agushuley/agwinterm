set -eu
umask 077
machine=$(uname -m)
case "$machine" in
    x86_64) arch=x64 ;;
    aarch64|arm64) arch=arm64 ;;
    *) echo "Unsupported WSL architecture: $machine (expected x86_64 or aarch64)" >&2; exit 1 ;;
esac
source=$(wslpath -u "$1")/agwinterm-hook-linux-$arch
shift
[ -f "$source" ] || { echo "The native WSL helper is missing: $source. Reinstall or rebuild the application." >&2; exit 1; }
temp=$(mktemp -d /tmp/agwinterm-native.XXXXXXXX)
trap 'rm -f "$temp/helper"; rmdir "$temp"' EXIT
trap 'exit 1' HUP INT TERM
cp "$source" "$temp/helper"
chmod 700 "$temp/helper"
"$temp/helper" "$@"
