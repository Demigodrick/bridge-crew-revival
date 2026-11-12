#!/usr/bin/env bash
# Cross-build rnnoise.dll (x86_64 Windows) from the upstream release tarball, inside Docker.
# Output: tools/rnnoise/out/rnnoise.dll, committed: releases ship that one, so this only needs running to upgrade
# RNNoise. Needs a running Docker daemon.
# The release tarball already contains the generated configure script and the model data
# (src/rnnoise_data.c), so nothing is downloaded during the build beyond the tarball itself.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="$here/out"

URL="https://github.com/xiph/rnnoise/releases/download/v0.2/rnnoise-0.2.tar.gz"
# SHA-256 of rnnoise-0.2.tar.gz as downloaded from the URL above (sha256sum of the release asset, 2026-11-08).
SHA256="90fce4b00b9ff24c08dbfe31b82ffd43bae383d85c5535676d28b0a2b11c0d37"
IMAGE="debian:bookworm"

mkdir -p "$out"
rm -f "$out/rnnoise.dll"

docker run --rm -e URL="$URL" -e SHA256="$SHA256" -v "$out:/out" "$IMAGE" bash -euo pipefail -c '
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq --no-install-recommends mingw-w64 autoconf automake libtool make curl ca-certificates file >/dev/null
  cd /tmp
  curl -fsSL -o rnnoise.tar.gz "$URL"
  actual="$(sha256sum rnnoise.tar.gz | cut -d" " -f1)"
  if [ "$actual" != "$SHA256" ]; then
    echo "rnnoise tarball SHA-256 mismatch: expected $SHA256, got $actual" >&2
    exit 1
  fi
  tar xzf rnnoise.tar.gz
  cd rnnoise-0.2
  ./configure --host=x86_64-w64-mingw32 --disable-examples --disable-doc --disable-static --enable-shared >/dev/null
  # -no-undefined: libtool refuses to build a Windows DLL without it. -static-libgcc: no libgcc_s_seh-1.dll.
  make -j"$(nproc)" LDFLAGS="-no-undefined -avoid-version -static-libgcc" >/dev/null
  dll="$(ls .libs/*.dll | head -n1)"
  x86_64-w64-mingw32-strip --strip-unneeded "$dll"
  cp "$dll" /out/rnnoise.dll
  file /out/rnnoise.dll
  echo "--- DLL imports:"
  imports="$(x86_64-w64-mingw32-objdump -p /out/rnnoise.dll | sed -n "s/^\s*DLL Name: //p")"
  echo "$imports"
  if [ -z "$imports" ]; then
    echo "objdump listed no imports for rnnoise.dll (unexpected)" >&2
    exit 1
  fi
  if echo "$imports" | grep -qvi -E "^(KERNEL32|msvcrt)\.dll$"; then
    echo "rnnoise.dll imports something besides KERNEL32.dll and msvcrt.dll" >&2
    exit 1
  fi
  echo "--- DLL exports:"
  exports="$(x86_64-w64-mingw32-objdump -p /out/rnnoise.dll)"
  for sym in rnnoise_create rnnoise_process_frame rnnoise_destroy; do
    if ! echo "$exports" | grep -qE "(^|[[:space:]])$sym\$"; then
      echo "rnnoise.dll does not export $sym" >&2
      exit 1
    fi
    echo "$sym"
  done
  chown "$(stat -c %u:%g /out)" /out/rnnoise.dll
'
echo "$out/rnnoise.dll"
