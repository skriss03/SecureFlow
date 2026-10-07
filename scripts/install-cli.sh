#!/usr/bin/env sh
# Install the `secureflow` command for the current user (Linux / macOS). After this, in ANY project folder:
#
#     secureflow scan
#
# Usage:
#   SECUREFLOW_CLI_BASE_URL=https://artifactory.example.com/artifactory/generic-local/secureflow sh install-cli.sh
#   curl -fsSL https://artifactory.example.com/.../install-cli.sh | SECUREFLOW_CLI_BASE_URL=... sh
#
# Settings (environment variables): SECUREFLOW_CLI_BASE_URL (required unless run from the repo with
# artifacts/ built), SECUREFLOW_CLI_VERSION (default 0.1.0), SECUREFLOW_INSTALL_DIR (default ~/.secureflow/bin),
# SECUREFLOW_SERVER (optional: written to ~/.secureflow/config.yml).
set -eu

VERSION="${SECUREFLOW_CLI_VERSION:-0.1.0}"
INSTALL_DIR="${SECUREFLOW_INSTALL_DIR:-$HOME/.secureflow/bin}"
BASE_URL="${SECUREFLOW_CLI_BASE_URL:-}"

if [ -z "$BASE_URL" ]; then
  here="$(cd "$(dirname "$0")" 2>/dev/null && pwd || true)"
  if [ -n "$here" ] && [ -d "$here/../artifacts/cli" ]; then BASE_URL="file://$(cd "$here/../artifacts/cli" && pwd)"; fi
fi
[ -n "$BASE_URL" ] || { echo "Set SECUREFLOW_CLI_BASE_URL to the folder that holds <version>/secureflow-<rid>." >&2; exit 1; }

case "$(uname -s)" in Darwin) os=osx ;; *) os=linux ;; esac
case "$(uname -m)" in arm64|aarch64) arch=arm64 ;; *) arch=x64 ;; esac
url="${BASE_URL%/}/$VERSION/secureflow-$os-$arch"
target="$INSTALL_DIR/secureflow"

echo "Installing secureflow $VERSION"
echo "  from $url"
echo "  to   $target"
mkdir -p "$INSTALL_DIR"
case "$url" in
  file://*) cp "${url#file://}" "$target.download" ;;
  *) curl -fsSL "$url" -o "$target.download" ;;
esac
mv "$target.download" "$target"
chmod +x "$target"

if [ -n "${SECUREFLOW_SERVER:-}" ]; then
  mkdir -p "$HOME/.secureflow"
  cfg="$HOME/.secureflow/config.yml"
  { echo "server: $SECUREFLOW_SERVER"; [ -f "$cfg" ] && grep -v '^[[:space:]]*server[[:space:]]*:' "$cfg" || true; } > "$cfg.tmp" && mv "$cfg.tmp" "$cfg"
  echo "  server set to $SECUREFLOW_SERVER in $cfg"
fi

case ":$PATH:" in
  *":$INSTALL_DIR:"*) ;;
  *) for rc in "$HOME/.bashrc" "$HOME/.zshrc"; do
       [ -f "$rc" ] && ! grep -q '.secureflow/bin' "$rc" && echo "export PATH=\"$INSTALL_DIR:\$PATH\"" >> "$rc"
     done
     echo "  added $INSTALL_DIR to PATH in your shell profile" ;;
esac

"$target" --version
echo
echo "Done. Open a NEW terminal, go to any project folder and run:"
echo "    secureflow scan"
