#!/usr/bin/env bash
# Build, package, and publish a StepSolve release to GitHub.
# Usage: bash scripts/release.sh v1.2.0
set -euo pipefail

VERSION="${1:?Usage: bash scripts/release.sh <version>  (e.g. v1.0.0)}"
PUBLISH="src/bin/Release/net10.0/linux-arm64/publish"
OUT="stepsolve-${VERSION}-arm64.tar.gz"

if ! command -v gh &>/dev/null; then
    echo "gh CLI not found. Install with: brew install gh" >&2
    exit 1
fi

echo "==> Writing version: $VERSION"
echo "$VERSION" > src/version.txt

echo "==> Building for linux-arm64"
dotnet publish src/StepSolve.csproj -c Release -r linux-arm64 --self-contained

echo "==> Bundling scripts and deploy files"
cp -r scripts/ "$PUBLISH/scripts/"
cp -r deploy/  "$PUBLISH/deploy/"

echo "==> Packaging $OUT"
tar -czf "$OUT" -C "$PUBLISH" .
echo "    Size: $(du -sh "$OUT" | cut -f1)"

echo "==> Creating GitHub release $VERSION"
gh release create "$VERSION" "$OUT" \
    --title "StepSolve $VERSION" \
    --generate-notes

echo "==> Cleaning up"
rm src/version.txt
rm "$OUT"

echo "==> Released $VERSION"
echo "    Users can update via the dashboard Settings → Software Update."
