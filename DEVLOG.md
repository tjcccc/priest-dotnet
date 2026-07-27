# DEVLOG

## 2026-07-27 — v2.8.0 — OpenAI Responses and provider-neutral reasoning

Synced the TypeScript reference and canonical protocol 2.8.0 behavior without changing the SQLite schema, timestamp representation, session persistence, or existing OpenAI-compatible Chat Completions adapter.

- Added `OpenAIResponsesProvider` with configurable base/exact URL, headers, and host-owned `HttpClient`; stateless message/tool continuation; JSON output formats; function tools; semantic SSE; finish/error normalization; and duplicate tool-terminal suppression.
- Added `ReasoningConfig`, safe `ReasoningInfo` summaries, opaque request-local continuation state, `reasoning_summary_delta`, `UsageInfo.ReasoningTokens`, and `FinishedReason.ContentFilter`.
- Mapped neutral reasoning controls to OpenAI Responses, Anthropic, and Ollama. Raw Ollama traces and raw Responses reasoning content are never surfaced. `ToolLoop` carries recognized opaque state between tool iterations; durable sessions remain text-only.
- Added focused v2.8 wire, safety, streaming, usage, and tool-loop regression coverage. `dotnet test Priest.slnx --no-restore`: 79 passed.
- Known pre-existing gap: the .NET common request model still does not implement protocol image inputs.

## 2026-06-27 — v2.6.1 — full spec sync (compaction, turn window, cached tokens, streaming usage)

Brings Priest to full parity with the spec at v2.6.1 (2.5.0 → 2.6.0 → 2.6.1), mirroring the priest-core/priest-typescript reference. All additions are off/opt-in by default; the SQLite schema is unchanged, so pre-2.5 sessions remain interoperable.

- **Cached input tokens (spec 2.5.0):** `AdapterResult.CachedInputTokens` / `UsageInfo.CachedInputTokens` and the `usage` stream event. Parsed from OpenAI-compat `usage.prompt_tokens_details.cached_tokens` and Anthropic `usage.cache_read_input_tokens` (complete + stream). Null when omitted.
- **Conversation compaction (spec 2.5.0):** new `Engine/Compactor.cs` (`ShouldCompact`, `PlanCompaction`, `BuildSummaryMessages`; ratio 0.8, default keep 6, summary cap 1024). `PriestConfig.MaxContextTokens` enables it; a chat turn crossing 80% of the budget folds older turns into a running summary and replays only `summary + recent tail`. State persists in session `Metadata["__compaction"]` with **camelCase keys** (cross-SDK contract, `Session.cs`). `engine.CompactSessionAsync()` for a manual `/compact`; trigger measured on clean chat turns only (tool-exchange replays skipped).
- **Session turn window (spec 2.6.0):** `PriestConfig.SessionContextTurns` caps replayed turns; the context builder windows from `Max(SummarizedThrough, Count-N)` and snaps an odd window down to a user turn.
- **OpenAI-compat streaming usage (spec 2.6.1):** streaming requests send `stream_options: {include_usage: true}` (overridable via `ProviderOptions`). `BuildBody` is `internal` (`InternalsVisibleTo`) for direct wire assertions.
- `SpecVersion` → "2.6.1"; csproj + README spec references bumped to v2.6.1 (README was previously stale at v2.3.0).
- Tests: `tests/Priest.Tests/CompactionTests.cs` (18 — incl. a SQLite round-trip asserting the persisted `__compaction` camelCase bytes) plus the existing wire tests. `dotnet test` green (71 total).

## 2026-06-12 — v2.4.0 — tool calling, structured streaming (spec 2.4.0 sync)

Syncs the spec 2.4.0 features (reference: priest-typescript / Python priest-core 2.4.0).

- **Tool calling (caller executes):** `PriestRequest.Tools` / `ToolChoice` / `ToolExchange`, `PriestResponse.ToolCalls`, `FinishedReason.ToolCalls`. Wire mappings for all three providers (OpenAI tools with JSON-string arguments, Anthropic tool_use/tool_result with merged user messages, Ollama tools with synthesized `call_N` ids and `tool_name` results). Tool exchange turns are never persisted in sessions.
- **`ToolLoop.RunWithToolsAsync()`:** generic call → execute → re-call loop with caller executor, optional approval hook, iteration cap, and exchange trace.
- **`PriestEngine.StreamEventsAsync()`:** structured streaming (`text_delta`, `tool_call_start/delta/end`, `usage`, `done` with full `PriestResponse`); adapters without native event streaming fall back via the default interface method; `StreamAsync()` reimplemented as a filter over it.
- **Cancellation:** the existing `CancellationToken` parameters map to the spec's cancellation concept; caller-initiated cancellation now surfaces as `REQUEST_ABORTED`, distinct from `PROVIDER_TIMEOUT`. `IMAGE_LOAD_ERROR` code added for table parity.
- Ollama `CompleteAsync` is now a real non-streaming call (reports usage and `done_reason`).
- Anthropic default `max_tokens` corrected to the spec-defined 8096 (was 1024).
- `PriestEngine.SpecVersion` → "2.4.0". Tests: 51 (7 new).

Known gap: multimodal `ImageInput` (spec 2.0) is still not implemented in this SDK.

---

## 2026-05-08 — v2.3.0 — optional profile memory loading

- Added `FilesystemProfileLoader(baseDir, includeMemories: false)` so host apps can load profile identity/rules/custom fields without injecting profile memories
- When memory loading is disabled, JSON profile `memories` arrays are ignored and callers can pass app-selected memory through `PriestRequest.Memory`
- Updated `PriestEngine.SpecVersion` and package version to `2.3.0`

---

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
