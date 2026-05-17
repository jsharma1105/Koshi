"""Unit tests for koshi.binary: rid detection, cache layout, atomic download."""

from __future__ import annotations

import hashlib
import os
from pathlib import Path

import pytest

from koshi import _manifest, binary
from koshi.errors import (
    BinaryCorruptedError,
    BinaryNotFoundError,
    UnsupportedPlatformError,
)


@pytest.mark.parametrize(
    ("system", "machine", "expected"),
    [
        ("Linux", "x86_64", "linux-x64"),
        ("Linux", "aarch64", "linux-arm64"),
        ("Linux", "arm64", "linux-arm64"),
        ("Darwin", "x86_64", "osx-x64"),
        ("Darwin", "arm64", "osx-arm64"),
        ("Windows", "AMD64", "win-x64"),
        ("Windows", "x86_64", "win-x64"),
        ("Windows", "ARM64", "win-arm64"),
    ],
)
def test_detect_rid_maps_supported_combinations(
    monkeypatch: pytest.MonkeyPatch, system: str, machine: str, expected: str
) -> None:
    monkeypatch.setattr("platform.system", lambda: system)
    monkeypatch.setattr("platform.machine", lambda: machine)
    assert binary.detect_rid() == expected


def test_detect_rid_rejects_unsupported_platforms(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("platform.system", lambda: "FreeBSD")
    monkeypatch.setattr("platform.machine", lambda: "x86_64")
    with pytest.raises(UnsupportedPlatformError, match="FreeBSD"):
        binary.detect_rid()


def test_versioned_cache_dir_includes_version(tmp_cache: Path) -> None:
    cache = binary.versioned_cache_dir()
    assert _manifest.VERSION in str(cache)
    assert cache.name == _manifest.VERSION
    # Stale-version isolation: a v0.3.0 cache dir must NOT match a v0.4.0 lookup.
    assert "0.4.0" in str(cache) or _manifest.VERSION in str(cache)


def test_resolve_binary_returns_koshi_bin_when_set(
    tmp_cache: Path, fake_binary: Path
) -> None:
    resolved = binary.resolve_binary()
    assert resolved == fake_binary


def test_resolve_binary_raises_when_koshi_bin_does_not_exist(
    tmp_cache: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("KOSHI_BIN", str(tmp_cache / "does-not-exist"))
    with pytest.raises(BinaryNotFoundError, match="does not exist"):
        binary.resolve_binary()


def test_atomic_download_writes_then_renames(
    tmp_cache: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    payload = b"the bytes of a fake binary " * 100
    expected_hash = hashlib.sha256(payload).hexdigest()
    dest = tmp_cache / "koshi-mcp-test"

    class _FakeResp:
        status = 200

        def __init__(self) -> None:
            self._payload = payload
            self._pos = 0

        def read(self, n: int) -> bytes:
            chunk = self._payload[self._pos : self._pos + n]
            self._pos += n
            return chunk

        def __enter__(self) -> _FakeResp:
            return self

        def __exit__(self, *exc: object) -> None:
            return None

    monkeypatch.setattr(binary, "urlopen", lambda *a, **kw: _FakeResp())

    binary._atomic_download("https://example.invalid/fake", dest, expected_hash)

    assert dest.exists()
    assert dest.read_bytes() == payload
    # No leftover temp files
    tmp_siblings = [p for p in dest.parent.iterdir() if p.name.startswith(".koshi-mcp-test")]
    assert tmp_siblings == []


def test_atomic_download_raises_on_hash_mismatch(
    tmp_cache: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    payload = b"this content does not match the expected hash"
    dest = tmp_cache / "koshi-mcp-bad"

    class _FakeResp:
        status = 200

        def __init__(self) -> None:
            self._payload = payload
            self._pos = 0

        def read(self, n: int) -> bytes:
            chunk = self._payload[self._pos : self._pos + n]
            self._pos += n
            return chunk

        def __enter__(self) -> _FakeResp:
            return self

        def __exit__(self, *exc: object) -> None:
            return None

    monkeypatch.setattr(binary, "urlopen", lambda *a, **kw: _FakeResp())

    fake_expected = hashlib.sha256(b"different bytes entirely").hexdigest()
    with pytest.raises(BinaryCorruptedError, match="hash mismatch"):
        binary._atomic_download("https://example.invalid/fake", dest, fake_expected)

    # Destination file must NOT be left behind if hash failed.
    assert not dest.exists()


def test_resolve_binary_uses_cached_when_present_and_hash_matches(
    tmp_cache: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr("platform.system", lambda: "Linux")
    monkeypatch.setattr("platform.machine", lambda: "x86_64")
    monkeypatch.delenv("KOSHI_BIN", raising=False)

    cache_dir = binary.versioned_cache_dir()
    cache_dir.mkdir(parents=True, exist_ok=True)
    cached_bin = cache_dir / "koshi-mcp-linux-x64"
    cached_bin.write_bytes(b"cached binary")

    # If the manifest has no entry for linux-x64 (dev build), the cache is used as-is.
    monkeypatch.setattr(binary, "EXPECTED_BINARIES", {})
    resolved = binary.resolve_binary()
    assert resolved == cached_bin


def test_resolve_binary_redownloads_when_cached_hash_mismatches(
    tmp_cache: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr("platform.system", lambda: "Linux")
    monkeypatch.setattr("platform.machine", lambda: "x86_64")
    monkeypatch.delenv("KOSHI_BIN", raising=False)

    cache_dir = binary.versioned_cache_dir()
    cache_dir.mkdir(parents=True, exist_ok=True)
    cached_bin = cache_dir / "koshi-mcp-linux-x64"
    cached_bin.write_bytes(b"stale binary contents")

    fresh_payload = b"fresh binary contents " * 50
    fresh_hash = hashlib.sha256(fresh_payload).hexdigest()
    monkeypatch.setattr(
        binary,
        "EXPECTED_BINARIES",
        {"linux-x64": {"filename": "koshi-mcp-linux-x64", "sha256": fresh_hash}},
    )

    class _FakeResp:
        status = 200

        def __init__(self) -> None:
            self._payload = fresh_payload
            self._pos = 0

        def read(self, n: int) -> bytes:
            chunk = self._payload[self._pos : self._pos + n]
            self._pos += n
            return chunk

        def __enter__(self) -> _FakeResp:
            return self

        def __exit__(self, *exc: object) -> None:
            return None

    monkeypatch.setattr(binary, "urlopen", lambda *a, **kw: _FakeResp())
    monkeypatch.setattr("shutil.which", lambda name: None)

    resolved = binary.resolve_binary()
    assert resolved == cached_bin
    assert resolved.read_bytes() == fresh_payload


def test_resolve_binary_falls_through_to_path(
    tmp_cache: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr("platform.system", lambda: "Linux")
    monkeypatch.setattr("platform.machine", lambda: "x86_64")
    monkeypatch.delenv("KOSHI_BIN", raising=False)
    monkeypatch.setattr(binary, "EXPECTED_BINARIES", {})

    path_bin = tmp_cache / "from-path" / "koshi-mcp"
    path_bin.parent.mkdir(parents=True, exist_ok=True)
    path_bin.write_text("fake")
    if os.name != "nt":
        path_bin.chmod(0o755)

    monkeypatch.setattr("shutil.which", lambda name: str(path_bin) if "koshi" in name else None)

    resolved = binary.resolve_binary()
    assert resolved == path_bin


def test_resolve_binary_refuses_to_download_without_manifest(
    tmp_cache: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr("platform.system", lambda: "Linux")
    monkeypatch.setattr("platform.machine", lambda: "x86_64")
    monkeypatch.delenv("KOSHI_BIN", raising=False)
    monkeypatch.setattr(binary, "EXPECTED_BINARIES", {})
    monkeypatch.setattr("shutil.which", lambda name: None)

    with pytest.raises(BinaryNotFoundError, match="no manifest entry"):
        binary.resolve_binary()
