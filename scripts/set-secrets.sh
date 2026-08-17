#!/usr/bin/env bash
# Push the credentials the live workflow needs into GitHub Actions secrets.
#
# Values are read from an env file and piped to `gh` on stdin, never passed as arguments,
# so they do not appear in the process list or in shell history.
#
#   ./scripts/set-secrets.sh [path-to-env-file]
#
# The env file uses the provider's own naming; GitHub gets the conventional names that
# live.yml and the SDKs expect.

set -euo pipefail

env_file="${1:-$HOME/code/docket-mcp/.env}"
repo="${ACPKIT_REPO:-vezaynk/acpkit}"

if [ ! -f "$env_file" ]; then
  echo "No env file at $env_file" >&2
  exit 1
fi

set -a
# shellcheck disable=SC1090
. "$env_file"
set +a

set_secret() {
  local name="$1" value="${2:-}"
  if [ -z "$value" ]; then
    echo "  skip  $name (not present in $env_file)"
    return
  fi

  if printf '%s' "$value" | gh secret set "$name" --repo "$repo" >/dev/null; then
    echo "  set   $name"
  else
    echo "  FAIL  $name" >&2
    return 1
  fi
}

echo "Setting secrets on $repo"
set_secret ANTHROPIC_API_KEY "${ANTHROPIC_KEY:-${ANTHROPIC_API_KEY:-}}"
set_secret OPENAI_API_KEY    "${OPENAI_KEY:-${OPENAI_API_KEY:-}}"
set_secret XAI_API_KEY       "${XAI_KEY:-${XAI_API_KEY:-}}"

echo
echo "Secrets now on the repository:"
gh secret list --repo "$repo"
