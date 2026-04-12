# DEVLOG

## 2026-04-11 — Initial implementation

First implementation of `priest-dotnet`, the C# / .NET SDK for the priest protocol.

NuGet package: `Priest`

Implements the priest protocol spec v1.0.0. Reference implementation: Python `priest-core`.

**What's implemented:**
- All three providers: Ollama (NDJSON streaming), OpenAI-compatible (SSE streaming), Anthropic (SSE streaming)
- Session persistence: `InMemorySessionStore` + `SqliteSessionStore` (Microsoft.Data.Sqlite)
- Profile loading: `FilesystemProfileLoader` + built-in default profile
- Context assembly: `ContextBuilder.BuildMessages()` — mirrors `context_builder.py` exactly
- `PriestEngine.RunAsync()` and `StreamAsync()` — full spec-compliant implementations with `IAsyncEnumerable<string>`
- Error types: `PriestException` class + `PriestErrorCode` static constants (values match spec)
- Schema types: all request/response types as C# classes/records; `Session` as a mutable class
- `JsonNode?` (System.Text.Json.Nodes) for heterogeneous JSON — zero external dependencies for this

**Runtime dependency:** `Microsoft.Data.Sqlite` (for `SqliteSessionStore`). All HTTP via `HttpClient`. All JSON via `System.Text.Json`.

**Target frameworks:** net8.0;net10.0

**Test suite:** 30 unit tests (xUnit) — ContextBuilder (8), Engine (7), InMemorySessionStore (4), SqliteSessionStore (4), Streaming (4), and InMemory extras (3).

**Spec version targeted:** 1.0.0 (asserted in `PriestEngine.SpecVersion`).

## 2026-04-12 — v1.0.0 release

- Multi-target: `net8.0;net10.0`
- Namespace fix: `Priest.Profile` → `Priest.Profiles`, `Priest.Session` → `Priest.Sessions` (resolves class/namespace collision)
- Added MIT LICENSE
