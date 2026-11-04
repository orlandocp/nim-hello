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

PowerShell (current session only):

```powershell
$env:NVIDIA_API_KEY = "nvapi-your-key"
```

Or save it to the `.key` file in the project folder (the app falls back to it when
the environment variable is missing):

```powershell
Set-Content -NoNewline .key "nvapi-your-key"
```

> `.key` is listed in `.gitignore`. Never commit your key.

## 3. Chat

```powershell
cd nim-hello
dotnet run
```

On startup it shows the models that already responded with your account and lets
you pick one:

```
Models that work with your account (14):
   1. ✔ google/diffusiongemma-26b-a4b-it                 8. ✔ nvidia/nemotron-3-ultra-550b-a55b
   2. ✔ meta/llama-3.2-90b-vision-instruct               9. ✔ nvidia/nemotron-3.5-bolt-e4b
   3. ✔ meta/llama-guard-4-12b                          10. ✔ nvidia/nemotron-3.5-lightning-30b-a3b
   4. ✔ meta/muse-glimmer-30b                           11. ✔ nvidia/nemotron-3.5-super-vl-120b-a12b
   5. ✔ minimaxai/minimax-m3.1-flash                    12. ✔ nvidia/nemotron-parse-2.0
   6. ✔ mistralai/mistral-large-4-1t-a52b               13. ✔ nvidia/riva-translate-4b-instruct-v2
   7. ✔ nvidia/nemotron-3-nano-omni-30b-a3b-reasoning   14. ✔ poolside/laguna-xs-2.1

  Enter = minimaxai/minimax-m3.1-flash
  number = select · all = full catalog (85) · text = search · q = quit
model>
```

| Input | Effect |
|-------|--------|
| `Enter` | Use the default model (last choice, or `minimaxai/minimax-m3.1-flash`) |
| `5` | Pick model number 5 from the displayed list |
| `all` | Show the full catalog (85) in two columns: `✔` works, `✘` failed, blank = untested |
| `nemotron` | Search by text; a single match is selected, several matches are numbered |
| `q` | Quit without chatting |

If you pick a model **without ✔**, the app sends it a test question with a
**30-second timeout**:

- It responds → marked as `ok` and saved to `models.json`.
- No response (timeout) or 400/403/404 → saved as `failed` and you are back at the menu.

The same happens if an untested model eventually responds in chat: it is marked
automatically. The list lives in **`models.json`** (not hardcoded) and can be
regenerated with `--probe-all`.

### Sync with the official catalog

On every interactive start the app calls `GET /v1/models` and **adds any missing
ids to `models.json`** with state `unprobed`. That way the file reflects the
official list even before you know whether your account can use those models.

Each entry has a single state field:

| `state` | Meaning |
|---------|---------|
| `ok` | Tested and **responds** with your account |
| `failed` | Tested and **does not respond** (timeout, 404…) |
| `unprobed` | In the official catalog, **never tested** |

| Field | Meaning |
|-------|---------|
| `id` | Model id |
| `state` | `ok` / `failed` / `unprobed` |
| `note` | Optional detail from the last result (`timeout`, `HTTP 404`, etc.) |

The file never removes old entries: if NVIDIA drops a model from the catalog it
stays there as a reference until you delete it by hand.

`models.json`, `.model` and `.key` are gitignored: they are local state, not source.

## Chat commands

| Command | Effect |
|---------|--------|
| `/model` | Open the model selector again (clears the history when switching) |
| `/thinking` | Show/hide the model's reasoning (in gray) |
| `/reset` | Clear the conversation history |
| `/quit` | Exit (also `/exit`) |

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
| `models.json` | External model list: `state` (`ok`/`failed`/`unprobed`) and `note` (generated) |
| `.model` | Last selector choice, used as the default next time (generated) |
| `.key` | Optional API key, used when `NVIDIA_API_KEY` is missing |

## Environment variables

| Variable | Default | Purpose |
|----------|---------|---------|
| `NVIDIA_API_KEY` | — | API key (falls back to `.key`) |
| `NVIDIA_MODEL` | — | Model to use; skips the menu |
| `NVIDIA_MAX_TOKENS` | `2048` | Max tokens per reply (includes reasoning) |
| `NIM_HOME` | current folder | Where to find `models.json`, `.model` and `.key` |

## How it works

- `POST https://integrate.api.nvidia.com/v1/chat/completions`
- Header `Authorization: Bearer $NVIDIA_API_KEY`
- OpenAI-style JSON body (`model`, `messages`, `temperature`, `max_tokens`, `stream`)
- **SSE streaming**: the reply is printed token by token (`data: {...}` / `data: [DONE]`)
- Supports **reasoning models**: reads `delta.reasoning_content` separately from `delta.content`
- Keeps the conversation history in memory and resends it every turn (so the bot remembers)

## Model diagnostics

```powershell
dotnet run -- --models     # list the 85 catalog ids (and sync models.json)
dotnet run -- --probe      # test 5 models with a 15s timeout
dotnet run -- --probe-all  # test the whole catalog and update models.json
```

**Important:** `GET /v1/models` returns the **catalog** (85 models), not what your
account is allowed to use. The only way to know what is enabled is to test with
your key. Free accounts usually have only a subset enabled; run `--probe-all`
to find yours.

## Common errors

| HTTP | Cause |
|------|-------|
| 401 / 403 | Invalid or expired key, or no permission for that model |
| 402 | No credit on the account |
| 404 | The `model` is not enabled for your account |
| 429 | Rate limit: wait and retry |

If a model **does not respond** (it just hangs), it is not enabled for the account:
the gateway does not answer and the client has to cut it off with a timeout. Use
another model or `--probe-all`.
