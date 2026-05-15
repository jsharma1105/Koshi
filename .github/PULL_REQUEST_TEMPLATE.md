<!--
Thanks for sending a pull request! Please make sure you've done the following:

1. Fork the repo and create your branch from `main`.
2. If you've added code that should be tested, add tests.
3. If you've changed APIs, update the documentation.
4. Ensure `dotnet build -c Release && dotnet test --no-build` succeeds.
5. Make sure your code passes `dotnet format --verify-no-changes`.
-->

## What does this PR do?

<!-- One-line summary, then any necessary context. Link the issue this closes. -->

Closes #

## Type of change

- [ ] Bug fix (non-breaking)
- [ ] New feature (non-breaking)
- [ ] Breaking change (existing behavior changes)
- [ ] Documentation only
- [ ] Refactor / tooling / CI

## Affected component

- [ ] `Koshi.Mcp` (MCP server)
- [ ] `Koshi.Agents` (persona installer)
- [ ] `Koshi.Core` (engine library)
- [ ] CI / packaging / release
- [ ] Documentation
- [ ] Sub-agent personas (`.github/copilot/agents/`, `.claude/agents/`)

## Checklist

- [ ] `dotnet build -c Release` succeeds
- [ ] `dotnet test --no-build -c Release` succeeds
- [ ] New tests added for new behavior
- [ ] No new runtime dependencies on cloud APIs in `Koshi.Core` or `Koshi.Mcp`
- [ ] No telemetry / "phone home" code introduced
- [ ] `CHANGELOG.md` updated (under `## [Unreleased]`)
- [ ] Documentation updated if public APIs or env vars changed

## Notes for reviewers

<!-- Anything not obvious from the diff. Trade-offs you considered. Tests you ran. -->
