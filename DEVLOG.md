# DEVLOG

## 2026-04-25 — v2.2.0 — json_schema structured output

Added `JsonSchema`, `JsonSchemaName`, and `JsonSchemaStrict` to `OutputSpec`.

- **OpenAI-compat:** `response_format:{type:"json_schema", json_schema:{name, schema, strict}}` wired in `BuildBody`.
- **Ollama (v0.5+):** `format:<schema_dict>` via `JsonSchema.DeepClone()`.
- **Anthropic:** schema description injected into system message in `BuildBody`; `outputSpec` now threaded through `CompleteAsync` and `StreamAsync`.
- `JsonSchema` takes precedence over `ProviderFormat` when both are set.
- `PriestEngine.SpecVersion` → `"2.2.0"`

---

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

## 2026-04-20 — v2.0.0 — context API redesign, memory dedup/trim, profile cache

Breaking changes matching priest core v2.0.0 spec.

**Schema changes:**
- `PriestRequest.SystemContext` → `Context` (raw system context, passed through untouched)
- `PriestRequest.ExtraContext` → `UserContext` (appended to user turn)
- `PriestRequest.Memory` added — dynamic memory entries, deduped and trimmable
- `PriestConfig.MaxSystemChars` added — triggers tail-trim when set

**Context assembly (`ContextBuilder.BuildMessages`):**
- Dynamic memory rendered under `## Memory\n\n` heading (after `## Loaded Memories\n\n`)
- Dedup: whitespace-stripped comparison; drops any `Memory` entry matching a profile memory or earlier dynamic entry
- Trim: tail-first on `Memory`, then `profile.Memories`; `Context`/rules/identity/custom/format instructions never trimmed

**Profile loader cache:**
- `FilesystemProfileLoader` now caches loaded profiles per instance, keyed on `File.GetLastWriteTimeUtc`
- Invalidates automatically when the file changes

**Test suite:** 37 unit tests (up from 30). New tests cover memory block rendering, cross-source dedup, self-dedup, whitespace-stripped dedup, tail-trim, and no-trim guard.

**Spec version:** `PriestEngine.SpecVersion` → `"2.0.0"`
