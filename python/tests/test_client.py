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
    _install_fake_proc(monkeypatch, [_make_initialize_response(server_version="0.3.0")])

    with pytest.raises(IncompatibleBinaryError, match=r"0\.3\.0"), Client(binary=fake_binary):
        pass


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


def test_capture_turn_omits_optional_args_when_unset(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    fake = _install_fake_proc(
        monkeypatch,
        [
            _make_initialize_response(),
            {"result": {"content": [{"type": "text", "text": "✅ Captured 0 decision(s)."}]}},
        ],
    )
    with Client(binary=fake_binary) as c:
        c.capture_turn(turn_summary="Decided to use Postgres for the auth store.")

    tc = json.loads(fake.stdin_value().splitlines()[2])
    assert tc["params"]["name"] == "koshi_capture_turn"
    args = tc["params"]["arguments"]
    assert args == {
        "turn_summary": "Decided to use Postgres for the auth store.",
        "linked_pr": 0,
        "auto_promote": True,
        "max_candidates": 5,
        "min_confidence": 0.5,
    }
    # None-valued optional args are omitted entirely.
    assert "linked_commits" not in args
    assert "userId" not in args
    assert "workspaceId" not in args
    assert "threadId" not in args


def test_capture_turn_passes_full_argument_set(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    fake = _install_fake_proc(
        monkeypatch,
        [
            _make_initialize_response(),
            {"result": {"content": [{"type": "text", "text": "✅ Captured 1 decision(s)."}]}},
        ],
    )
    with Client(binary=fake_binary) as c:
        c.capture_turn(
            turn_summary="Decision: switch cache to Redis.",
            linked_pr=1234,
            linked_commits="abc123,def456",
            auto_promote=False,
            max_candidates=10,
            min_confidence=0.7,
            user_id="alice",
            workspace_id="acme",
            thread_id="thread-7",
        )

    tc = json.loads(fake.stdin_value().splitlines()[2])
    assert tc["params"]["name"] == "koshi_capture_turn"
    # Server-side parameter names are snake_case for the turn fields and
    # camelCase for the scoping fields; the wire must match exactly.
    assert tc["params"]["arguments"] == {
        "turn_summary": "Decision: switch cache to Redis.",
        "linked_pr": 1234,
        "linked_commits": "abc123,def456",
        "auto_promote": False,
        "max_candidates": 10,
        "min_confidence": 0.7,
        "userId": "alice",
        "workspaceId": "acme",
        "threadId": "thread-7",
    }


def test_memory_export_to_vault_passes_arguments(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    fake = _install_fake_proc(
        monkeypatch,
        [
            _make_initialize_response(),
            {"result": {"content": [{"type": "text", "text": "✅ Exported 7 memories."}]}},
        ],
    )
    with Client(binary=fake_binary) as c:
        c.memory_export_to_vault(
            vault_path="/tmp/vault", overwrite=True, flavor="foam"
        )

    tc = json.loads(fake.stdin_value().splitlines()[2])
    assert tc["params"]["name"] == "koshi_memory_export_to_vault"
    assert tc["params"]["arguments"] == {
        "vaultPath": "/tmp/vault",
        "overwrite": True,
        "flavor": "foam",
    }


def test_memory_import_from_vault_defaults(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    fake = _install_fake_proc(
        monkeypatch,
        [
            _make_initialize_response(),
            {"result": {"content": [{"type": "text", "text": "✅ Imported 3 memories."}]}},
        ],
    )
    with Client(binary=fake_binary) as c:
        c.memory_import_from_vault(vault_path="/tmp/vault")

    tc = json.loads(fake.stdin_value().splitlines()[2])
    assert tc["params"]["name"] == "koshi_memory_import_from_vault"
    assert tc["params"]["arguments"] == {
        "vaultPath": "/tmp/vault",
        "mode": "merge",
        "flavor": "obsidian",
    }


def test_memory_sync_vault_passes_no_arguments(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    fake = _install_fake_proc(
        monkeypatch,
        [
            _make_initialize_response(),
            {"result": {"content": [{"type": "text", "text": "✅ Reloaded vault."}]}},
        ],
    )
    with Client(binary=fake_binary) as c:
        c.memory_sync_vault()

    tc = json.loads(fake.stdin_value().splitlines()[2])
    assert tc["params"]["name"] == "koshi_memory_sync_vault"
    assert tc["params"]["arguments"] == {}


def test_call_tool_adds_koshi_prefix_and_forwards_arguments(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    fake = _install_fake_proc(
        monkeypatch,
        [
            _make_initialize_response(),
            {"result": {"content": [{"type": "text", "text": "ok"}]}},
        ],
    )
    with Client(binary=fake_binary) as c:
        c.call_tool("search", query="auth", topK=3)

    tc = json.loads(fake.stdin_value().splitlines()[2])
    assert tc["params"]["name"] == "koshi_search"
    assert tc["params"]["arguments"] == {"query": "auth", "topK": 3}


def test_call_tool_preserves_already_prefixed_name(
    monkeypatch: pytest.MonkeyPatch, fake_binary: Path
) -> None:
    fake = _install_fake_proc(
        monkeypatch,
        [
            _make_initialize_response(),
            {"result": {"content": [{"type": "text", "text": "ok"}]}},
        ],
    )
    with Client(binary=fake_binary) as c:
        c.call_tool("koshi_health")

    tc = json.loads(fake.stdin_value().splitlines()[2])
    assert tc["params"]["name"] == "koshi_health"
    assert tc["params"]["arguments"] == {}


def test_call_tool_rejects_empty_name() -> None:
    c = Client(binary=Path("/does/not/exist"))
    with pytest.raises(ValueError, match="tool name must not be empty"):
        c.call_tool("")


def test_all_four_previously_missing_tools_are_exposed() -> None:
    """Regression guard for #76 — these four wrappers must remain on Client."""
    required = (
        "capture_turn",
        "memory_export_to_vault",
        "memory_import_from_vault",
        "memory_sync_vault",
    )
    for name in required:
        assert hasattr(Client, name), f"Client.{name} missing — see #76"
        assert callable(getattr(Client, name)), f"Client.{name} is not callable"


# ─── Integration tests against a real koshi-mcp binary ──────────────────────


@pytest.mark.slow
def test_real_binary_version_round_trip(real_binary: Path) -> None:
    """End-to-end: spawn the actual binary and ask for version. Verifies AOT wiring."""
    with Client(binary=real_binary) as c:
        out = c.version()
        assert "Koshi" in out


@pytest.mark.slow
def test_real_binary_remember_recall_round_trip(real_binary: Path) -> None:
    """Memory round-trip: writes a fact, reads it back, then forgets it."""
    with Client(binary=real_binary) as c:
        c.remember(content="The pytest secret is rosebud", subject="test-fixture")
        recall_output = c.recall("rosebud")
        assert "rosebud" in recall_output.lower()
        c.forget("test-fixture")
