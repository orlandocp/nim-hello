# AGENTS.md

Minimal .NET 10 console chat client for NVIDIA NIM's OpenAI-compatible API.
All logic lives in one file, `Program.cs` (top-level statements plus `NimChat`,
`ModelRegistry`, `ChatMessage`). No packages, tests, CI, solution file, linter,
or formatter config. `README.md` is the user-facing manual; keep it accurate
when changing flags or behavior.

## Language

- Code, identifiers, comments, commit messages, and every string the app prints
  are in **English**. Do not add Spanish text to the app, including console
  output, error messages, and legends.
- Docs may be edited in any language the user requests, but the app UI stays
  English.

## Commands

```powershell
dotnet build                          # compile only
dotnet run                            # interactive chat (live API + stdin)
dotnet run -- "question"              # one-shot; best for smoke tests
dotnet run -- --model <id> "question" # --model combines with a question
dotnet run -- --models                # catalog ids + sync models.json
dotnet run -- --probe                 # fixed 5 model probes, 30s each, 1 retry
dotnet run -- --probe-all             # whole catalog, 3 concurrent, no retry
```

## Gotchas

- Every invocation, including `--models` and `--probe`, reads the key at startup
  and exits with `Missing API key.` if `.key` is absent in `NIM_HOME`
  (default: current directory). Diagnostics are not key-free.
- Running with no args enters the interactive selector and makes live API calls.
  A model that does not respond hangs until the 120s stream timeout (30s for the
  probe). Do not run it in automated checks without cutting it off.
- `.key`, `.model`, `models.json`, `.env` are gitignored local state. `models.json`
  is generated and rewritten by the app; do not hand-edit or commit it.
- Model selection priority: `--model` > `NVIDIA_MODEL` > `.model` > the
  `DefaultModel` constant at the top of `Program.cs`.
- `models.json` is schema version 2: states are `ok`, `denied`, `error`,
  `unprobed`, plus `inCatalog`. An old or unreadable file is copied to
  `models.json.bak` and ignored, not migrated. Bump `ModelRegistry.SchemaVersion`
  when changing the shape.
- Probe/revalidation rules: untested always probes; `ok` after 7 days, `error`
  after 60 minutes, `denied` after 30 days. All configurable with
  `NVIDIA_REPROBE_*`; probe timeout/retries with `NVIDIA_PROBE_*`.
- A probe is a streaming request that stops at the first token (content or
  reasoning). Do not turn it back into a full non-streaming request: reasoning
  models can spend a small `max_tokens` budget on hidden thinking and produce
  false results.
- `!` means inconclusive (timeout/429/5xx/network), not "not enabled"; only
  400/403/404/410 map to `denied`.
- Selector numbers are positions in the **full catalog**, not in the filtered
  view: `25` must pick the same model in the works view, in search and in `all`.
  Keep that invariant when touching `PrintModelList` or the number parsing.
- `--probe-all` is optional: the selector probes any untested or stale model on
  demand. Never present the bulk command as a setup requirement.
- `--probe-all` and the selector's `/probe` / `/retry` share `ProbeBatchAsync`
  (3 concurrent, one live line per model, summary at the end). `/probe` and
  `--probe-all` skip retries; `/retry` uses `NVIDIA_PROBE_RETRIES`. `/probe`
  asks for confirmation and skips ids that left the catalog.
- Selector commands start with `/` (`/all`, `/works`, `/probe`, `/retry`,
  `/help`, `/quit`); anything else is a number, an exact id or a text search.
  Keep the prefix rule: it is what allows searching for words like `probe`.
- `/model` in the chat keeps `chatHistory`: switching models only changes the
  `model` field, because the API is stateless. Do not clear the history there.
  A 400/413 that mentions the context triggers `TrimHistory` (drops the oldest
  half in pairs, to keep user/assistant alternation) and one retry.
- Reasoning models spend `max_tokens` on hidden reasoning; empty replies usually
  mean the token budget ran out.
- Windows console output is set to UTF-8 so the `✓`/`✗`/`∅` markers render;
  preserve that when adding output.
- Exit codes: `0` ok, `1` missing key/arg/HTTP/timeout error, `2` one-shot empty
  reply.

## Conventions

- Keep the project dependency-free (`HttpClient` + `System.Text.Json`); target is
  `net10.0` with nullable and implicit usings enabled.
- Map new HTTP status cases in `NimChat.BuildHttpError` (display) and
  `NimChat.Classify` (state), not at call sites.
- Verify with `dotnet build`; use one-shot `dotnet run -- "hi"` only if a valid
  `.key` is present.
