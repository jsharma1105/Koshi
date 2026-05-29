"""
Synchronous MCP client for the koshi-mcp server.

Spawns the koshi-mcp binary as a subprocess, performs the JSON-RPC 2.0
``initialize`` / ``notifications/initialized`` handshake, then exposes
one Python method per Koshi MCP tool. Every tool method is a thin
wrapper around the MCP ``tools/call`` envelope — the wire format is
fixed and asserted in the test suite.

Threading: each ``Client`` is safe for use from a single thread (or
under a user-supplied lock). The internal lock serialises request /
response correlation; it is not a re-entrant lock.
"""

from __future__ import annotations

import contextlib
import json
import os
import subprocess
import sys
import threading
import time
from collections.abc import Callable, Mapping
from pathlib import Path
from types import TracebackType
from typing import Any

from ._manifest import VERSION
from .binary import resolve_binary
from .errors import (
    IncompatibleBinaryError,
    KoshiError,
    KoshiTimeoutError,
    ProtocolError,
    ToolError,
)

__all__ = ["Client"]

_LogHandler = Callable[[str], None]


def _parse_version(v: str) -> tuple[int, int, int] | None:
    """Best-effort parse of ``X.Y.Z[-pre][+meta]`` into ``(major, minor, patch)``.

    Returns ``None`` if the input does not look like a semver-ish string we
    can compare. We deliberately do not depend on ``packaging.version`` so
    the package stays stdlib-only.
    """
    if not v:
        return None
    core = v.split("+", 1)[0].split("-", 1)[0]
    parts = core.split(".")
    if len(parts) < 2:
        return None
    try:
        major = int(parts[0])
        minor = int(parts[1])
        patch = int(parts[2]) if len(parts) >= 3 else 0
    except ValueError:
        return None
    return major, minor, patch


def _is_compatible_version(server: str, client: str) -> bool:
    """True iff ``server`` and ``client`` share the same major and minor.

    Patch drift is treated as wire-compatible; any major or minor difference
    is treated as potentially MCP-surface-breaking and rejected.
    """
    s = _parse_version(server)
    c = _parse_version(client)
    if s is None or c is None:
        return False
    return s[0] == c[0] and s[1] == c[1]


def _format_incompatible_message(
    *,
    server_version: str,
    client_version: str,
    binary_path: Path | None,
    explicit_via_arg: bool,
    explicit_via_env: bool,
) -> str:
    """Return a multi-line, actionable ``IncompatibleBinaryError`` message."""
    if explicit_via_arg:
        source = "the `binary=` argument passed to Client()"
    elif explicit_via_env:
        env_value = os.environ.get("KOSHI_BIN", "")
        source = f"KOSHI_BIN={env_value!r}"
    else:
        source = "auto-resolution (versioned cache / PATH / GitHub release download)"

    server_core = server_version.split("+", 1)[0]
    lines = [
        f"koshi-mcp at {binary_path} reports version '{server_version}', but the "
        f"koshi Python package is {client_version} and requires the same major.minor "
        f"(got {server_core}, expected {client_version}).",
        "",
        f"Binary was resolved via: {source}.",
        "",
        "Fix one of the following:",
        f"  • Upgrade the binary:    dotnet tool update --global Koshi.Mcp --version {client_version}",
        f"  • Downgrade the Python:  pip install --upgrade koshi=={server_core}",
    ]
    if explicit_via_env:
        lines.append(
            "  • Or unset KOSHI_BIN so koshi can auto-download the matching binary"
        )
    elif not explicit_via_arg:
        lines.append(
            "  • Or remove the stale `dotnet tool install` from PATH so koshi can "
            "auto-download the matching version"
        )
    return "\n".join(lines)


class Client:
    """Stdio MCP client bound to a single koshi-mcp subprocess.

    Use as a context manager::

        with koshi.Client() as c:
            print(c.version())
            c.index_directory("/path/to/repo")
            print(c.search("auth pattern"))

    Or manage lifecycle manually::

        c = koshi.Client()
        c.open()
        try:
            print(c.health())
        finally:
            c.close()
    """

    def __init__(
        self,
        binary: str | Path | None = None,
        log_handler: _LogHandler | None = None,
        timeout: float = 15.0,
    ) -> None:
        self._binary_arg: Path | None = Path(binary) if binary is not None else None
        self._log_handler = log_handler
        self.timeout = timeout

        self._binary: Path | None = None
        self._proc: subprocess.Popen[str] | None = None
        self._next_id = 0
        self._lock = threading.Lock()
        self._stderr_thread: threading.Thread | None = None
        self._server_version: str | None = None

    # ─── lifecycle ───────────────────────────────────────────────────────

    def __enter__(self) -> Client:
        self.open()
        return self

    def __exit__(
        self,
        exc_type: type[BaseException] | None,
        exc: BaseException | None,
        tb: TracebackType | None,
    ) -> None:
        self.close()

    def open(self) -> None:
        if self._proc is not None:
            return

        self._binary = self._binary_arg or resolve_binary()

        # text=True + line buffering means every JSON document is a single
        # readline()/write() call. Koshi-mcp emits one document per line.
        self._proc = subprocess.Popen(
            [str(self._binary)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
            encoding="utf-8",
        )

        self._stderr_thread = threading.Thread(target=self._drain_stderr, daemon=True)
        self._stderr_thread.start()

        try:
            init = self._rpc(
                "initialize",
                {
                    "protocolVersion": "2024-11-05",
                    "capabilities": {},
                    "clientInfo": {"name": "koshi-py", "version": VERSION},
                },
            )
        except KoshiError:
            self.close()
            raise

        server_info = init.get("serverInfo") or {}
        raw_version = str(server_info.get("version", ""))
        # Strip "+<git-sha>" if present (InformationalVersionAttribute appends it).
        version_core = raw_version.split("+", 1)[0]
        self._server_version = version_core

        if version_core and version_core != VERSION:
            if _is_compatible_version(version_core, VERSION):
                # Same major.minor; only patch (or pre-release tag) differs.
                # Treat as wire-compatible. Surface a one-line warning so
                # the drift is visible without crashing the session.
                msg = (
                    f"koshi: server version {version_core} differs from client "
                    f"{VERSION} (same major.minor — wire-compatible). "
                    f"Consider aligning them."
                )
                if self._log_handler is not None:
                    with contextlib.suppress(Exception):
                        self._log_handler(msg)
                else:
                    sys.stderr.write(msg + "\n")
            else:
                # Major or minor differs; MCP surface may have changed. Fail
                # loud with a message that names both versions, the resolved
                # binary path, and concrete remediation steps.
                binary_path = self._binary
                explicit_via_arg = self._binary_arg is not None
                explicit_via_env = "KOSHI_BIN" in os.environ
                self.close()
                raise IncompatibleBinaryError(
                    _format_incompatible_message(
                        server_version=raw_version,
                        client_version=VERSION,
                        binary_path=binary_path,
                        explicit_via_arg=explicit_via_arg,
                        explicit_via_env=explicit_via_env,
                    )
                )

        self._notify("notifications/initialized")

    def close(self) -> None:
        proc = self._proc
        if proc is None:
            return
        self._proc = None
        try:
            if proc.stdin is not None:
                with contextlib.suppress(OSError):
                    proc.stdin.close()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()
                with contextlib.suppress(subprocess.TimeoutExpired):
                    proc.wait(timeout=2)
        finally:
            self._server_version = None

    # ─── transport ──────────────────────────────────────────────────────

    def _drain_stderr(self) -> None:
        proc = self._proc
        if proc is None or proc.stderr is None:
            return
        try:
            for line in proc.stderr:
                line = line.rstrip("\n")
                if self._log_handler is not None:
                    # Never let a user log handler crash the drainer.
                    with contextlib.suppress(Exception):
                        self._log_handler(line)
        except (ValueError, OSError):
            return

    def _ensure_open(self) -> subprocess.Popen[str]:
        if self._proc is None:
            raise ProtocolError("Client is not open. Call .open() or use `with` statement.")
        return self._proc

    def _send_line(self, payload: Mapping[str, Any]) -> None:
        proc = self._ensure_open()
        if proc.stdin is None:
            raise ProtocolError("subprocess stdin is unexpectedly None")
        proc.stdin.write(json.dumps(payload, separators=(",", ":")) + "\n")
        proc.stdin.flush()

    def _rpc(self, method: str, params: Mapping[str, Any] | None = None) -> dict[str, Any]:
        with self._lock:
            self._next_id += 1
            req_id = self._next_id
            payload: dict[str, Any] = {"jsonrpc": "2.0", "id": req_id, "method": method}
            if params is not None:
                payload["params"] = params

            self._send_line(payload)

            proc = self._ensure_open()
            assert proc.stdout is not None

            deadline = time.monotonic() + self.timeout
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise KoshiTimeoutError(
                        f"No response to '{method}' within {self.timeout:.1f}s"
                    )

                line = proc.stdout.readline()
                if not line:
                    raise ProtocolError(
                        f"koshi-mcp closed stdout while waiting for '{method}' response"
                    )

                line = line.strip()
                if not line:
                    continue

                try:
                    msg = json.loads(line)
                except json.JSONDecodeError as e:
                    raise ProtocolError(
                        f"Non-JSON line on stdout while waiting for '{method}': {line!r}"
                    ) from e

                if msg.get("id") != req_id:
                    # Stray notification or out-of-order reply; ignore and keep reading.
                    continue

                if "error" in msg:
                    err = msg["error"]
                    raise ProtocolError(
                        f"{method}: {err.get('message', err)} (code {err.get('code')})"
                    )

                return msg.get("result") or {}

    def _notify(self, method: str, params: Mapping[str, Any] | None = None) -> None:
        with self._lock:
            payload: dict[str, Any] = {"jsonrpc": "2.0", "method": method}
            if params is not None:
                payload["params"] = params
            self._send_line(payload)

    def _tool_call(self, tool: str, arguments: Mapping[str, Any] | None = None) -> str:
        result = self._rpc("tools/call", {"name": tool, "arguments": dict(arguments or {})})
        content = result.get("content") or []
        text = ""
        if content and isinstance(content, list):
            first = content[0]
            if isinstance(first, dict):
                text = str(first.get("text", ""))

        is_error = bool(result.get("isError"))
        if is_error or text.startswith("❌"):
            raise ToolError(tool, text or "tool returned an error", raw=result)
        return text

    # ─── server metadata ────────────────────────────────────────────────

    @property
    def server_version(self) -> str | None:
        """Version reported by the koshi-mcp server during initialize (or None)."""
        return self._server_version

    @property
    def binary_path(self) -> Path | None:
        """Filesystem path of the koshi-mcp binary actually spawned."""
        return self._binary

    # ─── Retrieval (5) ──────────────────────────────────────────────────

    def index(self, documents: list[Mapping[str, Any]]) -> str:
        """Index a list of in-memory documents for BM25 retrieval."""
        return self._tool_call("koshi_index", {"documents": json.dumps(documents)})

    def index_directory(
        self,
        path: str | None = None,
        pattern: str | None = None,
        max_file_size_kb: int = 256,
        max_files: int = 5000,
    ) -> str:
        """Recursively index supported text files under ``path``."""
        args: dict[str, Any] = {"maxFileSizeKb": max_file_size_kb, "maxFiles": max_files}
        if path is not None:
            args["path"] = path
        if pattern is not None:
            args["pattern"] = pattern
        return self._tool_call("koshi_index_directory", args)

    def search(self, query: str, top_k: int = 5) -> str:
        """Search the indexed corpus with BM25; return the top ``top_k`` chunks."""
        return self._tool_call("koshi_search", {"query": query, "topK": top_k})

    def list_indexed(self) -> str:
        """List all indexed documents grouped by source."""
        return self._tool_call("koshi_list_indexed")

    def clear_index(self) -> str:
        """Clear the indexed corpus."""
        return self._tool_call("koshi_clear_index")

    # ─── Memory (5) ─────────────────────────────────────────────────────

    def remember(
        self,
        content: str,
        subject: str,
        type: str = "Fact",
        confidence: float = 0.8,
        source: str = "user",
    ) -> str:
        """Store a fact / decision / pattern / preference in memory."""
        return self._tool_call(
            "koshi_remember",
            {
                "content": content,
                "subject": subject,
                "type": type,
                "confidence": confidence,
                "source": source,
            },
        )

    def recall(self, query: str, type: str = "All", top_k: int = 5) -> str:
        """Recall memories relevant to ``query``."""
        return self._tool_call(
            "koshi_recall",
            {"query": query, "type": type, "topK": top_k},
        )

    def memory_stats(self) -> str:
        """Return summary stats about the memory store."""
        return self._tool_call("koshi_memory_stats")

    def forget(self, subject: str) -> str:
        """Remove all memories with the given subject (case-insensitive)."""
        return self._tool_call("koshi_forget", {"subject": subject})

    def clear_memories(self, confirm: bool = False) -> str:
        """Clear ALL stored memories. Pass ``confirm=True`` to actually delete."""
        return self._tool_call("koshi_clear_memories", {"confirm": confirm})

    # ─── Context (3) ────────────────────────────────────────────────────

    def compile_context(
        self,
        system_prompt: str,
        user_query: str,
        retrieved_content: str | None = None,
        memories: str | None = None,
        team_context: str | None = None,
        token_budget: int = 8192,
        strategy: str = "CacheOptimized",
    ) -> str:
        """Compile a context bundle suitable for an LLM call."""
        args: dict[str, Any] = {
            "systemPrompt": system_prompt,
            "userQuery": user_query,
            "tokenBudget": token_budget,
            "strategy": strategy,
        }
        if retrieved_content is not None:
            args["retrievedContent"] = retrieved_content
        if memories is not None:
            args["memories"] = memories
        if team_context is not None:
            args["teamContext"] = team_context
        return self._tool_call("koshi_compile_context", args)

    def token_count(self, text: str) -> str:
        """Count GPT-4 (cl100k) tokens in ``text``."""
        return self._tool_call("koshi_token_count", {"text": text})

    def budget_plan(
        self,
        total_budget: int = 8192,
        system_prompt: str | None = None,
        team_context: str | None = None,
    ) -> str:
        """Plan a token budget across system / retrieval / memory / history."""
        args: dict[str, Any] = {"totalBudget": total_budget}
        if system_prompt is not None:
            args["systemPrompt"] = system_prompt
        if team_context is not None:
            args["teamContext"] = team_context
        return self._tool_call("koshi_budget_plan", args)

    # ─── Team / Quality (5) ─────────────────────────────────────────────

    def register_team(
        self,
        team_id: str,
        name: str,
        description: str | None = None,
        token_budget: int = 8192,
        top_k: int = 5,
        quality_target: float = 0.7,
        system_prompt: str | None = None,
        team_context: str | None = None,
    ) -> str:
        """Register a team for quality scoring."""
        args: dict[str, Any] = {
            "teamId": team_id,
            "name": name,
            "tokenBudget": token_budget,
            "topK": top_k,
            "qualityTarget": quality_target,
        }
        if description is not None:
            args["description"] = description
        if system_prompt is not None:
            args["systemPrompt"] = system_prompt
        if team_context is not None:
            args["teamContext"] = team_context
        return self._tool_call("koshi_register_team", args)

    def score_turn(
        self,
        team_id: str,
        retrieved_chunks: int = 0,
        memories_recalled: int = 0,
        budget_utilization: float = 0.5,
        cache_ratio: float = 0.0,
        latency_ms: int = 3000,
        user_rating: int = 0,
        issues: str | None = None,
    ) -> str:
        """Score a single turn and update the team's running quality score."""
        args: dict[str, Any] = {
            "teamId": team_id,
            "retrievedChunks": retrieved_chunks,
            "memoriesRecalled": memories_recalled,
            "budgetUtilization": budget_utilization,
            "cacheRatio": cache_ratio,
            "latencyMs": latency_ms,
            "userRating": user_rating,
        }
        if issues is not None:
            args["issues"] = issues
        return self._tool_call("koshi_score_turn", args)

    def team_dashboard(self, team_id: str) -> str:
        """Render the quality dashboard for a team."""
        return self._tool_call("koshi_team_dashboard", {"teamId": team_id})

    def analyze_feedback(self, team_id: str) -> str:
        """Analyse user feedback trends for a team."""
        return self._tool_call("koshi_analyze_feedback", {"teamId": team_id})

    def list_teams(self) -> str:
        """List all registered teams."""
        return self._tool_call("koshi_list_teams")

    # ─── Diagnostics (2) ────────────────────────────────────────────────

    def version(self) -> str:
        """Report Koshi MCP server version, .NET runtime, OS, and uptime."""
        return self._tool_call("koshi_version")

    def health(self) -> str:
        """Report runtime health: indexed corpus, memory, persistence, uptime."""
        return self._tool_call("koshi_health")


if sys.version_info < (3, 10):  # pragma: no cover  # noqa: UP036
    raise RuntimeError("koshi requires Python 3.10 or newer.")
