#!/usr/bin/env sh
# Removes the Koshi MCP server binary installed by install.sh.
#
# By design this leaves MCP client configuration, persona files, and
# indexed corpora intact so a later re-install does not lose state.
#
# Usage:
#   ./scripts/uninstall.sh [--prefix DIR] [--yes] [-h|--help]

set -eu

PREFIX=""
YES=0

color() { if [ -t 1 ]; then printf '\033[%sm%s\033[0m' "$1" "$2"; else printf '%s' "$2"; fi; }
info() { printf '[koshi] %s\n' "$1"; }
ok()   { printf '%s %s\n' "$(color 32 '[koshi]')" "$1"; }
warn() { printf '%s %s\n' "$(color 33 '[koshi]')" "$1" >&2; }
err()  { printf '%s %s\n' "$(color 31 '[koshi]')" "$1" >&2; }

usage() {
    cat <<'EOF'
Removes the Koshi MCP server binary installed by install.sh.

Flags:
  --prefix DIR   Install directory (default: ~/.local/bin or $KOSHI_PREFIX)
  --yes          Skip the confirmation prompt
  -h, --help     Show this help

The following are NOT removed:
  • MCP client config entries
  • Installed persona files
  • Indexed corpora / vaults under .koshi/ in your projects
  • Team registry under .koshi/teams.json
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --prefix)   PREFIX="$2"; shift 2 ;;
        --prefix=*) PREFIX="${1#--prefix=}"; shift ;;
        --yes|-y)   YES=1; shift ;;
        -h|--help)  usage; exit 0 ;;
        *) err "Unknown argument: $1"; usage >&2; exit 2 ;;
    esac
done

[ -z "$PREFIX" ] && PREFIX="${KOSHI_PREFIX:-$HOME/.local/bin}"
DEST="${PREFIX}/koshi-mcp"

printf '\n%s\n' "$(color 36 '===== Koshi uninstaller =====')"
printf '  prefix : %s\n' "$PREFIX"
printf '  binary : %s\n\n' "$DEST"

printf '%s\n' "$(color 33 'The following are NOT removed:')"
printf '  • MCP client config entries\n'
printf '  • Installed persona files\n'
printf '  • Indexed corpora / vaults under .koshi/ in your projects\n'
printf '  • Team registry under .koshi/teams.json\n\n'

# Surface concurrent .NET-tool installs the user may not realise are
# present. `dotnet tool install -g Koshi.Mcp` writes to
# ~/.dotnet/tools/koshi-mcp and wins on PATH for anyone who used that
# path. Removing one without the other leaves the user thinking
# uninstall failed because `koshi-mcp` still resolves.
if command -v dotnet >/dev/null 2>&1; then
    if dt_list="$(dotnet tool list -g 2>/dev/null)"; then
        dt_found=""
        printf '%s\n' "$dt_list" | grep -Eq '^[[:space:]]*koshi\.mcp[[:space:]]' && dt_found="$dt_found Koshi.Mcp"
        printf '%s\n' "$dt_list" | grep -Eq '^[[:space:]]*koshi\.agents[[:space:]]' && dt_found="$dt_found Koshi.Agents"
        if [ -n "$dt_found" ]; then
            warn "Detected concurrent .NET tool install(s):$dt_found"
            warn "This script does NOT remove .NET-tool installs. To finish removal, run:"
            for pkg in $dt_found; do
                warn "  dotnet tool uninstall -g $pkg"
            done
        fi
    fi
fi

if [ "$YES" -eq 0 ]; then
    if [ ! -t 0 ]; then
        err "Refusing to uninstall without --yes when stdin is not a TTY."
        exit 1
    fi
    printf 'Proceed with binary removal? [y/N] '
    read -r ans
    case "$ans" in
        y|Y|yes|YES) ;;
        *) printf 'Aborted.\n'; exit 0 ;;
    esac
fi

if [ -e "$DEST" ] || [ -L "$DEST" ]; then
    if rm -f "$DEST"; then
        ok "Removed $DEST"
    else
        err "Failed to remove $DEST"
        exit 1
    fi
else
    warn "No binary at $DEST (already removed?)"
fi

# Remove install dir if empty.
if [ -d "$PREFIX" ]; then
    if [ -z "$(ls -A "$PREFIX" 2>/dev/null)" ]; then
        rmdir "$PREFIX" 2>/dev/null && ok "Removed empty install dir $PREFIX" || true
    else
        warn "Install dir $PREFIX kept (not empty)."
    fi
fi

printf '\n%s\n' "$(color 36 '[koshi] Uninstall complete.')"
case "${SHELL:-}" in
    */zsh|*/bash|*/fish) info "If you added $PREFIX to your shell rc by hand, you may want to remove that line." ;;
esac
