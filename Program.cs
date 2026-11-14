using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected console */ }

const string DefaultModel = "minimaxai/minimax-m3.1-flash";
const int DefaultProbeTimeoutSeconds = 30;
const int DefaultProbeRetries = 1;
const int DefaultOkDays = 7;
const int DefaultErrorMinutes = 60;
const int DefaultDeniedDays = 30;

// Folder where models.json, .model and .key live (current directory by default).
var nimHome = Environment.GetEnvironmentVariable("NIM_HOME");
if (string.IsNullOrWhiteSpace(nimHome)) nimHome = Directory.GetCurrentDirectory();
nimHome = Path.GetFullPath(nimHome);

var modelsPath = Path.Combine(nimHome, "models.json");
var lastModelPath = Path.Combine(nimHome, ".model");
var keyPath = Path.Combine(nimHome, ".key");

// Probe and revalidation settings.
var probeTimeoutSeconds = ReadIntEnv("NVIDIA_PROBE_TIMEOUT", DefaultProbeTimeoutSeconds, 1);
var probeRetries = ReadIntEnv("NVIDIA_PROBE_RETRIES", DefaultProbeRetries, 0);
var okTtl = TimeSpan.FromDays(ReadIntEnv("NVIDIA_REPROBE_OK_DAYS", DefaultOkDays, 0));
var errorTtl = TimeSpan.FromMinutes(ReadIntEnv("NVIDIA_REPROBE_ERROR_MINUTES", DefaultErrorMinutes, 0));
var deniedTtl = TimeSpan.FromDays(ReadIntEnv("NVIDIA_REPROBE_DENIED_DAYS", DefaultDeniedDays, 0));

// --- Arguments: --model <id> can be combined with a question ---
var argv = args.ToList();
string? cliModel = null;
var modelFlag = argv.IndexOf("--model");
if (modelFlag >= 0)
{
    if (modelFlag + 1 >= argv.Count)
    {
        Console.Error.WriteLine("Missing model id after --model.");
        return 1;
    }
    cliModel = argv[modelFlag + 1].Trim();
    argv.RemoveRange(modelFlag, 2);
}

// --- API key: read only from the .key file ---
string? apiKey = null;
if (File.Exists(keyPath))
{
    try { apiKey = File.ReadAllText(keyPath).Trim(); } catch { /* reported below */ }
}

if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Missing API key.");
    Console.Error.WriteLine($@"Save your key to: {keyPath}");
    return 1;
}

// Max tokens per reply (includes the "thinking" of reasoning models).
var maxTokens = ReadIntEnv("NVIDIA_MAX_TOKENS", 2048, 1);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
var api = new NimChat(http, DefaultModel);

// External list of models and whether they work with this account.
var registry = ModelRegistry.Load(modelsPath);
List<string>? catalogCache = null;

// --- Diagnostic modes ---
if (argv.Count == 1 && argv[0] is "--help" or "-h")
{
    PrintHelp();
    return 0;
}

if (argv.Count == 1 && argv[0] is "--models" or "-m")
{
    try
    {
        var ids = await api.ListModelsAsync();
        foreach (var id in ids)
            Console.WriteLine(id);

        // Keep models.json in sync with the official catalog.
        var (added, changed) = registry.SyncCatalog(ids);
        if (changed)
        {
            registry.Save();
            Console.Error.WriteLine(added > 0
                ? $"[models.json] +{added} new models from the catalog."
                : "[models.json] catalog membership updated.");
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[error] {ex.Message}");
        return 1;
    }
}

if (argv.Count == 1 && argv[0] is "--probe" or "-p")
{
    string[] candidates =
    [
        DefaultModel,
        "mistralai/mistral-large-4-1t-a52b",
        "nvidia/nemotron-3.5-lightning-30b-a3b",
        "deepseek-ai/deepseek-v4.1-flash",
        "meta/llama-3.2-11b-vision-instruct"
    ];

    foreach (var candidate in candidates)
    {
        var result = await ProbeModelAsync(new NimChat(http, candidate), probeTimeoutSeconds, probeRetries);
        if (result.Outcome == ProbeOutcome.Fatal)
        {
            Console.WriteLine($"ABORT   {candidate}  ->  {result.Detail}");
            Console.WriteLine("Check the API key or account credit, then retry.");
            registry.Save();
            return 1;
        }

        registry.ApplyProbe(candidate, result);

        var label = result.Outcome switch
        {
            ProbeOutcome.Responded => "OK",
            ProbeOutcome.Denied => "DENIED",
            _ => "ERROR"
        };
        var detail = result.Outcome == ProbeOutcome.Responded
            ? $"({registry.DescribeReasoning(candidate)})"
            : $"({result.Detail})";
        Console.WriteLine($"{label,-7} {candidate}  {detail}");
    }

    registry.Save();
    return 0;
}

if (argv.Count == 1 && argv[0] is "--probe-all" or "-pa")
{
    List<string> ids;
    try
    {
        ids = (await api.ListModelsAsync()).ToList();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[error] {ex.Message}");
        return 1;
    }

    var batch = await ProbeBatchAsync(ids, retries: 0);
    registry.Save();
    PrintBatchSummary(batch);
    Console.WriteLine($"models.json updated: {registry.SupportedIds.Count(id => registry.IsInCatalog(id))} supported.");

    if (batch.Fatal is not null)
    {
        Console.Error.WriteLine($"[aborted] {batch.Fatal}. Check the API key or account credit.");
        return 1;
    }

    return 0;
}

// --- Model to use ---
// Priority: --model > NVIDIA_MODEL > .model (last choice) > default.
var envModel = Environment.GetEnvironmentVariable("NVIDIA_MODEL");
var explicitModel = !string.IsNullOrWhiteSpace(cliModel)
    ? cliModel
    : !string.IsNullOrWhiteSpace(envModel) ? envModel.Trim() : null;

var savedModel = ReadSavedModel(lastModelPath);
var model = explicitModel ?? savedModel ?? DefaultModel;

// One-shot mode: dotnet run -- "your question" (no menu)
if (argv.Count > 0)
{
    var question = string.Join(' ', argv);
    var nim = new NimChat(http, model);
    var history = new List<ChatMessage> { new("user", question) };

    try
    {
        var reply = await PrintStreamAsync(nim, history, maxTokens, showReasoning: false);

        if (reply.Text.Trim().Length > 0)
        {
            var changed = registry.MarkChatSuccess(model, reply.Reasoning);
            registry.Save();
            if (changed)
                Console.Error.WriteLine($"\n[models.json] {model}: {registry.DescribeReasoning(model)}.");
        }

        return reply.Text.Trim().Length == 0 ? 2 : 0;
    }
    catch (OperationCanceledException)
    {
        Console.ResetColor();
        Console.Error.WriteLine("\n[timeout] the model did not respond within 120s.");
        registry.MarkChatError(model, ModelState.Error, "timeout 120s");
        registry.Save();
        return 1;
    }
    catch (Exception ex)
    {
        Console.ResetColor();
        Console.Error.WriteLine($"\n[error] {ex.Message}");
        var (state, detail) = ClassifyChatError(ex);
        registry.MarkChatError(model, state, detail);
        registry.Save();
        return 1;
    }
}

// --- Interactive chat ---
// First sync models.json with the official catalog from the endpoint:
// new models show up even if we don't know yet whether they work.
await GetCatalogAsync();

// If the model was not forced (--model / NVIDIA_MODEL), show the selector.
if (explicitModel is null)
{
    var menuDefault = savedModel is not null && registry.IsSupported(savedModel) ? savedModel : DefaultModel;
    var picked = await SelectModelAsync(menuDefault);
    if (picked is null)
    {
        Console.WriteLine("(bye)");
        return 0;
    }
    model = picked;
    SaveLastModel(lastModelPath, model);
}

var chat = new NimChat(http, model);
var chatHistory = new List<ChatMessage>();
var showReasoning = false;

var ageText = registry.GetAge(model);
Console.WriteLine();
Console.WriteLine($"Model: {model}  (max_tokens: {maxTokens}, {registry.DescribeReasoning(model)}, " +
                  $"{(ageText == "n/a" ? "not probed" : $"last probe {ageText} ago")})");
Console.WriteLine("Type your message. Commands: /model [id] (switch, keeps the conversation), /thinking, /reset, /quit.");
Console.WriteLine();

while (true)
{
    Console.Write("you> ");
    var line = Console.ReadLine();
    if (line is null) break; // Ctrl+Z / end of input

    line = line.Trim();
    if (line.Length == 0) continue;

    if (line is "/quit" or "/exit") break;

    if (line == "/reset")
    {
        chatHistory.Clear();
        Console.WriteLine("(history cleared)\n");
        continue;
    }

    if (line == "/thinking")
    {
        showReasoning = !showReasoning;
        Console.WriteLine(showReasoning
            ? "(showing the model's reasoning in gray)\n"
            : "(reasoning hidden)\n");
        continue;
    }

    var isModelCommand = line.StartsWith("/model", StringComparison.OrdinalIgnoreCase)
                         && (line.Length == "/model".Length || char.IsWhiteSpace(line["/model".Length]));

    if (isModelCommand)
    {
        var arg = line["/model".Length..].Trim();
        string? picked;

        if (arg.Length == 0)
        {
            picked = await SelectModelAsync(model);
            if (picked is null)
            {
                Console.WriteLine("(unchanged)\n");
                continue;
            }
        }
        else
        {
            picked = await ResolveModelAsync(arg);
            if (picked is null)
            {
                Console.WriteLine();
                continue;
            }
            if (!await TrySelectAsync(picked)) continue;
        }

        // The conversation is kept: the next request sends the whole history to
        // the new model (the API is stateless).
        model = picked;
        chat = new NimChat(http, model);
        SaveLastModel(lastModelPath, model);
        Console.WriteLine($"(model: {model} · {chatHistory.Count} messages kept)\n");
        continue;
    }

    var trims = 0;
    while (true)
    {
        chatHistory.Add(new ChatMessage("user", line));
        Console.Write("bot> ");

        try
        {
            var reply = await PrintStreamAsync(chat, chatHistory, maxTokens, showReasoning);

            if (reply.Text.Trim().Length == 0)
            {
                chatHistory.RemoveAt(chatHistory.Count - 1);
                Console.WriteLine("\n[warning] the model returned no content (did it run out of max_tokens while thinking?).");
                Console.WriteLine($"          Try: $env:NVIDIA_MAX_TOKENS = \"{maxTokens * 2}\" and restart.\n");
                break;
            }

            var changed = registry.MarkChatSuccess(model, reply.Reasoning);
            registry.Save();

            if (changed)
                Console.WriteLine($"\n[models.json] {model}: {registry.DescribeReasoning(model)}.");

            chatHistory.Add(new ChatMessage("assistant", reply.Text));
            Console.WriteLine("\n");
            break;
        }
        catch (Exception ex) when (IsContextOverflow(ex) && trims < 2 && chatHistory.Count > 2)
        {
            // The model's context window is smaller than the history: drop the
            // oldest turns and send the same message again.
            trims++;
            Console.ResetColor();
            chatHistory.RemoveAt(chatHistory.Count - 1);
            var dropped = TrimHistory(chatHistory);
            Console.Error.WriteLine($"\n[context] {model} rejected the history as too long. " +
                                    $"Dropped {dropped} older messages, retrying...\n");
        }
        catch (OperationCanceledException)
        {
            Console.ResetColor();
            chatHistory.RemoveAt(chatHistory.Count - 1);
            registry.MarkChatError(model, ModelState.Error, "timeout 120s");
            registry.Save();
            Console.Error.WriteLine("\n[timeout] the model did not respond within 120s. Try again or use /model.\n");
            break;
        }
        catch (Exception ex)
        {
            Console.ResetColor();
            chatHistory.RemoveAt(chatHistory.Count - 1);
            var (state, detail) = ClassifyChatError(ex);
            registry.MarkChatError(model, state, detail);
            registry.Save();
            Console.Error.WriteLine($"\n[error] {ex.Message}\n");
            break;
        }
    }
}

return 0;

// Official endpoint catalog + models.json sync (once per run).
async Task<List<string>> GetCatalogAsync()
{
    if (catalogCache is not null) return catalogCache;

    try
    {
        catalogCache = (await api.ListModelsAsync()).ToList();
        var (added, changed) = registry.SyncCatalog(catalogCache);
        if (changed)
        {
            registry.Save();
            Console.WriteLine(added > 0
                ? $"(models.json: +{added} new models from the official catalog)"
                : "(models.json: catalog membership updated)");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[warning] could not read the official NVIDIA catalog: {ex.Message}");
        catalogCache = [];
    }

    return catalogCache;
}

// Resolves "/model <text>" to a model id: exact match first, then a unique
// substring match.
async Task<string?> ResolveModelAsync(string arg)
{
    var catalog = await GetCatalogAsync();
    var all = catalog.Concat(registry.Ids)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
        .ToList();

    var exact = all.FirstOrDefault(id => id.Equals(arg, StringComparison.OrdinalIgnoreCase));
    if (exact is not null) return exact;

    var matches = all.Where(id => id.Contains(arg, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count == 1) return matches[0];

    Console.WriteLine(matches.Count == 0
        ? $"  No model matches \"{arg}\"."
        : $"  \"{arg}\" matches {matches.Count} models. Use /model to pick one from the list.");
    return null;
}

// --- Model selector ---
async Task<string?> SelectModelAsync(string current)
{
    var catalog = await GetCatalogAsync();

    var all = catalog.Concat(registry.Ids)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (all.Count == 0)
    {
        Console.Error.WriteLine("[error] no models available: the catalog is unreachable and models.json is empty.");
        return null;
    }

    var mode = "working";
    var search = "";

    // Turns a target word, a full-catalog number or a model id into a list of
    // ids. /probe and /forget share it, so both speak the same vocabulary.
    List<string>? ResolveTargets(string argument)
    {
        var word = argument.Trim();
        if (word.Length == 0)
        {
            Console.WriteLine("  Missing target: a number, an id, or " +
                              "all/working/failed/untested/denied.");
            Console.WriteLine();
            return null;
        }

        switch (word.ToLowerInvariant())
        {
            case "all":
                return all.Where(registry.IsInCatalog).ToList();
            case "working":
                return all.Where(id => registry.IsSupported(id) && registry.IsInCatalog(id)).ToList();
            case "failed":
                return all.Where(id => registry.IsError(id) && registry.IsInCatalog(id)).ToList();
            case "untested":
                return all.Where(id => registry.IsUntested(id) && registry.IsInCatalog(id)).ToList();
            case "denied":
                return all.Where(id => registry.IsDenied(id) && registry.IsInCatalog(id)).ToList();
        }

        if (int.TryParse(word, out var n))
        {
            if (n < 1 || n > all.Count)
            {
                Console.WriteLine($"  There is no number {n} (the full catalog has {all.Count}).");
                Console.WriteLine();
                return null;
            }

            return [all[n - 1]];
        }

        var exact = all.FirstOrDefault(id => id.Equals(word, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return [exact];

        var matches = all.Where(id => id.Contains(word, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches;

        Console.WriteLine(matches.Count == 0
            ? $"  No model matches \"{word}\"."
            : $"  \"{word}\" matches {matches.Count} models. Use a number or the full id.");
        Console.WriteLine();
        return null;
    }

    while (true)
    {
        List<string> shown;
        string header;

        switch (mode)
        {
            case "all":
                shown = all;
                header = $"Full catalog ({all.Count})";
                break;

            case "search":
                shown = all.Where(id => id.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
                header = $"Results for \"{search}\" ({shown.Count} of {all.Count})";
                break;

            case "failed":
                shown = all.Where(id => registry.IsError(id) && registry.IsInCatalog(id)).ToList();
                header = $"Models that failed the last probe ({shown.Count} of {all.Count})";
                break;

            case "untested":
                shown = all.Where(id => registry.IsUntested(id) && registry.IsInCatalog(id)).ToList();
                header = $"Models not probed yet ({shown.Count} of {all.Count})";
                break;

            case "denied":
                shown = all.Where(id => registry.IsDenied(id) && registry.IsInCatalog(id)).ToList();
                header = $"Models not enabled for your account ({shown.Count} of {all.Count})";
                break;

            default: // "working"
                shown = registry.Ids
                    .Where(registry.IsSupported)
                    .ToList();

                if (shown.Count == 0)
                {
                    shown = all;
                    header = $"No usable tested models - full catalog ({all.Count})";
                }
                else
                {
                    header = $"Models that work with your account ({shown.Count} of {all.Count})";
                }
                break;
        }

        var stale = shown.Count(id => registry.IsStale(id, okTtl, errorTtl, deniedTtl));
        if (stale > 0) header += $" - {stale} stale (revalidated when selected)";

        PrintModelList(shown, header, registry, okTtl, errorTtl, deniedTtl, showAge: mode != "all", all);

        var currentMarker = registry.GetMarker(current).Replace(" ", "");
        var currentAge = registry.GetAge(current);
        Console.WriteLine($"  Current: {current}  ({currentMarker}, " +
                          $"{(currentAge == "n/a" ? "not probed" : $"last probe {currentAge} ago")})");
        Console.WriteLine($"  number = select · text = search · Enter = {current} · q = quit");
        Console.WriteLine(
            $"  /list all ({all.Count}) · " +
            $"working ({all.Count(id => registry.IsSupported(id) && registry.IsInCatalog(id))}) · " +
            $"failed ({all.Count(id => registry.IsError(id) && registry.IsInCatalog(id))}) · " +
            $"untested ({all.Count(id => registry.IsUntested(id) && registry.IsInCatalog(id))}) · " +
            $"denied ({all.Count(id => registry.IsDenied(id) && registry.IsInCatalog(id))})");
        Console.WriteLine("  /probe <target> now · /forget <target> reset · /help");
        Console.Write("model> ");

        var input = (Console.ReadLine() ?? "").Trim();

        if (input.Length == 0) input = current;
        if (input is "q" or "Q") return null;

        // Anything starting with "/" is a command; everything else is a number,
        // an exact id or a text search, so the two never collide.
        if (input.StartsWith('/'))
        {
            var body = input[1..].Trim();
            var space = body.IndexOfAny([' ', '\t']);
            var command = (space < 0 ? body : body[..space]).ToLowerInvariant();
            var argument = space < 0 ? "" : body[(space + 1)..].Trim();

            switch (command)
            {
                case "quit" or "exit":
                    return null;

                case "help":
                    PrintSelectorHelp();
                    continue;

                case "list":
                {
                    var view = argument.ToLowerInvariant();
                    if (view is not ("all" or "working" or "failed" or "untested" or "denied"))
                    {
                        Console.WriteLine("  /list expects all, working, failed, untested or denied.");
                        Console.WriteLine();
                        continue;
                    }

                    mode = view;
                    search = "";
                    continue;
                }

                case "probe":
                {
                    var ids = ResolveTargets(argument);
                    if (ids is null) continue;

                    if (ids.Count == 0)
                    {
                        Console.WriteLine($"  No models in the {argument} state.");
                        Console.WriteLine();
                        continue;
                    }

                    if (ids.Count > 1)
                    {
                        Console.Write(argument.Equals("all", StringComparison.OrdinalIgnoreCase)
                            ? $"  Probe all {ids.Count} models? This can take a few minutes. [y/N] "
                            : $"  Probe {ids.Count} models? [y/N] ");
                        var answer = (Console.ReadLine() ?? "").Trim();
                        if (answer is not ("y" or "Y" or "yes" or "Yes" or "YES"))
                        {
                            Console.WriteLine("  (cancelled)");
                            Console.WriteLine();
                            continue;
                        }
                    }

                    // A full sweep stays fast: no retries. Anything you aimed at
                    // explicitly gets the configured retries.
                    var retries = argument.Equals("all", StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : probeRetries;

                    Console.WriteLine();
                    var batch = await ProbeBatchAsync(ids, retries);
                    registry.Save();
                    PrintBatchSummary(batch);
                    if (batch.Fatal is not null)
                        Console.WriteLine($"  ! {batch.Fatal}. Check the API key or account credit.");
                    Console.WriteLine();
                    continue;
                }

                case "forget":
                {
                    var ids = ResolveTargets(argument);
                    if (ids is null) continue;

                    if (ids.Count == 0)
                    {
                        Console.WriteLine($"  No models in the {argument} state.");
                        Console.WriteLine();
                        continue;
                    }

                    if (ids.Count > 1)
                    {
                        Console.Write($"  Forget the stored result of {ids.Count} models? [y/N] ");
                        var answer = (Console.ReadLine() ?? "").Trim();
                        if (answer is not ("y" or "Y" or "yes" or "Yes" or "YES"))
                        {
                            Console.WriteLine("  (cancelled)");
                            Console.WriteLine();
                            continue;
                        }
                    }

                    var forgotten = ids.Count(registry.Forget);
                    registry.Save();

                    Console.WriteLine(ids.Count == 1
                        ? $"  {ids[0]}: back to \"?\", probed again when selected."
                        : $"  {forgotten} of {ids.Count} models back to \"?\", probed again when selected.");
                    Console.WriteLine();
                    continue;
                }

                default:
                    Console.WriteLine($"  Unknown command \"/{command}\". Type /help to see the commands.");
                    Console.WriteLine();
                    continue;
            }
        }

        if (int.TryParse(input, out var n))
        {
            if (n < 1 || n > all.Count)
            {
                Console.WriteLine($"  There is no number {n} (the full catalog has {all.Count}).");
                Console.WriteLine();
                continue;
            }

            // Numbers are positions in the full catalog, so they mean the same
            // model in every view (works, search, all).
            var picked = all[n - 1];
            if (await TrySelectAsync(picked)) return picked;
            continue;
        }

        var exact = all.FirstOrDefault(id => id.Equals(input, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            if (await TrySelectAsync(exact)) return exact;
            continue;
        }

        var matches = all.Where(id => id.Contains(input, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            Console.WriteLine($"  No results for \"{input}\". Try another text or type /list all.");
            Console.WriteLine();
            continue;
        }
        if (matches.Count == 1)
        {
            Console.WriteLine($"  -> {matches[0]}");
            if (await TrySelectAsync(matches[0])) return matches[0];
            continue;
        }

        mode = "search";
        search = input;
    }
}

// Probe the model when it is untested or its last result is stale.
async Task<bool> TrySelectAsync(string id)
{
    if (!registry.NeedsProbe(id, okTtl, errorTtl, deniedTtl)) return true;

    var age = registry.GetAge(id);
    Console.WriteLine(age == "n/a"
        ? $"  Probing {id} (timeout {probeTimeoutSeconds}s)..."
        : $"  Revalidating {id} (last probe {age} ago, timeout {probeTimeoutSeconds}s)...");

    var result = await ProbeModelAsync(new NimChat(http, id), probeTimeoutSeconds, probeRetries);

    if (result.Outcome == ProbeOutcome.Fatal)
    {
        Console.WriteLine($"  ! {result.Detail}. Check the API key or account credit.");
        Console.WriteLine();
        return false;
    }

    registry.ApplyProbe(id, result);
    registry.Save();

    switch (result.Outcome)
    {
        case ProbeOutcome.Responded:
            var extra = result.Detail.Length > 0 ? $" [{result.Detail}]" : "";
            Console.WriteLine($"  ✓ {id} responds ({registry.DescribeReasoning(id)}){extra}. Saved to models.json.");
            Console.WriteLine();
            return true;
        case ProbeOutcome.Denied:
            Console.WriteLine($"  ✗ {id}: not enabled ({result.Detail}). Saved to models.json.");
            Console.WriteLine();
            return false;
        default:
            Console.WriteLine($"  ✗ {id}: error ({result.Detail}). Saved to models.json.");
            Console.WriteLine();
            return false;
    }
}

// Prints the reply live. "Thinking" is shown in gray or as (thinking...).
static async Task<(string Text, bool Reasoning)> PrintStreamAsync(NimChat nim, IReadOnlyList<ChatMessage> history, int maxTokens, bool showReasoning)
{
    var dimmed = false;
    var thinkingShown = false;
    var contentStarted = false;

    var result = await nim.StreamAsync(
        history,
        onContent: text =>
        {
            if (!contentStarted)
            {
                contentStarted = true;
                if (dimmed) { Console.ResetColor(); Console.WriteLine(); }
                else if (thinkingShown) { Console.WriteLine(); }
            }
            Console.Write(text);
        },
        onReasoning: text =>
        {
            if (showReasoning)
            {
                if (!dimmed) { dimmed = true; Console.ForegroundColor = ConsoleColor.DarkGray; }
                Console.Write(text);
            }
            else if (!thinkingShown)
            {
                thinkingShown = true;
                Console.Write("(thinking...)");
            }
        },
        maxTokens: maxTokens);

    Console.ResetColor();
    return result;
}

static void PrintModelList(IReadOnlyList<string> ids, string header, ModelRegistry registry, TimeSpan okTtl, TimeSpan errorTtl, TimeSpan deniedTtl, bool showAge, IReadOnlyList<string> catalog)
{
    Console.WriteLine();
    Console.WriteLine(header + ":");

    if (ids.Count == 0)
    {
        Console.WriteLine("  (empty list)");
        Console.WriteLine();
        return;
    }

    // Numbers are positions in the full catalog, so a filtered view keeps the
    // number the model has in the "all" view.
    var numberById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < catalog.Count; i++) numberById[catalog[i]] = i + 1;

    var numberWidth = catalog.Count.ToString().Length;
    var cells = new List<string>(ids.Count);
    for (var i = 0; i < ids.Count; i++)
    {
        var stale = registry.IsStale(ids[i], okTtl, errorTtl, deniedTtl);
        var number = (numberById.TryGetValue(ids[i], out var global) ? global : i + 1)
            .ToString().PadLeft(numberWidth);
        var marker = registry.GetMarker(ids[i]);

        // The full catalog drops the age column so it still fits in two columns.
        cells.Add(showAge
            ? $"{number}. {marker} {registry.GetAge(ids[i]).PadLeft(4)}{(stale ? "*" : " ")} {ids[i]}"
            : $"{number}. {marker}{(stale ? "*" : " ")} {ids[i]}");
    }

    var maxLen = cells.Max(c => c.Length);
    var twoColumns = ids.Count > 10 && maxLen * 2 + 4 <= SafeWidth();

    if (!twoColumns)
    {
        foreach (var cell in cells)
            Console.WriteLine("  " + cell);
    }
    else
    {
        var rows = (cells.Count + 1) / 2;
        for (var row = 0; row < rows; row++)
        {
            var line = "  " + cells[row].PadRight(maxLen + 3);
            if (row + rows < cells.Count) line += cells[row + rows];
            Console.WriteLine(line.TrimEnd());
        }
    }

    Console.WriteLine();
    Console.WriteLine("  ? = untested | ✓ = works | ✗ = not enabled | ! = error | ∅ = not in catalog");
    Console.WriteLine("  R = reasoning | NR = no reasoning | - = unknown");
    Console.WriteLine("  age = time since the last probe | * = stale, revalidated when selected");
    if (ids.Count != catalog.Count)
        Console.WriteLine($"  number = position in the full catalog (1-{catalog.Count})");
    Console.WriteLine();
}

static int SafeWidth()
{
    try { return Console.WindowWidth > 0 ? Console.WindowWidth : 120; }
    catch { return 120; }
}

static string? ReadSavedModel(string path)
{
    try
    {
        if (!File.Exists(path)) return null;
        var value = File.ReadAllText(path).Trim();
        return value.Length > 0 ? value : null;
    }
    catch { return null; }
}

static void SaveLastModel(string path, string model)
{
    try { File.WriteAllText(path, model + Environment.NewLine); } catch { /* not critical */ }
}

static int ReadIntEnv(string name, int fallback, int minimum)
{
    var raw = Environment.GetEnvironmentVariable(name);
    return int.TryParse(raw, out var value) && value >= minimum ? value : fallback;
}

static (string State, string Detail) ClassifyChatError(Exception ex)
{
    if (ex is OperationCanceledException) return (ModelState.Error, "timeout");

    if (ex is HttpRequestException http && http.StatusCode is { } status)
    {
        var code = (int)status;
        var state = code is 400 or 403 or 404 or 410 ? ModelState.Denied : ModelState.Error;
        return (state, $"HTTP {code}");
    }

    return (ModelState.Error, ex.Message.Split('\n')[0]);
}

// True when the API rejected the request because the conversation is longer
// than the model's context window.
static bool IsContextOverflow(Exception ex)
{
    if (ex is not HttpRequestException http) return false;
    if (http.StatusCode is not { } status) return false;
    if ((int)status is not (400 or 413)) return false;

    var message = ex.Message;
    return message.Contains("context", StringComparison.OrdinalIgnoreCase)
           || message.Contains("too long", StringComparison.OrdinalIgnoreCase)
           || message.Contains("maximum length", StringComparison.OrdinalIgnoreCase);
}

// Drops the oldest half of the history in pairs, so it still starts with a
// "user" message and the last exchange is kept.
static int TrimHistory(List<ChatMessage> history)
{
    var droppable = Math.Max(0, history.Count - 2);
    var drop = Math.Min(droppable, Math.Max(2, history.Count / 2));
    drop -= drop % 2;
    if (drop <= 0) return 0;

    history.RemoveRange(0, drop);
    return drop;
}

// Probes a batch of models (3 at a time), printing one line per model as it
// finishes. Used by --probe-all and by the "probe" / "retry" selector commands.
async Task<BatchResult> ProbeBatchAsync(IReadOnlyList<string> ids, int retries)
{
    var started = DateTimeOffset.Now;
    var results = new List<(string Id, ProbeResult Result)>();
    var total = ids.Count;
    string? fatal = null;

    using var gate = new SemaphoreSlim(3);
    using var abort = new CancellationTokenSource();

    await Task.WhenAll(ids.Select(async id =>
    {
        if (abort.IsCancellationRequested) return;
        await gate.WaitAsync();
        try
        {
            if (abort.IsCancellationRequested) return;

            var result = await ProbeModelAsync(new NimChat(http, id), probeTimeoutSeconds, retries, abort.Token);
            if (abort.IsCancellationRequested) return;

            if (result.Outcome == ProbeOutcome.Fatal)
            {
                fatal ??= $"{id}: {result.Detail}";
                abort.Cancel();
                return;
            }

            registry.ApplyProbe(id, result);

            lock (results)
            {
                results.Add((id, result));
                var label = result.Outcome switch
                {
                    ProbeOutcome.Responded => "OK",
                    ProbeOutcome.Denied => "DENIED",
                    _ => "ERROR"
                };
                var detail = result.Outcome == ProbeOutcome.Responded
                    ? $"({registry.DescribeReasoning(id)})"
                    : $"({result.Detail})";
                Console.WriteLine($"{label,-7} {id}  {detail}   [{results.Count}/{total}]");
            }
        }
        finally
        {
            gate.Release();
        }
    }));

    return new BatchResult(results, fatal, DateTimeOffset.Now - started, total);
}

static void PrintBatchSummary(BatchResult batch)
{
    var ok = batch.Results.Count(r => r.Result.Outcome == ProbeOutcome.Responded);
    var denied = batch.Results.Count(r => r.Result.Outcome == ProbeOutcome.Denied);
    var error = batch.Results.Count(r => r.Result.Outcome == ProbeOutcome.Transient);

    Console.WriteLine();
    Console.WriteLine($"OK: {ok}   DENIED: {denied}   ERROR: {error}   TOTAL: {batch.Results.Count}" +
                      $"{(batch.Results.Count < batch.Total ? $" of {batch.Total}" : "")}   " +
                      $"in {FormatDuration(batch.Elapsed)}");
}

static string FormatDuration(TimeSpan elapsed)
    => elapsed.TotalSeconds < 60
        ? $"{(int)elapsed.TotalSeconds}s"
        : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";

// Runs one probe with retries for transient failures.
static async Task<ProbeResult> ProbeModelAsync(NimChat nim, int timeoutSeconds, int retries, CancellationToken external = default)
{
    for (var attempt = 0; ; attempt++)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var result = await nim.ProbeAsync(cts.Token);
        if (external.IsCancellationRequested) return result;
        if (result.Outcome != ProbeOutcome.Transient || attempt >= retries) return result;

        try { await Task.Delay(TimeSpan.FromSeconds(2), external); }
        catch (OperationCanceledException) { return result; }
    }
}

static void PrintSelectorHelp()
{
    Console.WriteLine();
    Console.WriteLine("  Selector commands:");
    Console.WriteLine("    /list all       show the full catalog");
    Console.WriteLine("    /list working   show only the models marked ✓");
    Console.WriteLine("    /list failed    show only the models marked !");
    Console.WriteLine("    /list untested  show only the models marked ?");
    Console.WriteLine("    /list denied    show only the models marked ✗");
    Console.WriteLine();
    Console.WriteLine("    /probe all      test every model in the catalog");
    Console.WriteLine("    /probe failed   test only the models marked !");
    Console.WriteLine("    /probe denied   test only the models marked ✗");
    Console.WriteLine("    /probe 22       test the model at position 22");
    Console.WriteLine("    /probe <id>     test that model (a unique part of the id works too)");
    Console.WriteLine();
    Console.WriteLine("    /forget 22      drop the stored result: it goes back to ?");
    Console.WriteLine("    /quit           leave the selector");
    Console.WriteLine();
    Console.WriteLine("  /probe and /forget take the same words: all, working, failed,");
    Console.WriteLine("  untested, denied, a number, or an id. Sets ask for confirmation;");
    Console.WriteLine("  a single model does not.");
    Console.WriteLine("  Anything else is a number (position in the full catalog), an exact");
    Console.WriteLine("  model id, or a text to search by name. Enter keeps the current model.");
    Console.WriteLine();
}

static void PrintHelp()
{
    Console.WriteLine("""
        nim-hello - console chat client for NVIDIA NIM

        Usage:
          dotnet run                       interactive chat (with model selector)
          dotnet run -- "question"         one-shot question, no menu
          dotnet run -- --model <id>       use that model and skip the menu
          dotnet run -- --models           list the catalog and sync models.json
          dotnet run -- --probe            test 5 models
          dotnet run -- --probe-all        test the whole catalog and update models.json

        In the model selector:
          number                          pick that position in the full catalog
          text                            search by id
          /list <view>                    change the view: all, working, failed,
                                          untested or denied
          /probe <target>                 test now: a number, an id, or a view word
          /forget <target>                drop the stored result (back to ?)
          /help                           list the selector commands
          /quit (or q)                    quit

        In the chat:
          /model [id]                     switch model, keeping the conversation
          /thinking                       show or hide the reasoning
          /reset                          clear the conversation
          /quit                           exit

        Environment variables:
          NVIDIA_MODEL                  model to use (skips the menu)
          NVIDIA_MAX_TOKENS             max tokens per reply (default 2048)
          NIM_HOME                      folder for models.json / .model / .key
          NVIDIA_PROBE_TIMEOUT          seconds per probe attempt (default 30)
          NVIDIA_PROBE_RETRIES          retries for transient probe errors (default 1)
          NVIDIA_REPROBE_OK_DAYS        revalidate working models after N days (default 7)
          NVIDIA_REPROBE_ERROR_MINUTES  retry errored models after N minutes (default 60)
          NVIDIA_REPROBE_DENIED_DAYS    retry denied models after N days (default 30)
        """);
}

internal sealed class NimChat
{
    private const string Endpoint = "https://integrate.api.nvidia.com/v1/chat/completions";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly string _model;

    public NimChat(HttpClient http, string model)
    {
        _http = http;
        _model = model;
    }

    /// <summary>
    /// Quick reachability probe: stream a tiny request and stop at the first
    /// token (content or reasoning). Proves the model is enabled without
    /// waiting for a full answer.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var (text, reasoning) = await StreamAsync(
                [new ChatMessage("user", "ping")],
                onContent: _ => { },
                onReasoning: _ => { },
                maxTokens: 64,
                ct: ct,
                stopAfterFirstToken: true);

            var detail = text.Length == 0 && !reasoning ? "no content" : "";
            return new ProbeResult(ProbeOutcome.Responded, reasoning, detail);
        }
        catch (OperationCanceledException)
        {
            return new ProbeResult(ProbeOutcome.Transient, false, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return Classify(ex);
        }
        catch (Exception ex)
        {
            return new ProbeResult(ProbeOutcome.Transient, false, ex.Message.Split('\n')[0]);
        }
    }

    /// <summary>Streaming SSE request. Separates "thinking" (reasoning) from the reply.</summary>
    public async Task<(string Text, bool Reasoning)> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        Action<string> onContent,
        Action<string>? onReasoning = null,
        int maxTokens = 1024,
        CancellationToken ct = default,
        bool stopAfterFirstToken = false)
    {
        var payload = new
        {
            model = _model,
            messages = history,
            temperature = 0.6,
            max_tokens = maxTokens,
            stream = true
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(timeout.Token);
            throw new HttpRequestException(BuildHttpError((int)response.StatusCode, errorBody), null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var reply = new StringBuilder();
        var sawReasoning = false;
        var sawToken = false;

        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            // SSE format: "data: {json}" ... "data: [DONE]"
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") break;

            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;

            var delta = choices[0].GetProperty("delta");

            // Some models (Nemotron reasoning, DeepSeek) send "thinking" separately.
            var reasoning = GetText(delta, "reasoning_content");
            if (reasoning.Length == 0) reasoning = GetText(delta, "reasoning");
            if (reasoning.Length > 0)
            {
                sawReasoning = true;
                sawToken = true;
                onReasoning?.Invoke(reasoning);
            }

            var content = GetText(delta, "content");
            if (content.Length > 0)
            {
                reply.Append(content);
                sawToken = true;
                onContent(content);
            }

            if (stopAfterFirstToken && sawToken) break;
        }

        return (reply.ToString(), sawReasoning);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("https://integrate.api.nvidia.com/v1/models", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}", null, response.StatusCode);

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(m => m.GetProperty("id").GetString() ?? "")
            .Where(id => id.Length > 0)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetText(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static ProbeResult Classify(HttpRequestException ex)
    {
        if (ex.StatusCode is not { } status)
            return new ProbeResult(ProbeOutcome.Transient, false, ex.Message.Split('\n')[0]);

        var code = (int)status;
        var detail = $"HTTP {code}";
        return code switch
        {
            401 or 402 => new ProbeResult(ProbeOutcome.Fatal, false, detail),
            400 or 403 or 404 or 410 => new ProbeResult(ProbeOutcome.Denied, false, detail),
            _ => new ProbeResult(ProbeOutcome.Transient, false, detail)
        };
    }

    private string BuildHttpError(int status, string body)
    {
        var hint = status switch
        {
            401 or 403 => "Invalid key or no permission for this model.",
            402 => "No credit on the account.",
            404 => $"Model '{_model}' is not enabled for your account. Use /model or --probe-all.",
            410 => $"Model '{_model}' is no longer available. Use /model or --probe-all.",
            429 => "Rate limit: wait a few seconds and retry.",
            _ => ""
        };

        return hint.Length == 0
            ? $"HTTP {status}: {body}"
            : $"HTTP {status}: {body}{Environment.NewLine}Hint: {hint}";
    }
}

/// <summary>models.json: external list of models and whether they work with this account.</summary>
internal sealed class ModelRegistry
{
    public const int SchemaVersion = 2;
    private const int MinCatalogSize = 10;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    private readonly Dictionary<string, ModelEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private ModelRegistry(string path) => _path = path;

    public static ModelRegistry Load(string path)
    {
        var registry = new ModelRegistry(path);
        if (!File.Exists(path)) return registry;

        try
        {
            var file = JsonSerializer.Deserialize<ModelFile>(File.ReadAllText(path), JsonOpts);

            // Old or unknown schemas are backed up and ignored; they are not migrated.
            if (file?.SchemaVersion != SchemaVersion)
            {
                Backup(path);
                Console.Error.WriteLine($"[warning] {Path.GetFileName(path)} uses an old format; " +
                                        $"backed up to {Path.GetFileName(path)}.bak and started fresh.");
                return registry;
            }

            foreach (var entry in file.Models ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry.Id)) continue;
                entry.Id = entry.Id.Trim();
                if (entry.State is not (ModelState.Ok or ModelState.Denied or ModelState.Error or ModelState.Unprobed))
                    entry.State = ModelState.Unprobed;
                registry._entries[entry.Id] = entry;
            }
        }
        catch (Exception ex)
        {
            Backup(path);
            Console.Error.WriteLine($"[warning] could not read {Path.GetFileName(path)}: {ex.Message} " +
                                    $"(backed up to {Path.GetFileName(path)}.bak)");
        }

        return registry;
    }

    public IReadOnlyList<string> Ids => _entries.Keys
        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public IReadOnlyList<string> SupportedIds => _entries.Values
        .Where(e => e.State == ModelState.Ok)
        .Select(e => e.Id)
        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public bool IsSupported(string id) => _entries.TryGetValue(id, out var entry) && entry.State == ModelState.Ok;

    public bool IsError(string id) => _entries.TryGetValue(id, out var entry) && entry.State == ModelState.Error;

    public bool IsDenied(string id) => _entries.TryGetValue(id, out var entry) && entry.State == ModelState.Denied;

    public bool IsUntested(string id) => !_entries.TryGetValue(id, out var entry) || entry.State == ModelState.Unprobed;

    public bool IsInCatalog(string id) => !_entries.TryGetValue(id, out var entry) || entry.InCatalog;

    /// <summary>
    /// Marker for the list: state (?, ✓, ✗, !, ∅) and reasoning (R, NR, -).
    /// </summary>
    public string GetMarker(string id)
    {
        if (!_entries.TryGetValue(id, out var entry)) return "? -";

        var state = !entry.InCatalog ? "∅" : entry.State switch
        {
            ModelState.Ok => "✓",
            ModelState.Denied => "✗",
            ModelState.Error => "!",
            _ => "?"
        };

        var reasoning = entry.Reasoning switch
        {
            true => "R",
            false => "NR",
            null => "-"
        };

        return $"{state} {reasoning}";
    }

    /// <summary>Human-readable reasoning status for the model header line.</summary>
    public string DescribeReasoning(string id)
    {
        if (!_entries.TryGetValue(id, out var entry)) return "reasoning: unknown";
        return entry.Reasoning switch
        {
            true => "reasoning: yes",
            false => "reasoning: no",
            null => "reasoning: unknown"
        };
    }

    /// <summary>Compact age of the last probe: "5m", "3h", "9d" or "n/a".</summary>
    public string GetAge(string id)
    {
        if (!_entries.TryGetValue(id, out var entry) || entry.LastProbe is null) return "n/a";

        var age = DateTimeOffset.Now - entry.LastProbe.Value;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalMinutes < 60) return $"{Math.Max(1, (int)age.TotalMinutes)}m";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }

    /// <summary>True when the last result is older than its state's TTL.</summary>
    public bool IsStale(string id, TimeSpan okTtl, TimeSpan errorTtl, TimeSpan deniedTtl)
    {
        if (!_entries.TryGetValue(id, out var entry) || entry.LastProbe is null) return false;

        var age = DateTimeOffset.Now - entry.LastProbe.Value;
        return entry.State switch
        {
            ModelState.Ok => age > okTtl,
            ModelState.Error => age > errorTtl,
            ModelState.Denied => age > deniedTtl,
            _ => false
        };
    }

    /// <summary>Untested models and stale results must be probed before use.</summary>
    public bool NeedsProbe(string id, TimeSpan okTtl, TimeSpan errorTtl, TimeSpan deniedTtl)
    {
        if (!_entries.TryGetValue(id, out var entry)) return true;
        if (entry.State == ModelState.Unprobed || entry.LastProbe is null) return true;
        return IsStale(id, okTtl, errorTtl, deniedTtl);
    }

    /// <summary>Adds missing catalog ids and flags entries that left the catalog.</summary>
    public (int Added, bool Changed) SyncCatalog(IReadOnlyList<string> catalog)
    {
        if (catalog.Count < MinCatalogSize) return (0, false);

        var ids = new HashSet<string>(catalog, StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var id in catalog)
        {
            if (string.IsNullOrWhiteSpace(id) || _entries.ContainsKey(id)) continue;
            _entries[id] = new ModelEntry { Id = id, State = ModelState.Unprobed, InCatalog = true };
            added++;
        }

        var changed = added > 0;
        foreach (var entry in _entries.Values)
        {
            var inCatalog = ids.Contains(entry.Id);
            if (entry.InCatalog == inCatalog) continue;
            entry.InCatalog = inCatalog;
            changed = true;
        }

        return (added, changed);
    }

    /// <summary>Stores a probe result. Fatal errors are not recorded.</summary>
    public void ApplyProbe(string id, ProbeResult result)
    {
        if (result.Outcome == ProbeOutcome.Fatal) return;

        var entry = GetOrAdd(id);
        entry.LastProbe = DateTimeOffset.Now;

        switch (result.Outcome)
        {
            case ProbeOutcome.Responded:
                entry.State = ModelState.Ok;
                entry.Reasoning = result.Reasoning;
                entry.LastError = result.Detail.Length > 0 ? result.Detail : null;
                break;
            case ProbeOutcome.Denied:
                entry.State = ModelState.Denied;
                entry.LastError = result.Detail;
                break;
            default:
                entry.State = ModelState.Error;
                entry.LastError = result.Detail;
                break;
        }
    }

    /// <summary>Records a successful chat reply. Returns true when the visible state changed.</summary>
    public bool MarkChatSuccess(string id, bool reasoning)
    {
        var entry = GetOrAdd(id);
        var changed = entry.State != ModelState.Ok || entry.Reasoning != reasoning;

        entry.State = ModelState.Ok;
        entry.Reasoning = reasoning;
        entry.LastError = null;
        entry.LastProbe = DateTimeOffset.Now;
        return changed;
    }

    /// <summary>Records a failed chat request. Returns true when the visible state changed.</summary>
    public bool MarkChatError(string id, string state, string detail)
    {
        var entry = GetOrAdd(id);
        var changed = entry.State != state || entry.LastError != detail;

        entry.State = state;
        entry.LastError = detail;
        entry.LastProbe = DateTimeOffset.Now;
        return changed;
    }

    /// <summary>
    /// Drops the stored result: the model goes back to "?" and is probed again
    /// when it is selected. Returns false when there was nothing to forget.
    /// </summary>
    public bool Forget(string id)
    {
        if (!_entries.TryGetValue(id, out var entry)) return false;

        var hadResult = entry.State != ModelState.Unprobed
                        || entry.LastProbe is not null
                        || entry.LastError is not null;

        entry.State = ModelState.Unprobed;
        entry.Reasoning = null;
        entry.LastProbe = null;
        entry.LastError = null;
        return hadResult;
    }

    public void Save()
    {
        try
        {
            var file = new ModelFile
            {
                SchemaVersion = SchemaVersion,
                Updated = DateTimeOffset.Now.ToString("O"),
                Models = _entries.Values
                    .OrderBy(e => e.State switch
                    {
                        ModelState.Ok => 0,
                        ModelState.Denied => 1,
                        ModelState.Error => 2,
                        _ => 3
                    })
                    .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
            File.WriteAllText(_path, JsonSerializer.Serialize(file, JsonOpts));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[warning] could not save {Path.GetFileName(_path)}: {ex.Message}");
        }
    }

    private ModelEntry GetOrAdd(string id)
    {
        if (_entries.TryGetValue(id, out var entry)) return entry;
        entry = new ModelEntry { Id = id, InCatalog = true };
        _entries[id] = entry;
        return entry;
    }

    private static void Backup(string path)
    {
        try { File.Copy(path, path + ".bak", overwrite: true); } catch { /* not critical */ }
    }
}

internal static class ModelState
{
    public const string Ok = "ok";             // tested and it works
    public const string Denied = "denied";     // rejected for this account (400/403/404)
    public const string Error = "error";       // timeout, 429, 5xx or network failure
    public const string Unprobed = "unprobed"; // in the catalog, never tested
}

internal enum ProbeOutcome
{
    Responded,
    Denied,
    Transient,
    Fatal
}

internal sealed record ProbeResult(ProbeOutcome Outcome, bool Reasoning, string Detail);

/// <summary>Outcome of probing a batch of models.</summary>
internal sealed record BatchResult(List<(string Id, ProbeResult Result)> Results, string? Fatal, TimeSpan Elapsed, int Total);

internal sealed class ModelFile
{
    public int SchemaVersion { get; set; }
    public string? Updated { get; set; }
    public List<ModelEntry> Models { get; set; } = [];
}

internal sealed class ModelEntry
{
    public string Id { get; set; } = "";
    public string State { get; set; } = ModelState.Unprobed;

    /// <summary>True = the model emits reasoning, false = it does not, null = not observed yet.</summary>
    public bool? Reasoning { get; set; }

    /// <summary>Human note; left untouched by the app.</summary>
    public string? Note { get; set; }

    /// <summary>Detail of the last failed probe or chat request.</summary>
    public string? LastError { get; set; }

    /// <summary>When the model was last probed or used successfully.</summary>
    public DateTimeOffset? LastProbe { get; set; }

    /// <summary>False when the id disappeared from the official catalog.</summary>
    public bool InCatalog { get; set; } = true;
}

internal sealed record ChatMessage(string Role, string Content);
