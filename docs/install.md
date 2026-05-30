# Install — every supported path

The root [README](../README.md) shows the single shortest path. This page
documents every other supported way to install Koshi.

Pick whichever matches your environment. They all reach the same MCP server
and the same on-disk format, so you can switch later without losing memory
or indexes.

| Path | When to pick it |
|---|---|
| [Shell installer](#shell-installer-recommended) | You want one command. Default. |
| [`pip install koshi`](#python--pip-install-koshi) | You are a Python host (no .NET install). |
| [`dotnet tool install --global Koshi.Mcp`](#net--dotnet-tool-install) | You already have the .NET 10 SDK. |
| [Native AOT binary (manual)](#native-aot-binary-manual) | You want full control over the download / placement / verification step. |

---

## Shell installer (recommended)

### Linux / macOS

```sh
curl -fsSL https://raw.githubusercontent.com/jsharma1105/Koshi/main/scripts/install.sh | sh
```

### Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/jsharma1105/Koshi/main/scripts/install.ps1 | iex
```

The installer downloads the host-RID AOT binary from the latest GitHub
release, verifies its SHA-256 against the per-release `manifest.json`,
installs it under a stable prefix (`%LOCALAPPDATA%\Programs\Koshi` on
Windows, `~/.local/bin` on Unix), ensures the prefix is on `PATH`, and
then runs `koshi-mcp init` to wire up MCP clients and install personas.

### Flags

| Flag | Purpose |
|---|---|
| `--prefix <dir>` | Override install directory |
| `--version <v>` | Pin a release version (e.g. `v0.8.1` or `0.8.1`) |
| `--client <name>` | Pre-select MCP client (`claude` / `copilot` / `all`) |
| `--no-init` | Install binary only, skip the wizard |
| `--non-interactive` | Run wizard non-interactively (auto-set when stdin is redirected) |
| `--skip-personas` / `--skip-team` | Pass-through wizard flags |
| `--dry-run` | Print the plan, write nothing |

`--client` accepts only `claude`, `copilot`, or `all` because those are the
two MCP clients whose config files `koshi-mcp init` writes directly. For
Cursor / Windsurf / Agency, run the installer without `--client` and the
wizard drops `.cursorrules`, `.windsurfrules`, and `AGENTS.md` into the
project automatically whenever it detects the matching project markers —
see [Client Setup](../src/Koshi.Mcp/README.md#client-setup) for one-time
config-file entries those clients still need.

The `init` wizard subcommand was added after `v0.8.1`. The installer probes
the downloaded binary's `--help` output and gracefully falls back to a
manual `koshi-agents install --client copilot` instruction on older
releases.

Uninstall removes the binary and the PATH entry only — MCP client configs,
personas, indexed corpora, and the team registry are intentionally
preserved. To uninstall:

```sh
# Linux / macOS
curl -fsSL https://raw.githubusercontent.com/jsharma1105/Koshi/main/scripts/uninstall.sh | sh -s -- --yes
```

```powershell
# Windows
irm https://raw.githubusercontent.com/jsharma1105/Koshi/main/scripts/uninstall.ps1 | iex
```

### Known constraints

- **macOS Intel (`osx-x64`):** no AOT binary published. The installer
  exits with a clear message; use `dotnet tool install --global Koshi.Mcp`
  instead.
- **Windows Defender / SmartScreen** sometimes blocks the post-download
  smoke test of a freshly-downloaded EXE for a short window. The SHA-256
  has already proven the bits are correct, so the installer warns rather
  than fails.

---

## Python — `pip install koshi`

```bash
pip install koshi
python -c "from koshi import Client
with Client() as k:
    print(k.version())"
```

No .NET install required. The first `Client()` call auto-downloads a
native AOT binary into your user cache, verified against SHA-256 hashes
baked into the wheel. See [`python/README.md`](../python/README.md) for the
full API.

---

## .NET — `dotnet tool install`

```bash
dotnet tool install --global Koshi.Mcp
# Optional: install the persona installer
dotnet tool install --global Koshi.Agents
koshi-agents install --client both
```

Requires the .NET 10 SDK on `PATH`. The binary lives at
`$HOME/.dotnet/tools/koshi-mcp` (Unix) or
`%USERPROFILE%\.dotnet\tools\koshi-mcp.exe` (Windows).

---

## Native AOT binary (manual)

Every [GitHub release](https://github.com/jsharma1105/Koshi/releases)
ships self-contained single-file `koshi-mcp` binaries for `linux-x64`,
`linux-arm64`, `osx-arm64`, `win-x64`, and `win-arm64`, plus matching
`.sha256` sidecars and a `manifest.json`. Run them on a clean machine
without any .NET runtime installed.

```bash
# Linux x64 example — adapt the RID for your platform
curl -L -o koshi-mcp \
  https://github.com/jsharma1105/Koshi/releases/latest/download/koshi-mcp-linux-x64
curl -L -o koshi-mcp.sha256 \
  https://github.com/jsharma1105/Koshi/releases/latest/download/koshi-mcp-linux-x64.sha256
sha256sum -c <(awk '{print $1"  koshi-mcp"}' koshi-mcp.sha256)
chmod +x koshi-mcp
./koshi-mcp --version  # prints "koshi-mcp X.Y.Z+<sha>" and exits
```

Intel Macs (`osx-x64`): no AOT binary published. Use
`dotnet tool install --global Koshi.Mcp`.

---

## After installing

Run the wizard:

```sh
koshi-mcp init
```

It detects installed MCP clients, registers `koshi` in each client's
config (idempotent), installs the five sub-agent personas, and optionally
registers a team. The shell installer runs this automatically unless you
pass `--no-init`.

Verify everything is wired up:

```sh
koshi-agents doctor
```

Then restart your MCP client(s) once so they pick up the new `koshi` MCP
entry. The next question you ask the agent in any project Koshi knows
about will go through it.
