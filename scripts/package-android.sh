#!/usr/bin/env bash
# Build a release APK signed with v1, v2, and v3 so it can be sideloaded
# onto a Huawei Mate 60 (HarmonyOS 4, arm64). The keystore is never committed.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ANDROID="$ROOT/android"
OUT="${LANBRIDGE_APK_OUT:-$ROOT/dist/android}"
SIGN_DIR="${LANBRIDGE_SIGN_DIR:-$ANDROID/.signing}"
KEYSTORE="${LANBRIDGE_KEYSTORE:-$SIGN_DIR/lanbridge-release.keystore}"
PASSFILE="${LANBRIDGE_KEY_PASSFILE:-$SIGN_DIR/keystore-pass.txt}"
ALIAS="${LANBRIDGE_KEY_ALIAS:-lanbridge}"
APK="$OUT/LanBridge-1.0.0.apk"

if [[ -z "${ANDROID_HOME:-}" ]]; then
  echo "ANDROID_HOME is not set" >&2
  exit 1
fi
BT="$ANDROID_HOME/build-tools/34.0.0"
if [[ ! -x "$BT/apksigner" || ! -x "$BT/zipalign" ]]; then
  echo "need apksigner and zipalign in $BT" >&2
  exit 1
fi

mkdir -p "$SIGN_DIR" "$OUT"
chmod 700 "$SIGN_DIR"

if [[ ! -f "$KEYSTORE" ]]; then
  PASS="$(openssl rand -hex 16)"
  umask 077
  printf '%s\n' "$PASS" > "$PASSFILE"
  chmod 600 "$PASSFILE"
  keytool -genkeypair -v \
    -keystore "$KEYSTORE" \
    -storetype PKCS12 \
    -alias "$ALIAS" \
    -keyalg RSA -keysize 2048 -validity 10000 \
    -storepass "$PASS" -keypass "$PASS" \
    -dname "CN=LanBridge, O=LanBridge, C=CN"
  chmod 600 "$KEYSTORE"
  echo "created keystore $KEYSTORE"
else
  if [[ ! -f "$PASSFILE" ]]; then
    echo "keystore exists but password file is missing: $PASSFILE" >&2
    exit 1
  fi
  PASS="$(tr -d '\r\n' < "$PASSFILE")"
fi

(
  cd "$ANDROID"
  ./gradlew :app:assembleRelease --offline || ./gradlew :app:assembleRelease
)

UNSIGNED="$ANDROID/app/build/outputs/apk/release/app-release-unsigned.apk"
if [[ ! -f "$UNSIGNED" ]]; then
  echo "unsigned release apk not found at $UNSIGNED" >&2
  exit 1
fi

ALIGNED="$OUT/aligned.apk"
rm -f "$ALIGNED" "$APK"
"$BT/zipalign" -p -f 4 "$UNSIGNED" "$ALIGNED"
# v4 is a sidecar .idsig. HarmonyOS sideload does not use it.
"$BT/apksigner" sign \
  --ks "$KEYSTORE" \
  --ks-key-alias "$ALIAS" \
  --ks-pass "pass:$PASS" \
  --key-pass "pass:$PASS" \
  --v1-signing-enabled true \
  --v2-signing-enabled true \
  --v3-signing-enabled true \
  --v4-signing-enabled false \
  --out "$APK" \
  "$ALIGNED"
rm -f "$ALIGNED" "${APK}.idsig"
# A plain verify starts at the APK minSdk (29). From API 24 the tool does not
# count the JAR signature, and from API 28 it verifies with v3 only, so v1 and
# v2 show as false even when those blocks are present and valid. Pin the check
# at API 21, which still accepts this SHA-256 JAR signature, and fail unless
# v1, v2, and v3 all verify.
verify_out="$("$BT/apksigner" verify --verbose --print-certs --min-sdk-version 21 "$APK" 2>&1)" || {
  printf '%s\n' "$verify_out" >&2
  echo "apksigner verify failed" >&2
  exit 1
}
for scheme in \
  "Verified using v1 scheme (JAR signing): true" \
  "Verified using v2 scheme (APK Signature Scheme v2): true" \
  "Verified using v3 scheme (APK Signature Scheme v3): true"
do
  if ! grep -q -F "$scheme" <<<"$verify_out"; then
    printf '%s\n' "$verify_out" >&2
    echo "signature scheme missing: $scheme" >&2
    exit 1
  fi
done
echo "$APK"
echo "keystore $KEYSTORE"
echo "password file $PASSFILE"
