"""Exception hierarchy for the koshi Python client."""

from __future__ import annotations


class KoshiError(Exception):
    """Base class for all koshi errors."""


class BinaryNotFoundError(KoshiError):
    """No suitable koshi-mcp binary was found on this system."""


class IncompatibleBinaryError(KoshiError):
    """The resolved binary's version does not match this koshi package.

    Raised when:
      * KOSHI_BIN points to a binary whose `serverInfo.version` differs from
        koshi.__version__.
      * A binary found on PATH (e.g. an old `dotnet tool install` of
        Koshi.Mcp) has a mismatched version.

    The Python package and the koshi-mcp binary version must match exactly.
    """


class BinaryCorruptedError(KoshiError):
    """A downloaded binary's SHA-256 hash did not match the expected hash."""


class UnsupportedPlatformError(KoshiError):
    """No AOT artifact is published for this OS/CPU combination."""


class ProtocolError(KoshiError):
    """Received an unexpected or malformed message from the koshi-mcp server."""


class ToolError(KoshiError):
    """The MCP server returned an error response for a tools/call request."""

    def __init__(self, tool: str, message: str, raw: object | None = None) -> None:
        self.tool = tool
        self.message = message
        self.raw = raw
        super().__init__(f"{tool}: {message}")


class KoshiTimeoutError(KoshiError):
    """A request to the koshi-mcp server timed out before a response arrived."""
