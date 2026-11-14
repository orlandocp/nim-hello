# nim-hello — NVIDIA NIM console chat (C#)

Minimal interactive chat client for NVIDIA NIM's OpenAI-compatible endpoint.
No external packages: just `HttpClient` + `System.Text.Json`.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An NVIDIA API key from [build.nvidia.com](https://build.nvidia.com)

## 1. Get an API key

1. Go to https://build.nvidia.com
2. Pick a model and click **Get API Key** → create and copy it (it starts with `nvapi-`).

## 2. Configure the key

Save your key to the `.key` file in the project folder:

```powershell
Set-Content -NoNewline .key "nvapi-your-key"
```

The app reads the key **only** from that file.

> `.key` is listed in `.gitignore`. Never commit your key.

## 3. Chat

```powershell
cd nim-hello
dotnet run
```

On startup it shows the models that already responded with your account and lets
you pick one:

```
Models that work with your account (13 of 83) - 2 stale (revalidated when selected):
  22. ✓ NR  10d* meta/llama-3.2-90b-vision-instruct
  25. ✓ R    1d  meta/muse-glimmer-30b
  29. ✓ R   12h  minimaxai/minimax-m3.1-flash
  ...
  61. ✓ R   30d* nvidia/nemotron-3.5-lightning-30b-a3b

  ? = untested | ✓ = works | ✗ = not enabled | ! = error | ∅ = not in catalog
  R = reasoning | NR = no reasoning | - = unknown
  age = time since the last probe | * = stale, revalidated when selected
  number = position in the full catalog (1-83)

  Current: meta/muse-glimmer-30b  (✓R, last probe 1d ago)
  number = select · text = search · Enter = meta/muse-glimmer-30b · q = quit
  /list all (83) · working (13) · failed (10) · untested (0) · denied (60)
  /probe <target> now · /forget <target> reset · /help
model>
```

Each row has four parts:

- **Number**: the position in the **full catalog**, the same number the model
  has in the `/list all` view. Typing `25` picks the same model in every view; a
  filtered list shows gaps because it is a subset.
- **State**: `?` never tested, `✓` responds with your account, `✗` rejected
  (400/403/404/410), `!` transient error (timeout, 429, 5xx, network), `∅` no
  longer in the official catalog.
- **Reasoning**: `R` emits reasoning, `NR` does not, `-` not observed yet.
  Reasoning models show `(thinking...)` and spend `max_tokens` on hidden
  reasoning.
- **Age**: time since the last probe (`5m`, `3h`, `9d`), plus `*` when the result
  is stale and will be revalidated on selection.

On a fresh install every entry shows `?` and the selector opens the full catalog;
pick one and the app probes it before chatting.

| Input | Effect |
|-------|--------|
| `Enter` | Use the default model (last choice, or `minimaxai/minimax-m3.1-flash`) |
| `25` | Pick the model at position 25 of the full catalog |
| `nemotron` | Search by text; a single match is selected, several matches are numbered |
| `/list all` | Show the full catalog with every state |
| `/list working` | Show only the models marked `✓` |
| `/list failed` | Show only the models marked `!` |
| `/list untested` | Show only the models marked `?` |
| `/list denied` | Show only the models marked `✗` |
| `/probe all` | Test every model in the catalog (asks `y/N` first; skips `∅`) |
| `/probe failed` | Test only the models marked `!` (asks `y/N` first) |
| `/probe 22` | Test the model at position 22 right now (no confirmation) |
| `/probe <id>` | Test that model; a unique part of the id works too |
| `/forget 22` | Drop the stored result: it goes back to `?` and is probed on selection |
| `/help` | List the selector commands |
| `/quit` or `q` | Quit without chatting |

Anything that starts with `/` is a command; anything else is a number, an exact
model id or a text search. That way `all`, `probe` or `list` can still be used
as search terms.

`/probe` and `/forget` take the same targets: `all`, `working`, `failed`,
`untested`, `denied`, a full-catalog number, or a model id. A target that
resolves to more than one model asks `y/N`; a single model does not.
`/forget` never touches the network, it only clears `models.json`, so the model
is probed again the next time it is selected.

Every probe prints one line per model as it finishes, then a summary with the
elapsed time, and redraws the same view; it does not enter the chat. `/probe all`
runs without retries to stay fast; every other target uses
`NVIDIA_PROBE_RETRIES`.

```
ERROR   nvidia/llama-3.1-nemoguard-8b-content-safety  (HTTP 415)   [2/10]
OK      nvidia/nemotron-parse-2.0  (reasoning: no)   [6/10]
...
OK: 1   DENIED: 0   ERROR: 9   TOTAL: 10   in 2m 4s
```

If the chosen model is **untested or stale**, the app probes it before entering
the chat. The probe streams a tiny request and stops at the first token (content
or reasoning), so it measures reachability, not answer quality:

- Any token arrives → `ok`; the chat starts.
- 400/403/404/410 → `denied`; the reason is shown and you stay in the same list.
- Timeout, 429, 5xx or network error → `error`, with one retry for transient
  failures. The reason is shown and you stay in the same list.
- 401/402 → the probe is aborted and nothing is marked (bad key or no credit).

A model is probed again only when its last result is stale:

| Last state | Revalidated after |
|------------|-------------------|
| `ok` | 7 days |
| `error` | 60 minutes |
| `denied` | 30 days |
| `unprobed` | always |

The same happens if an untested model eventually responds in chat: it is marked
automatically. The list lives in **`models.json`** (not hardcoded). `--probe-all`
is optional: it only seeds many results at once.

### Sync with the official catalog

On every interactive start the app calls `GET /v1/models` and **adds any missing
ids to `models.json`** with state `unprobed`. Ids that disappear from a
successful catalog response are marked `inCatalog: false` (`∅`) instead of being
deleted, so their history and notes are kept. The flags are not touched when the
catalog request fails or returns an implausibly short list.

| `state` | Meaning |
|---------|---------|
| `ok` | Tested and **responds** with your account |
| `denied` | Tested and **rejected** (400/403/404/410) |
| `error` | Probe failed for a transient reason (timeout, 429, 5xx, network) |
| `unprobed` | In the official catalog, **never tested** |

| Field | Meaning |
|-------|---------|
| `id` | Model id |
| `state` | `ok` / `denied` / `error` / `unprobed` |
| `reasoning` | `true` = emits reasoning, `false` = does not, absent = not observed yet |
| `note` | Human note; the app never overwrites it |
| `lastError` | Detail of the last failure (`timeout`, `HTTP 404`, `no content`) |
| `lastProbe` | When the model was last probed or used successfully |
| `inCatalog` | `false` when the id left the official catalog |

The app learns `reasoning` automatically from the first streamed chunk after a
successful reply and stores the result.

`models.json` is schema version 2. An older or unreadable file is copied to
`models.json.bak` and ignored; it is not migrated.

`models.json`, `.model` and `.key` are gitignored: they are local state, not source.

## Chat commands

| Command | Effect |
|---------|--------|
| `/model` | Open the model selector; the conversation is kept |
| `/model 22` | Switch to position 22 of the full catalog (same number as the selector) |
| `/model <id>` | Switch by exact id, or a unique part of it (`llama-3.2-90b`) |
| `/thinking` | Show/hide the model's reasoning (in gray) |
| `/reset` | Clear the conversation history |
| `/quit` | Exit (also `/exit`) |

Switching keeps the whole conversation: the API is stateless, so the next request
sends the full history to the new model.

```
you> My name is Orlando. Reply with just OK.
bot> OK
you> /model 22
(model: meta/llama-3.2-90b-vision-instruct · 2 messages kept)
you> What is my name? Answer with one word.
bot> Orlando
```

If the new model has a smaller context window and rejects the history, the app
drops the oldest half, tells you how many messages it removed, and retries the
same message once. `/reset` clears everything by hand.

## One-shot mode

```powershell
dotnet run -- "Explain what an LLM is in two lines"
```

No menu: it uses `--model`, or `NVIDIA_MODEL`, or `.model`, or the default.

## Pick a model without the menu

```powershell
dotnet run -- --model mistralai/mistral-large-4-1t-a52b
$env:NVIDIA_MODEL = "nvidia/nemotron-3.5-lightning-30b-a3b"; dotnet run
```

## Files

| File | Purpose |
|------|---------|
| `models.json` | External model list: `state`, `reasoning`, `lastProbe`, `lastError`, `inCatalog` (generated) |
| `.model` | Last selector choice, used as the default next time (generated) |
| `.key` | Your NVIDIA API key (the only key source; gitignored) |

## Environment variables

| Variable | Default | Purpose |
|----------|---------|---------|
| `NVIDIA_MODEL` | — | Model to use; skips the menu |
| `NVIDIA_MAX_TOKENS` | `2048` | Max tokens per reply (includes reasoning) |
| `NIM_HOME` | current folder | Where to find `models.json`, `.model` and `.key` |
| `NVIDIA_PROBE_TIMEOUT` | `30` | Seconds per probe attempt |
| `NVIDIA_PROBE_RETRIES` | `1` | Retries for transient probe failures (timeout, 429, 5xx, network) |
| `NVIDIA_REPROBE_OK_DAYS` | `7` | Revalidate working models after N days |
| `NVIDIA_REPROBE_ERROR_MINUTES` | `60` | Retry errored models after N minutes |
| `NVIDIA_REPROBE_DENIED_DAYS` | `30` | Retry denied models after N days |

## How it works

- `POST https://integrate.api.nvidia.com/v1/chat/completions`
- Header `Authorization: Bearer <key from .key>`
- OpenAI-style JSON body (`model`, `messages`, `temperature`, `max_tokens`, `stream`)
- **SSE streaming**: the reply is printed token by token (`data: {...}` / `data: [DONE]`)
- Supports **reasoning models**: reads `delta.reasoning_content` separately from `delta.content`
- Keeps the conversation history in memory and resends it every turn (so the bot remembers)

## Model diagnostics

```powershell
dotnet run -- --models     # list the catalog ids (and sync models.json)
dotnet run -- --probe      # test 5 models and save the results
dotnet run -- --probe-all  # test the whole catalog, 3 at a time, and update models.json
```

`--probe` and `--probe-all` print `OK`, `DENIED` or `ERROR` per model and update
`models.json` (unlike the interactive probe, `--probe-all` does not retry
transient failures). A 401 or 402 aborts the run instead of marking every model.

**Important:** `GET /v1/models` returns the **catalog** (84 models at the time of
writing), not what your account is allowed to use. The only way to know what is
enabled is to test with your key. You do **not** need `--probe-all` to start:
pick any model in the selector and it is probed on demand. `--probe-all` is just
a bulk shortcut for testing the whole catalog at once.

## Common errors

| HTTP | Cause |
|------|-------|
| 401 / 403 | Invalid or expired key, or no permission for that model |
| 402 | No credit on the account |
| 404 | The `model` is not enabled for your account |
| 410 | The model was retired or removed from the catalog |
| 429 | Rate limit: wait and retry |

If a model **does not respond**, it is recorded as `!` (timeout), not `✗`: the
result is inconclusive, not proof that the account lacks access. Retry later or
pick it again from the selector.
