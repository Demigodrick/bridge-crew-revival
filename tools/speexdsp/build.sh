#!/usr/bin/env bash
# Cross-build speexdsp.dll (x86_64 Windows, floating point) from the upstream release tarball, inside Docker:
# Revival voice's echo cancellation (speex_echo_*).
# Output: tools/speexdsp/out/speexdsp.dll (git-ignored). Needs a running Docker daemon.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="$here/out"

URL="https://downloads.xiph.org/releases/speex/speexdsp-1.2.1.tar.gz"
# SHA-256 of speexdsp-1.2.1.tar.gz as downloaded from the URL above (sha256sum of the release, 2026-11-12).
SHA256="8c777343e4a6399569c72abc38a95b24db56882c83dbdb6c6424a5f4aeb54d3d"
IMAGE="debian:bookworm"

mkdir -p "$out"
rm -f "$out/speexdsp.dll"

docker run --rm -e URL="$URL" -e SHA256="$SHA256" -v "$out:/out" "$IMAGE" bash -euo pipefail -c '
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq --no-install-recommends mingw-w64 make curl ca-certificates file >/dev/null
  cd /tmp
  curl -fsSL -o speexdsp.tar.gz "$URL"
  actual="$(sha256sum speexdsp.tar.gz | cut -d" " -f1)"
  if [ "$actual" != "$SHA256" ]; then
    echo "speexdsp tarball SHA-256 mismatch: expected $SHA256, got $actual" >&2
    exit 1
  fi
  tar xzf speexdsp.tar.gz
  cd speexdsp-1.2.1
  # SSE is on by default for x86_64 in its configure; floating point is the default.
  ./configure --host=x86_64-w64-mingw32 --disable-examples --disable-static --enable-shared >/dev/null
  # -no-undefined: libtool refuses to build a Windows DLL without it. -static-libgcc: no libgcc_s_seh-1.dll.
  make -j"$(nproc)" -C libspeexdsp LDFLAGS="-no-undefined -avoid-version -static-libgcc" >/dev/null
  dll="$(ls libspeexdsp/.libs/*.dll | head -n1)"
  x86_64-w64-mingw32-strip --strip-unneeded "$dll"
  cp "$dll" /out/speexdsp.dll
  file /out/speexdsp.dll
  echo "--- DLL imports:"
  imports="$(x86_64-w64-mingw32-objdump -p /out/speexdsp.dll | sed -n "s/^\s*DLL Name: //p")"
  echo "$imports"
  if [ -z "$imports" ]; then
    echo "objdump listed no imports for speexdsp.dll (unexpected)" >&2
    exit 1
  fi
  if echo "$imports" | grep -qvi -E "^(KERNEL32|msvcrt)\.dll$"; then
    echo "speexdsp.dll imports something besides KERNEL32.dll and msvcrt.dll" >&2
    exit 1
  fi
  echo "--- DLL exports:"
  exports="$(x86_64-w64-mingw32-objdump -p /out/speexdsp.dll)"
  for sym in speex_echo_state_init_mc speex_echo_cancellation speex_echo_ctl speex_echo_state_destroy; do
    if ! echo "$exports" | grep -qE "(^|[[:space:]])$sym\$"; then
      echo "speexdsp.dll does not export $sym" >&2
      exit 1
    fi
    echo "$sym"
  done
  chown "$(stat -c %u:%g /out)" /out/speexdsp.dll
'
echo "$out/speexdsp.dll"
