# Development

Build, test, and release `Koshi.Mcp` and `Koshi.Agents` from source.

## Build & test

```bash
git clone https://github.com/jsharma1105/Koshi
cd Koshi

# Restore + build everything
dotnet restore
dotnet build -c Release

# Run unit tests (currently 868 across Core, Mcp, Agents)
dotnet test --no-build

# End-to-end smoke test of the MCP protocol
dotnet run --project tests/Koshi.Mcp.SmokeTest -c Release
```

## Pack & install your local build

```bash
# Pack the NuGet tool
dotnet pack src/Koshi.Mcp/Koshi.Mcp.csproj -c Release -o nupkg

# Install your local build over any published one
dotnet tool install --global --add-source ./nupkg Koshi.Mcp

# Same for the personas installer
dotnet pack src/Koshi.Agents/Koshi.Agents.csproj -c Release -o nupkg
dotnet tool install --global --add-source ./nupkg Koshi.Agents
```

## Project layout

```
src/
├── Koshi.Core/        # The retrieval/context/memory/quality engine
├── Koshi.Mcp/         # The MCP server (publishes to NuGet)
└── Koshi.Agents/      # Sub-agent persona installer (publishes to NuGet)
python/                # Python wrapper (publishes to PyPI as `koshi`)
tests/
├── Koshi.Core.Tests/      # xUnit unit tests
├── Koshi.Mcp.Tests/       # MCP-tool unit tests
├── Koshi.Agents.Tests/    # CLI / persona-installer tests
└── Koshi.Mcp.SmokeTest/   # End-to-end JSON-RPC smoke harness
docs/                  # Long-form documentation (this folder)
scripts/               # Install / uninstall shell scripts
```

## Releasing

Releases are automated by
[`.github/workflows/release.yml`](https://github.com/jsharma1105/Koshi/blob/main/.github/workflows/release.yml).
To cut a new version:

1. Bump `<Version>` in both `src/Koshi.Mcp/Koshi.Mcp.csproj` and
   `src/Koshi.Agents/Koshi.Agents.csproj`.
2. Bump the Python package version in `python/pyproject.toml` and
   `python/src/koshi/__init__.py` to match.
3. Update `version` in `plugin.json` and `.claude-plugin/plugin.json` to
   match.
4. Add a new section at the top of
   [`CHANGELOG.md`](https://github.com/jsharma1105/Koshi/blob/main/CHANGELOG.md).
5. Open a PR titled `release vX.Y.Z`. Merge once CI is green.
6. Tag `main` with `vX.Y.Z` and push the tag:
   ```bash
   git tag vX.Y.Z && git push origin vX.Y.Z
   ```
7. The release workflow builds, tests, packs both packages, pushes to
   NuGet and PyPI, builds the per-RID AOT binaries, and creates a
   GitHub release with auto-generated notes.

## Pack locally without publishing

```bash
dotnet pack src/Koshi.Mcp -c Release -o nupkg -p:Version=X.Y.Z
dotnet tool install --global --add-source ./nupkg Koshi.Mcp --version X.Y.Z
```

## Contributing

See [`CONTRIBUTING.md`](https://github.com/jsharma1105/Koshi/blob/main/CONTRIBUTING.md)
for the PR checklist, conventional-commit format, and CI matrix.
