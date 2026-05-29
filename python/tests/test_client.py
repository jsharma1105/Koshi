"""Unit tests for koshi.client: wire-format assertion, version handshake, errors."""

from __future__ import annotations

import io
import json
import subprocess
from pathlib import Path
from typing import Any

import pytest

from koshi import Client, IncompatibleBinaryError, ToolError
from koshi._manifest import VERSION
from koshi.client import _is_compatible_version


class _FakeProc:
    """Stand-in for subprocess.Popen used to capture the wire protocol."""

    def __init__(self, scripted_responses: list[dict[str, Any]]) -> None:
        self._responses = scripted_responses
        self._response_iter = iter(scripted_responses)
        self.captured_writes: list[str] = []
        self.stdin = io.StringIO()
        # We intercept writes manually so we can drive scripted responses.
        self._stdin_buf: list[str] = []
        self._stdout_buf: list[str] = []
        self.stdout = _ScriptedStdout(self)
        self.stderr = io.StringIO("")

    def feed_response(self, payload: dict[str, Any]) -> None:
        self._stdout_buf.append(json.dumps(payload) + "\n")

    def write_stdin(self, data: str) -> None:
        self._stdin_buf.append(data)
        # If a scripted response is queued, push it on each write
        for line in data.splitlines():
            if not line.strip():
                continue
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue
            if msg.get("method") == "notifications/initialized":
                continue
            try:
                resp = next(self._response_iter)
            except StopIteration:
                continue
            # Echo the same id back if not already set.
            resp.setdefault("id", msg.get("id"))
            resp.setdefault("jsonrpc", "2.0")
            self.feed_response(resp)

    def wait(self, timeout: float | None = None) -> int:
        return 0

    def kill(self) -> None:
        return None

    def stdin_value(self) -> str:
        return "".join(self._stdin_buf)


class _ScriptedStdin:
    def __init__(self, proc: _FakeProc) -> None:
        self._proc = proc

    def write(self, data: str) -> int:
        self._proc.write_stdin(data)
        return len(data)

    def flush(self) -> None:
        return None

    def close(self) -> None:
        return None


class _ScriptedStdout:
    def __init__(self, proc: _FakeProc) -> None:
        self._proc = proc

    def readline(self) -> str:
        if not self._proc._stdout_buf:
            return ""
        return self._proc._stdout_buf.pop(0)


def _install_fake_proc(
    monkeypatch: pytest.MonkeyPatch, responses: list[dict[str, Any]]
) -> _FakeProc:
    fake = _FakeProc(responses)
    fake.stdin = _ScriptedStdin(fake)  # type: ignore[assignment]

    def _popen(*args: object, **kwargs: object) -> _FakeProc:
        return fake

    monkeypatch.setattr(subprocess, "Popen", _popen)
    return fake


def _make_initialize_response(server_version: str = VERSION) -> dict[str, Any]:
    return {
        "result": {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "serverInfo": {"name": "koshi-mcp", "version": server_version},
        }
    }


def test_client_handshakes_initialize_with_correct_envelope(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    fake = _install_fake_proc(monkeypatch, [_make_initialize_response()])

    with Client(binary=fake_binary) as c:
        assert c.server_version == VERSION

    raw = fake.stdin_value()
    lines = [ln for ln in raw.splitlines() if ln.strip()]
    init = json.loads(lines[0])
    assert init["jsonrpc"] == "2.0"
    assert init["method"] == "initialize"
    assert init["params"]["clientInfo"]["name"] == "koshi-py"
    assert init["params"]["clientInfo"]["version"] == VERSION

    notify = json.loads(lines[1])
    assert notify["method"] == "notifications/initialized"
    assert "id" not in notify  # notifications must NOT have an id


def test_search_uses_tools_call_envelope(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    fake = _install_fake_proc(
        monkeypatch,
        [
            _make_initialize_response(),
            {"result": {"content": [{"type": "text", "text": "Found 0 results."}]}},
        ],
    )

    with Client(binary=fake_binary) as c:
        text = c.search("auth pattern", top_k=7)

    assert text == "Found 0 results."

    raw = fake.stdin_value()
    lines = [ln for ln in raw.splitlines() if ln.strip()]
    # Lines: 0 = initialize, 1 = initialized notification, 2 = tools/call
    tools_call = json.loads(lines[2])
    assert tools_call["jsonrpc"] == "2.0"
    assert tools_call["method"] == "tools/call"
    assert tools_call["params"]["name"] == "koshi_search"
    assert tools_call["params"]["arguments"] == {"query": "auth pattern", "topK": 7}


def test_remember_passes_full_argument_set(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    fake = _install_fake_proc(
        monkeypatch,
        [
            _make_initialize_response(),
            {"result": {"content": [{"type": "text", "text": "✅ Remembered"}]}},
        ],
    )

    with Client(binary=fake_binary) as c:
        c.remember(content="JWT 1h", subject="auth", type="Decision",
                   confidence=0.95, source="user")

    tools_call = json.loads(fake.stdin_value().splitlines()[2])
    assert tools_call["params"]["name"] == "koshi_remember"
    assert tools_call["params"]["arguments"] == {
        "content": "JWT 1h",
        "subject": "auth",
        "type": "Decision",
        "confidence": 0.95,
        "source": "user",
    }


def test_tool_error_response_raises_tool_error(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    _install_fake_proc(
        monkeypatch,
        [
            _make_initialize_response(),
            {"result": {"content": [{"type": "text", "text": "❌ Subject must not be empty."}]}},
        ],
    )

    with Client(binary=fake_binary) as c:
        with pytest.raises(ToolError) as excinfo:
            c.forget(subject="")
        assert "Subject must not be empty" in str(excinfo.value)
        assert excinfo.value.tool == "koshi_forget"


def test_is_error_flag_also_raises_tool_error(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    _install_fake_proc(
        monkeypatch,
        [
            _make_initialize_response(),
            {
                "result": {
                    "isError": True,
                    "content": [{"type": "text", "text": "something broke"}],
                }
            },
        ],
    )

    with Client(binary=fake_binary) as c, pytest.raises(ToolError, match="something broke"):
        c.search("x")


def test_version_mismatch_raises_incompatible(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    # 0.3.0 vs VERSION 0.4.1 → different minor → incompatible.
    _install_fake_proc(monkeypatch, [_make_initialize_response(server_version="0.3.0")])

    with pytest.raises(IncompatibleBinaryError) as excinfo, Client(binary=fake_binary):
        pass

    msg = str(excinfo.value)
    # Names both versions.
    assert "0.3.0" in msg
    assert VERSION in msg
    # Names the resolution source (constructor arg).
    assert "binary=" in msg
    # Gives concrete remediation steps.
    assert "dotnet tool update" in msg
    assert "pip install --upgrade koshi" in msg


def test_version_patch_drift_is_accepted_with_warning(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    # Same major.minor as VERSION (0.4.1) but patch differs.
    drifted = "0.4.0"
    assert drifted != VERSION
    _install_fake_proc(monkeypatch, [_make_initialize_response(server_version=drifted)])

    captured: list[str] = []
    with Client(binary=fake_binary, log_handler=captured.append) as c:
        assert c.server_version == drifted

    # Compatibility warning routed to log_handler.
    assert any(drifted in line and VERSION in line for line in captured), captured


def test_version_patch_drift_warning_falls_back_to_stderr(
    monkeypatch: pytest.MonkeyPatch,
    fake_binary: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    drifted = "0.4.2"
    assert drifted != VERSION
    _install_fake_proc(monkeypatch, [_make_initialize_response(server_version=drifted)])

    with Client(binary=fake_binary) as c:
        assert c.server_version == drifted

    err = capsys.readouterr().err
    assert drifted in err
    assert VERSION in err


def test_version_mismatch_via_env_mentions_koshi_bin(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path, tmp_path: Path
) -> None:
    # KOSHI_BIN set (fake_binary fixture does this) but no binary= arg → message
    # should call out KOSHI_BIN as the resolution source.
    _install_fake_proc(monkeypatch, [_make_initialize_response(server_version="0.3.0")])

    # Patch resolve_binary so we don't actually try to download / find on PATH;
    # we want the env-source branch, which means no explicit binary= arg.
    monkeypatch.setattr("koshi.client.resolve_binary", lambda: fake_binary)

    with pytest.raises(IncompatibleBinaryError) as excinfo, Client():
        pass

    msg = str(excinfo.value)
    assert "KOSHI_BIN" in msg
    assert "0.3.0" in msg


def test_version_with_git_sha_suffix_is_accepted(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    _install_fake_proc(
        monkeypatch,
        [_make_initialize_response(server_version=f"{VERSION}+abc1234")],
    )
    with Client(binary=fake_binary) as c:
        assert c.server_version == VERSION


def test_missing_server_version_is_tolerated(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    _install_fake_proc(
        monkeypatch,
        [{"result": {"protocolVersion": "2024-11-05", "capabilities": {}, "serverInfo": {}}}],
    )
    with Client(binary=fake_binary) as c:
        assert c.server_version == ""


def test_clear_memories_passes_confirm_flag(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    fake = _install_fake_proc(
        monkeypatch,
        [
            _make_initialize_response(),
            {"result": {"content": [{"type": "text", "text": "✅ Cleared 0 memory(ies)."}]}},
        ],
    )
    with Client(binary=fake_binary) as c:
        c.clear_memories(confirm=True)

    tc = json.loads(fake.stdin_value().splitlines()[2])
    assert tc["params"]["arguments"] == {"confirm": True}


# ─── Integration tests against a real koshi-mcp binary ──────────────────────


def _skip_if_real_binary_incompatible(real_binary: Path) -> None:
    """Skip the @slow integration tests when the installed binary's major.minor
    does not match the dev-checkout ``_manifest.py`` VERSION (e.g. running
    against a globally-installed Koshi.Mcp from a source tree where VERSION
    is the un-injected dev placeholder).
    """
    import subprocess as _sp

    try:
        proc = _sp.run(
            [str(real_binary), "--version"],
            capture_output=True,
            text=True,
            timeout=10,
            check=False,
        )
    except (OSError, _sp.TimeoutExpired) as e:
        pytest.skip(f"Could not query {real_binary} --version: {e}")
    out = (proc.stdout or "") + (proc.stderr or "")
    # Best-effort version extraction: look for the first X.Y.Z token.
    import re as _re

    m = _re.search(r"\b(\d+\.\d+\.\d+)\b", out)
    if m is None:
        return  # binary doesn't expose --version cleanly; let the test proceed
    binary_version = m.group(1)
    if not _is_compatible_version(binary_version, VERSION):
        pytest.skip(
            f"installed koshi-mcp is {binary_version} but this checkout's "
            f"_manifest.py VERSION is {VERSION}; run "
            f"`python scripts/inject-manifest.py` or install matching versions"
        )


@pytest.mark.slow
def test_real_binary_version_round_trip(real_binary: Path) -> None:
    """End-to-end: spawn the actual binary and ask for version. Verifies AOT wiring."""
    _skip_if_real_binary_incompatible(real_binary)
    with Client(binary=real_binary) as c:
        out = c.version()
        assert "Koshi" in out


@pytest.mark.slow
def test_real_binary_remember_recall_round_trip(real_binary: Path) -> None:
    """Memory round-trip: writes a fact, reads it back, then forgets it."""
    _skip_if_real_binary_incompatible(real_binary)
    with Client(binary=real_binary) as c:
        c.remember(content="The pytest secret is rosebud", subject="test-fixture")
        recall_output = c.recall("rosebud")
        assert "rosebud" in recall_output.lower()
        c.forget("test-fixture")
