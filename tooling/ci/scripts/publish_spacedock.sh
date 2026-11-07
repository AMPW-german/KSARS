#!/usr/bin/env bash
set -euo pipefail

: "${SPACEDOCK_ID:?Missing SPACEDOCK_ID}"
: "${SPACEDOCK_USERNAME:?Missing SPACEDOCK_USERNAME}"
: "${SPACEDOCK_PASSWORD:?Missing SPACEDOCK_PASSWORD}"
: "${RELEASE_VERSION:?Missing RELEASE_VERSION}"
: "${KSA_VERSION:?Missing KSA_VERSION}"
: "${ZIPBALL:?Missing ZIPBALL}"

printf '%s' "${RELEASE_CHANGELOG:-}" > spacedock-changelog.md

curl -sS --fail --form-string "username=$SPACEDOCK_USERNAME" \
  --form-string "password=$SPACEDOCK_PASSWORD" \
  -c ./cookies \
  https://spacedock.info/api/login

curl -sS --fail -b ./cookies \
  --form-string "version=$RELEASE_VERSION" \
  -F "changelog=<spacedock-changelog.md" \
  --form-string "game-version=$KSA_VERSION" \
  --form-string "notify-followers=yes" \
  -F "zipball=@$ZIPBALL" \
  "https://spacedock.info/api/mod/${SPACEDOCK_ID}/update"
