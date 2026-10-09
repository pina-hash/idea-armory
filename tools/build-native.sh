#!/usr/bin/env bash
# Builds IDEA Armory's native parts for x64 with mingw-w64 on Linux, for developers
# (docs/agent/EXPLORER.md). The shipped binaries come from tools/build-native.ps1 (MSVC, /MT,
# /guard:cf) on windows-latest; these are for checks under Wine and must never be shipped.
#
#   tools/build-native.sh [--version 0.3.3] [--out publish/native]
#
# Needs g++-mingw-w64-x86-64 (x86_64-w64-mingw32-gcc, -g++ and -windres). Writes to
# <out>/x64/: ArmoryBadges.dll, ArmoryShell.exe, BadgeProbe.exe and ShellPipeTest.exe.
# Warnings are errors. Then, for example:
#   WINEDEBUG=-all wine publish/native/x64/BadgeProbe.exe table.bin queries.txt --dll publish/native/x64/ArmoryBadges.dll
#   WINEDEBUG=-all wine publish/native/x64/ShellPipeTest.exe publish/native/x64/ArmoryShell.exe 100
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=""
out="$root/publish/native"
while [ $# -gt 0 ]; do
  case "$1" in
    --version) version="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
if [ -z "$version" ]; then
  version="$(sed -n 's:.*<Version>\([0-9.]*\)</Version>.*:\1:p' "$root/src/Armory.Agent/Armory.Agent.csproj" | head -n 1)"
fi
if ! [[ "$version" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)$ ]]; then
  echo "Version must look like 0.3.3, not \"$version\"." >&2
  exit 2
fi

cc=x86_64-w64-mingw32-gcc
cxx=x86_64-w64-mingw32-g++
windres=x86_64-w64-mingw32-windres
for tool in "$cc" "$cxx" "$windres"; do
  command -v "$tool" >/dev/null || { echo "$tool is missing (apt install g++-mingw-w64-x86-64)." >&2; exit 2; }
done

bin="$out/x64"
obj="$out/obj/x64"
mkdir -p "$bin" "$obj"
sed -e "s/@ARMORY_VERSION_MAJOR@/${BASH_REMATCH[1]}/g" \
    -e "s/@ARMORY_VERSION_MINOR@/${BASH_REMATCH[2]}/g" \
    -e "s/@ARMORY_VERSION_PATCH@/${BASH_REMATCH[3]}/g" \
    "$root/native/ArmoryVersion.h.in" > "$obj/ArmoryVersion.h"

native="$root/native"
warn=(-Wall -Wextra -Werror)
common=(-O2 -municode -static -static-libgcc -DUNICODE -D_UNICODE)

"$windres" --include-dir "$obj" --include-dir "$native/badges" -O coff \
  -o "$obj/ArmoryBadges.res.o" "$native/badges/ArmoryBadges.rc"
"$cxx" -std=c++17 "${warn[@]}" "${common[@]}" -static-libstdc++ -shared \
  -o "$bin/ArmoryBadges.dll" "$native/badges/ArmoryBadges.cpp" "$native/badges/ArmoryBadges.def" \
  "$obj/ArmoryBadges.res.o" -luuid -ladvapi32

"$windres" --include-dir "$obj" --include-dir "$native/shell" -O coff \
  -o "$obj/ArmoryShell.res.o" "$native/shell/ArmoryShell.rc"
"$cc" -std=c11 "${warn[@]}" "${common[@]}" -mwindows \
  -o "$bin/ArmoryShell.exe" "$native/shell/ArmoryShell.c" "$obj/ArmoryShell.res.o" \
  -ladvapi32 -lshell32 -luser32 -lbcrypt

"$cxx" -std=c++17 "${warn[@]}" "${common[@]}" -static-libstdc++ \
  -o "$bin/BadgeProbe.exe" "$native/badges/BadgeProbe.cpp" -lole32 -luuid -ladvapi32
"$cc" -std=c11 "${warn[@]}" "${common[@]}" \
  -o "$bin/ShellPipeTest.exe" "$native/shell/ShellPipeTest.c"

echo "built $version for x64 in $bin:"
ls -l "$bin"
