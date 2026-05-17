"""Shared pytest fixtures for the koshi Python package."""

from __future__ import annotations

import os
import shutil
from collections.abc import Iterator
from pathlib import Path

import pytest


@pytest.fixture
def tmp_cache(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Iterator[Path]:
    """Redirect koshi's cache root to a tmp dir for the duration of the test."""
    monkeypatch.setenv("XDG_CACHE_HOME", str(tmp_path))
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    monkeypatch.setenv("HOME", str(tmp_path))
    monkeypatch.setenv("USERPROFILE", str(tmp_path))
    monkeypatch.delenv("KOSHI_BIN", raising=False)
    yield tmp_path


@pytest.fixture
def fake_binary(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Path:
    """A fake koshi-mcp binary on disk; KOSHI_BIN points to it."""
    fake = tmp_path / "koshi-mcp"
    fake.write_text("#!/usr/bin/env false\n", encoding="utf-8")
    if os.name != "nt":
        fake.chmod(0o755)
    monkeypatch.setenv("KOSHI_BIN", str(fake))
    return fake


@pytest.fixture
def real_binary() -> Path:
    """The actual koshi-mcp binary on PATH (skips if unavailable)."""
    candidate = (
        os.environ.get("KOSHI_BIN")
        or shutil.which("koshi-mcp")
        or shutil.which("koshi-mcp.exe")
    )
    if candidate is None or not Path(candidate).exists():
        pytest.skip(
            "real koshi-mcp binary not on PATH or in KOSHI_BIN; install it first "
            "(`dotnet tool install --global Koshi.Mcp`) to run @pytest.mark.slow tests."
        )
    return Path(candidate)
