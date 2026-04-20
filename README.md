# Priest

C# / .NET SDK for the [priest](https://github.com/tjcccc/priest) AI orchestration protocol.

.NET 8+ · C# · One dependency (`Microsoft.Data.Sqlite` for SQLite sessions)

---

## Overview

`Priest` is a .NET class library that implements the priest protocol spec v2.0.0 natively — no Python server, no FFI. It is designed for .NET backends, Unity games, Godot projects, and any C# host that needs to talk to a local or remote AI provider.

The core API is two methods on `PriestEngine`:

| Method | Returns | Use when |
|--------|---------|----------|
| `RunAsync(request)` | `Task<PriestResponse>` | You need structured metadata (usage, latency, session info) |
| `StreamAsync(request)` | `IAsyncEnumerable<string>` | You want to yield text as it arrives |

---

## Installation

```bash
dotnet add package Priest
```

---

## Quick Start

### Single run with Ollama

```csharp
using Priest.Engine;
using Priest.Profiles;
using Priest.Providers;
using Priest.Schema;

var engine = new PriestEngine(
    profileLoader: new FilesystemProfileLoader("./profiles"),
    adapters: new Dictionary<string, IProviderAdapter>
    {
        ["ollama"] = new OllamaProvider("http://localhost:11434"),
    }
);

var response = await engine.RunAsync(new PriestRequest(
    config: new PriestConfig("ollama", "llama3.2"),
    prompt: "What is the capital of France?"
));

if (response.Ok)
    Console.WriteLine(response.Text);
```

### Streaming

```csharp
await foreach (var chunk in engine.StreamAsync(new PriestRequest(
    config: new PriestConfig("ollama", "llama3.2"),
    prompt: "Tell me a story."
)))
{
    Console.Write(chunk);
}
```

### Anthropic or OpenAI-compatible providers

```csharp
var engine = new PriestEngine(
    profileLoader: new FilesystemProfileLoader("./profiles"),
    adapters: new Dictionary<string, IProviderAdapter>
    {
        ["anthropic"] = new AnthropicProvider("sk-ant-..."),
        ["openai"]    = new OpenAICompatProvider("https://api.openai.com", "sk-..."),
    }
);

var response = await engine.RunAsync(new PriestRequest(
    config: new PriestConfig("anthropic", "claude-opus-4-6"),
    prompt: "Summarize the priest protocol in one sentence."
));
```

---

## Session Continuity

Pass a `Session` property to persist conversation history across calls.

```csharp
using Priest.Sessions;

using var store = new SqliteSessionStore("./sessions.db");
store.Open();

var engine = new PriestEngine(
    profileLoader: new FilesystemProfileLoader("./profiles"),
    sessionStore: store,
    adapters: new Dictionary<string, IProviderAdapter>
    {
        ["ollama"] = new OllamaProvider(),
    }
);

var config = new PriestConfig("ollama", "llama3.2");
var sessionId = "user-123-chat";

// First turn — session is created automatically
await engine.RunAsync(new PriestRequest(config, "My name is Alex.")
{
    Session = new SessionRef(sessionId),
});

// Second turn — session is continued
var r = await engine.RunAsync(new PriestRequest(config, "What is my name?")
{
    Session = new SessionRef(sessionId),
});
// r.Text → "Your name is Alex."
```

`SessionRef` behavior:

| `ContinueExisting` | `CreateIfMissing` | Result |
|--------------------|-------------------|--------|
| `true` (default) | `true` (default) | Load existing session or create it |
| `true` | `false` | Load existing or throw `SESSION_NOT_FOUND` |
| `false` | — | Always create a new session |

The SQLite store is interoperable with the Python `priest` `SqliteSessionStore` and the Swift/TypeScript SDKs — the schema and timestamp format are identical across all implementations.

---

## Profiles

A profile supplies `identity`, `rules`, and optional `custom` and `memories` that shape the system prompt.

```
profiles/
├── default.json
└── coder.json
```

```csharp
var loader = new FilesystemProfileLoader("./profiles");
```

Falls back to the built-in default profile when the named file is not found.

Profile format — `default.json`:

```json
{
  "identity": "You are a helpful assistant.",
  "rules": "Be honest. Do not make things up.\nBe concise unless the user asks for depth.",
  "memories": []
}
```

---

## Memory and Context

```csharp
var response = await engine.RunAsync(new PriestRequest(config, "What should I work on today?")
{
    // Raw system context — injected first, never trimmed or deduped
    Context = ["Today is Monday. App: ProjectManager"],

    // Dynamic memory — deduped against profile memories and each other
    Memory = ["User prefers bullet points.", "Active sprint: v3.0"],

    // Per-turn user context — appended to the user message
    UserContext = ["Recent tasks: [fix login bug, update README]"],
});
```

When `MaxSystemChars` is set on the config, the engine trims `Memory` entries tail-first, then `profile.Memories` tail-first. `Context`, rules, identity, custom, and format instructions are never trimmed.

```csharp
var response = await engine.RunAsync(new PriestRequest(
    new PriestConfig("ollama", "llama3.2") { MaxSystemChars = 4096 },
    "Summarize my notes."
) { Memory = longMemoryList });
```

---

## Output Format Hints

```csharp
var response = await engine.RunAsync(new PriestRequest(config, "List three planets as JSON.")
{
    Output = new OutputSpec
    {
        ProviderFormat = ProviderFormat.Json,
        PromptFormat   = PromptFormat.Json,
    },
});
```

`ProviderFormat` activates the provider's native structured-output mode (e.g. Ollama `format` field, OpenAI `response_format`). `PromptFormat` injects a natural-language instruction into the system prompt — works with any provider.

`response.Text` is always the raw string. `Priest` never parses the output.

---

## Error Handling

Two errors are always thrown and never captured into `response.Error`:

- `PROVIDER_NOT_REGISTERED` — no adapter found for the requested provider key.
- `SESSION_NOT_FOUND` — session lookup failed and `CreateIfMissing` is `false`.

All other provider errors (network failures, rate limits, timeouts) are caught and placed into `response.Error`. Check `response.Ok` before reading `response.Text`.

```csharp
using Priest.Errors;

try
{
    var response = await engine.RunAsync(request);
    if (response.Ok)
        Console.WriteLine(response.Text);
    else
        Console.Error.WriteLine($"Provider error: {response.Error!.Message}");
}
catch (PriestException ex)
{
    // PROVIDER_NOT_REGISTERED or SESSION_NOT_FOUND
    Console.Error.WriteLine($"Fatal: {ex.Code} — {ex.Message}");
}
```

---

## Providers

| Key | Class | Notes |
|-----|-------|-------|
| any | `OllamaProvider` | NDJSON streaming; local by default (`http://localhost:11434`) |
| any | `AnthropicProvider` | SSE streaming; requires API key |
| any | `OpenAICompatProvider` | SSE streaming; works with any OpenAI-compatible endpoint |

Provider keys are arbitrary strings — the key you register in the `adapters` dictionary must match the `Provider` field in `PriestConfig`.

---

## Custom Providers

Implement `IProviderAdapter` to add your own backend:

```csharp
using Priest.Providers;

public class MyProvider : IProviderAdapter
{
    public Task<AdapterResult> CompleteAsync(
        IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null, CancellationToken ct = default)
    {
        return Task.FromResult(new AdapterResult("Hello from MyProvider", "stop"));
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IList<ChatMessage> messages, PriestConfig config,
        OutputSpec? outputSpec = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return "Hello ";
        yield return "from MyProvider";
        await Task.CompletedTask;
    }
}
```

---

## Spec

`Priest` targets priest protocol spec **v2.0.0**. The spec lives in the [`priest`](https://github.com/tjcccc/priest) repository under `spec/`.

```csharp
PriestEngine.SpecVersion  // "2.0.0"
```

---

## Requirements

- .NET 8+
- `Microsoft.Data.Sqlite` is the only runtime dependency (required for `SqliteSessionStore`)
