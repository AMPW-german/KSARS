#!/usr/bin/env bash
set -euo pipefail

: "${GITHUB_REPOSITORY:?Missing GITHUB_REPOSITORY}"
: "${GITHUB_TOKEN:?Missing GITHUB_TOKEN}"
: "${RELEASE_VERSION:?Missing RELEASE_VERSION}"
: "${KSA_VERSION:?Missing KSA_VERSION}"
: "${ASSET_PATH:?Missing ASSET_PATH}"

ASSET_NAME="$(basename "$ASSET_PATH")"
if [ ! -f "$ASSET_PATH" ]; then
  echo "Expected release asset file '$ASSET_PATH' was not found." >&2
  exit 1
fi

json_payload=$(jq -n \
  --arg tag_name "$RELEASE_VERSION" \
  --arg name "$RELEASE_VERSION" \
  --arg body "Built for KSA version $KSA_VERSION" \
  '{ tag_name: $tag_name, name: $name, body: $body, draft: true, prerelease: false, generate_release_notes: true }')

echo "Creating release with payload: $json_payload"
response_file=$(mktemp)
http_status=$(curl -sS -L -X POST \
  -H "Accept: application/vnd.github+json" \
  -H "Authorization: Bearer $GITHUB_TOKEN" \
  -H "X-GitHub-Api-Version: 2026-03-10" \
  "https://api.github.com/repos/${GITHUB_REPOSITORY}/releases" \
  -d "$json_payload" \
  -o "$response_file" \
  -w "%{http_code}")

if [ "$http_status" -ne 201 ]; then
  echo "Failed to create release. Expected HTTP 201, got HTTP $http_status." >&2
  cat "$response_file" >&2
  exit 1
fi

upload_url=$(jq -r '.upload_url' "$response_file")
if [ -z "$upload_url" ] || [ "$upload_url" = "null" ]; then
  echo "Release created but upload_url is missing in response." >&2
  cat "$response_file" >&2
  exit 1
fi

upload_url="${upload_url%\{*}"
echo "Uploading release asset: $ASSET_NAME"
upload_response_file=$(mktemp)
upload_status=$(curl -sS -L -X POST \
  -H "Accept: application/vnd.github+json" \
  -H "Authorization: Bearer $GITHUB_TOKEN" \
  -H "Content-Type: application/zip" \
  -H "X-GitHub-Api-Version: 2026-03-10" \
  --data-binary @"$ASSET_PATH" \
  "${upload_url}?name=${ASSET_NAME}" \
  -o "$upload_response_file" \
  -w "%{http_code}")

if [ "$upload_status" -ne 201 ]; then
  echo "Failed to upload release asset. Expected HTTP 201, got HTTP $upload_status." >&2
  cat "$upload_response_file" >&2
  exit 1
fi

release_id=$(jq -r '.id' "$response_file")
if [ -z "$release_id" ] || [ "$release_id" = "null" ]; then
  echo "Release created but id is missing in response." >&2
  cat "$response_file" >&2
  exit 1
fi

publish_response_file=$(mktemp)
publish_status=$(curl -sS -L -X PATCH \
  -H "Accept: application/vnd.github+json" \
  -H "Authorization: Bearer $GITHUB_TOKEN" \
  -H "Content-Type: application/json" \
  -H "X-GitHub-Api-Version: 2026-03-10" \
  "https://api.github.com/repos/${GITHUB_REPOSITORY}/releases/${release_id}" \
  -d '{"draft":false}' \
  -o "$publish_response_file" \
  -w "%{http_code}")

if [ "$publish_status" -ne 200 ]; then
  echo "Failed to publish release. Expected HTTP 200, got HTTP $publish_status." >&2
  cat "$publish_response_file" >&2
  exit 1
fi

release_changelog=$(jq -r '.body // ""' "$publish_response_file")
if [ -n "${GITHUB_OUTPUT:-}" ]; then
  {
    echo "changelog<<EOF"
    echo "$release_changelog"
    echo "EOF"
  } >> "$GITHUB_OUTPUT"
fi

echo "Release created and asset uploaded successfully."
