"""
koshi — Python client for the Koshi MCP server.

Quickstart::

    from koshi import Client

    with Client() as koshi:
        print(koshi.version())
        koshi.index_directory("/path/to/repo")
        print(koshi.search("auth pattern", top_k=5))

The first call auto-downloads the matching native AOT binary (~15 MB)
into your user cache. No .NET install required. Set ``KOSHI_BIN`` to
override the resolver with an explicit path (e.g. an air-gapped
deployment, or a build of your own).
"""

from __future__ import annotations

from ._manifest import VERSION as __version__
from .client import Client
from .errors import (
    BinaryCorruptedError,
    BinaryNotFoundError,
    IncompatibleBinaryError,
    KoshiError,
    KoshiTimeoutError,
    ProtocolError,
    ToolError,
    UnsupportedPlatformError,
)

__all__ = [
    "BinaryCorruptedError",
    "BinaryNotFoundError",
    "Client",
    "IncompatibleBinaryError",
    "KoshiError",
    "KoshiTimeoutError",
    "ProtocolError",
    "ToolError",
    "UnsupportedPlatformError",
    "__version__",
]
