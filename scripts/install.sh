#!/usr/bin/env sh
# One-command installer for the Koshi MCP server (#74 Option A).
#
# Downloads the matching koshi-mcp AOT binary for the host RID from the
# latest GitHub release, verifies its SHA-256 against the release
# manifest, installs it under ~/.local/bin (or $KOSHI_PREFIX), then
# (unless --no-init) delegates to `koshi-mcp init` for client / persona
# / team setup.
#
# Usage (web):
#   curl -fsSL https://raw.githubusercontent.com/jsharma1105/Koshi/main/scripts/install.sh | sh
#
# Usage (local checkout):
#   ./scripts/install.sh [flags]
#
# Flags:
#   --prefix DIR        Install dir (default: ~/.local/bin)
#   --version TAG       Release tag to install (e.g. 0.8.1 or v0.8.1)
#                       Can also be set via $KOSHI_VERSION.
#   --no-init           Skip the post-install `koshi-mcp init` wizard step.
#   --no-mcp-register   Alias for --no-init.
#   --non-interactive   Forwarded to `koshi-mcp init` (auto-set when no TTY).
#   --client NAME       Forwarded to `koshi-mcp init --client NAME`.
#   --skip-index        Forwarded to `koshi-mcp init --skip-index`.
#   --skip-personas     Forwarded to `koshi-mcp init --skip-personas`.
#   --skip-team         Forwarded to `koshi-mcp init --skip-team`.
#   --dry-run           Print what would happen without mutating any state.
#   -h, --help          Show this help and exit.
#
# Security note: the downloaded binary is verified against a SHA-256 hash
# fetched from the same GitHub release's manifest.json. This protects
# against transport corruption and accidental asset swap, but does NOT by
# itself guarantee authenticity if the GitHub release is compromised.

set -eu

REPO="jsharma1105/Koshi"
USER_AGENT="koshi-installer/1.0 (+https://github.com/${REPO})"

PREFIX=""
VERSION=""
NO_INIT=0
NON_INTERACTIVE=0
CLIENT=""
SKIP_INDEX=0
SKIP_PERSONAS=0
SKIP_TEAM=0
DRY_RUN=0

color() { if [ -t 1 ]; then printf '\033[%sm%s\033[0m' "$1" "$2"; else printf '%s' "$2"; fi; }
info()  { printf '[koshi] %s\n' "$1"; }
step()  { printf '%s %s\n' "$(color 36 '[koshi]')" "$1"; }
ok()    { printf '%s %s\n' "$(color 32 '[koshi]')" "$1"; }
warn()  { printf '%s %s\n' "$(color 33 '[koshi]')" "$1" >&2; }
err()   { printf '%s %s\n' "$(color 31 '[koshi]')" "$1" >&2; }

usage() {
    sed -n '2,/^set -eu/p' "$0" | sed 's/^# \{0,1\}//' | sed '/^set -eu/d'
}

# ── Parse args ──────────────────────────────────────────────────────────────
while [ $# -gt 0 ]; do
    case "$1" in
        --prefix)           PREFIX="$2"; shift 2 ;;
        --prefix=*)         PREFIX="${1#--prefix=}"; shift ;;
        --version)          VERSION="$2"; shift 2 ;;
        --version=*)        VERSION="${1#--version=}"; shift ;;
        --no-init|--no-mcp-register) NO_INIT=1; shift ;;
        --non-interactive)  NON_INTERACTIVE=1; shift ;;
        --client)           CLIENT="$2"; shift 2 ;;
        --client=*)         CLIENT="${1#--client=}"; shift ;;
        --skip-index)       SKIP_INDEX=1; shift ;;
        --skip-personas)    SKIP_PERSONAS=1; shift ;;
        --skip-team)        SKIP_TEAM=1; shift ;;
        --dry-run)          DRY_RUN=1; shift ;;
        -h|--help)          usage; exit 0 ;;
        *) err "Unknown argument: $1"; usage; exit 2 ;;
    esac
done

# ── Required tools ──────────────────────────────────────────────────────────
need() { command -v "$1" >/dev/null 2>&1 || { err "Required command not found: $1"; exit 1; }; }
need curl
need uname
need mkdir
need mv
need chmod

# Pick a sha-256 tool (sha256sum on Linux, shasum -a 256 on macOS)
SHA_CMD=""
if command -v sha256sum >/dev/null 2>&1; then
    SHA_CMD="sha256sum"
elif command -v shasum >/dev/null 2>&1; then
    SHA_CMD="shasum -a 256"
else
    err "Neither sha256sum nor shasum is available; cannot verify binary integrity."
    exit 1
fi

compute_sha256() {
    # shellcheck disable=SC2086
    $SHA_CMD "$1" | awk '{print tolower($1)}'
}

# ── 1. Detect RID ───────────────────────────────────────────────────────────
detect_rid() {
    os="$(uname -s 2>/dev/null || echo unknown)"
    arch="$(uname -m 2>/dev/null || echo unknown)"
    case "$os" in
        Linux)
            case "$arch" in
                x86_64|amd64) echo "linux-x64" ;;
                aarch64|arm64) echo "linux-arm64" ;;
                *) err "Unsupported Linux arch: $arch"; exit 1 ;;
            esac
            ;;
        Darwin)
            case "$arch" in
                arm64) echo "osx-arm64" ;;
                x86_64)
                    err "macOS Intel (x86_64) has no native AOT binary."
                    err "Install via dotnet instead:"
                    err "    dotnet tool install --global Koshi.Mcp"
                    exit 1
                    ;;
                *) err "Unsupported macOS arch: $arch"; exit 1 ;;
            esac
            ;;
        *) err "Unsupported OS: $os"; exit 1 ;;
    esac
}

# ── 2. Normalize version (strip leading 'v') ────────────────────────────────
normalize_version() {
    v="$1"
    case "$v" in
        v*|V*) echo "${v#?}" ;;
        *)     echo "$v" ;;
    esac
}

is_valid_version() {
    case "$1" in
        [0-9]*.[0-9]*.[0-9]*) return 0 ;;
        *) return 1 ;;
    esac
}

# ── 3. Resolve version ──────────────────────────────────────────────────────
resolve_version() {
    if [ -n "$VERSION" ]; then
        normalize_version "$VERSION"
        return
    fi
    if [ -n "${KOSHI_VERSION:-}" ]; then
        normalize_version "$KOSHI_VERSION"
        return
    fi
    step "Resolving latest release tag from GitHub API…" >&2
    api="https://api.github.com/repos/${REPO}/releases/latest"
    if resp="$(curl -fsSL -A "$USER_AGENT" -H 'Accept: application/vnd.github+json' "$api" 2>/dev/null)"; then
        # Prefer jq if available; fall back to grep
        if command -v jq >/dev/null 2>&1; then
            tag="$(printf '%s' "$resp" | jq -r '.tag_name // empty')"
        else
            tag="$(printf '%s' "$resp" | grep -E '"tag_name"' | head -1 | sed -E 's/.*"tag_name"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/')"
        fi
        if [ -n "$tag" ]; then
            ver="$(normalize_version "$tag")"
            if is_valid_version "$ver"; then
                printf '%s\n' "$ver"
                return
            fi
            err "GitHub API returned an unexpected tag_name: '$tag'"
        else
            err "Could not parse tag_name from GitHub API response."
        fi
    else
        err "Failed to query GitHub API ($api)."
    fi
    err "Set KOSHI_VERSION (e.g. KOSHI_VERSION=0.8.1) or pass --version to skip the API call."
    exit 1
}

# ── 4. Fetch + parse manifest ───────────────────────────────────────────────
fetch_manifest_field() {
    # Args: rid, field (filename|sha256)
    rid="$1"; field="$2"
    if command -v jq >/dev/null 2>&1; then
        printf '%s' "$MANIFEST_JSON" | jq -r ".binaries[\"${rid}\"].${field} // empty"
    else
        # Sed-only fallback: pull the rid's object, then the field.
        printf '%s' "$MANIFEST_JSON" \
            | tr -d '\n' \
            | sed -E "s/.*\"${rid}\"[[:space:]]*:[[:space:]]*\\{([^}]*)\\}.*/\\1/" \
            | grep -oE "\"${field}\"[[:space:]]*:[[:space:]]*\"[^\"]+\"" \
            | sed -E "s/.*\"${field}\"[[:space:]]*:[[:space:]]*\"([^\"]+)\".*/\\1/" \
            | head -1
    fi
}

# ── 5. Download + verify ────────────────────────────────────────────────────
download_binary() {
    url="$1"; expected_sha="$2"; tmp="$3"
    step "Downloading $url"
    if ! curl -fsSL -A "$USER_AGENT" -o "$tmp" "$url"; then
        rm -f "$tmp"
        err "Failed to download $url"
        exit 1
    fi
    actual="$(compute_sha256 "$tmp")"
    expected="$(printf '%s' "$expected_sha" | tr 'A-Z' 'a-z')"
    if [ "$actual" != "$expected" ]; then
        rm -f "$tmp"
        err "SHA-256 mismatch."
        err "  expected: $expected"
        err "  actual:   $actual"
        err "  URL: $url"
        exit 1
    fi
    ok "SHA-256 verified ($expected)"
}

# ── 6. Smoke-test the temp binary before moving into place ──────────────────
smoke_test() {
    tmp="$1"
    chmod +x "$tmp"
    step "Smoke-testing downloaded binary (--version)"
    if out="$("$tmp" --version 2>&1)"; then
        info "Binary self-reports: $(printf '%s' "$out" | tr -d '\r' | head -1)"
    else
        err "Downloaded binary failed to execute (--version)."
        err "Output: $out"
        err "Original binary (if any) was NOT replaced."
        rm -f "$tmp"
        exit 1
    fi
}

# ── 7. PATH note ────────────────────────────────────────────────────────────
path_contains() {
    case ":$PATH:" in
        *":$1:"*) return 0 ;;
        *) return 1 ;;
    esac
}

path_note() {
    dir="$1"
    if path_contains "$dir"; then
        info "$dir is already on PATH"
        return
    fi
    warn "$dir is NOT on your PATH."
    rcfile=""
    case "${SHELL:-}" in
        */zsh)  rcfile="$HOME/.zshrc" ;;
        */bash) rcfile="$HOME/.bashrc" ;;
        */fish) rcfile="$HOME/.config/fish/config.fish" ;;
        *)      rcfile="$HOME/.profile" ;;
    esac
    warn "Add this line to $rcfile (or your shell's equivalent) and restart your shell:"
    if [ "${rcfile##*.}" = "fish" ]; then
        warn "    set -gx PATH $dir \$PATH"
    else
        warn "    export PATH=\"$dir:\$PATH\""
    fi
}

# ── 8. Wizard delegation ────────────────────────────────────────────────────
wizard_supported() {
    # The 'init' wizard subcommand was introduced after v0.8.1. Older
    # binaries silently fall through to launching the MCP stdio server,
    # which would hang the installer waiting for client messages on stdin.
    # Probe --help for the word 'init'.
    help_out="$("$1" --help 2>/dev/null || true)"
    printf '%s\n' "$help_out" | grep -Eq '^[[:space:]]*init([[:space:]]|$)'
}

invoke_wizard() {
    bin="$1"
    if ! wizard_supported "$bin"; then
        warn "This binary does not yet support the 'init' wizard (likely v0.8.1 or older)."
        warn "Falling back to legacy setup. Run the following to wire up clients/personas:"
        warn "  koshi-agents install --client copilot   # or claude, cursor, windsurf"
        warn "When a release with the wizard ships, re-run this installer for the full flow."
        return 0
    fi
    auto_nonint=0
    if [ ! -t 0 ]; then auto_nonint=1; fi
    eff_nonint=0
    if [ "$NON_INTERACTIVE" -eq 1 ] || [ "$auto_nonint" -eq 1 ]; then eff_nonint=1; fi
    if [ "$auto_nonint" -eq 1 ] && [ "$NON_INTERACTIVE" -eq 0 ]; then
        warn "No interactive stdin detected; running 'koshi-mcp init --non-interactive'."
        warn "Re-run 'koshi-mcp init' from a terminal later for the full wizard."
    fi
    set --
    set -- init
    [ "$eff_nonint"     -eq 1 ] && set -- "$@" --non-interactive
    [ -n "$CLIENT" ]            && set -- "$@" --client "$CLIENT"
    [ "$SKIP_INDEX"     -eq 1 ] && set -- "$@" --skip-index
    [ "$SKIP_PERSONAS"  -eq 1 ] && set -- "$@" --skip-personas
    [ "$SKIP_TEAM"      -eq 1 ] && set -- "$@" --skip-team
    step "Running: $bin $*"
    "$bin" "$@"
}

# ── Main ────────────────────────────────────────────────────────────────────
[ -z "$PREFIX" ] && PREFIX="${KOSHI_PREFIX:-$HOME/.local/bin}"

RID="$(detect_rid)"
VER="$(resolve_version)"

MANIFEST_URL="https://github.com/${REPO}/releases/download/v${VER}/manifest.json"
step "Fetching release manifest: $MANIFEST_URL"
if ! MANIFEST_JSON="$(curl -fsSL -A "$USER_AGENT" "$MANIFEST_URL" 2>/dev/null)"; then
    err "Failed to download manifest.json for v${VER}"
    exit 1
fi

FILENAME="$(fetch_manifest_field "$RID" filename)"
SHA256="$(fetch_manifest_field "$RID" sha256)"
if [ -z "$FILENAME" ] || [ -z "$SHA256" ]; then
    err "Release v${VER} does not include a binary for RID '$RID'."
    exit 1
fi
BIN_URL="https://github.com/${REPO}/releases/download/v${VER}/${FILENAME}"
DEST="${PREFIX}/koshi-mcp"

printf '\n%s\n' "$(color 36 '===== Koshi installer =====')"
printf '  version  : %s\n' "$VER"
printf '  RID      : %s\n' "$RID"
printf '  binary   : %s\n' "$FILENAME"
printf '  source   : %s\n' "$BIN_URL"
printf '  sha256   : %s\n' "$SHA256"
printf '  prefix   : %s\n' "$PREFIX"
printf '  dest     : %s\n' "$DEST"
if [ "$NO_INIT" -eq 1 ]; then
    printf '  init     : skipped (--no-init)\n'
else
    printf '  init     : will run koshi-mcp init\n'
fi
printf '  dry-run  : %s\n\n' "$([ "$DRY_RUN" -eq 1 ] && echo yes || echo no)"

if [ "$DRY_RUN" -eq 1 ]; then
    info "Dry-run mode: no files will be modified."
    exit 0
fi

mkdir -p "$PREFIX"
TMP="${DEST}.download.tmp"
download_binary "$BIN_URL" "$SHA256" "$TMP"
smoke_test "$TMP"
mv -f "$TMP" "$DEST"
chmod +x "$DEST"
ok "Installed: $DEST"

path_note "$PREFIX"

if [ "$NO_INIT" -eq 1 ]; then
    info "Skipping wizard (--no-init)."
    printf '\n%s\n' "$(color 36 'Next steps:')"
    printf '  1. Ensure %s is on PATH (see warning above if needed).\n' "$PREFIX"
    printf '  2. Run: koshi-mcp init\n'
    printf '  3. Restart your MCP client(s) once.\n'
    exit 0
fi

# Run wizard via the absolute path so PATH ordering doesn't matter.
invoke_wizard "$DEST"
WIZ_CODE=$?

printf '\n%s\n' "$(color 36 '===== Next steps =====')"
printf '  • Binary  : %s\n' "$DEST"
printf '  • PATH    : %s\n' "$PREFIX"
printf '  • Re-run  : koshi-mcp init   (re-wire clients / personas / team)\n'
printf '  • Verify  : koshi-mcp doctor\n'
printf '  • Docs    : https://github.com/%s#readme\n\n' "$REPO"
printf '%s\n' "$(color 33 'If you just registered a new client, restart it once to pick up the koshi MCP entry.')"
exit "$WIZ_CODE"
