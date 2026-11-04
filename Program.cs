using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected console */ }

const string DefaultModel = "minimaxai/minimax-m3.1-flash";
const int VerifyTimeoutSeconds = 30;

// Folder where models.json, .model and .key live (current directory by default).
var nimHome = Environment.GetEnvironmentVariable("NIM_HOME");
if (string.IsNullOrWhiteSpace(nimHome)) nimHome = Directory.GetCurrentDirectory();
nimHome = Path.GetFullPath(nimHome);

var modelsPath = Path.Combine(nimHome, "models.json");
var lastModelPath = Path.Combine(nimHome, ".model");
var keyPath = Path.Combine(nimHome, ".key");

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
var maxTokens = int.TryParse(Environment.GetEnvironmentVariable("NVIDIA_MAX_TOKENS"), out var mt) && mt > 0
    ? mt
    : 2048;

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
        var added = registry.SyncCatalog(ids);
        if (added > 0)
        {
            registry.Save();
            Console.Error.WriteLine($"[models.json] +{added} new models from the catalog (not tested).");
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var probe = new NimChat(http, candidate);
            var result = await probe.SendAsync([new ChatMessage("user", "ping")], maxTokens: 16, ct: cts.Token);
            Console.WriteLine($"OK      {candidate}  ({DescribeReasoning(result.Reasoning)})");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"TIMEOUT {candidate}  (no response within 15s)");
        }
        catch (Exception ex)
        {
            var detail = ex.Message.Split('\n')[0];
            Console.WriteLine($"FAIL    {candidate}  ->  {detail}");
        }
    }

    return 0;
}

if (argv.Count == 1 && argv[0] is "--probe-all" or "-pa")
{
    var ids = await api.ListModelsAsync();
    var results = new System.Collections.Concurrent.ConcurrentBag<(string Id, string Status, bool Reasoning)>();
    using var gate = new SemaphoreSlim(5);

    await Task.WhenAll(ids.Select(async id =>
    {
        await gate.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var probe = new NimChat(http, id);
            var result = await probe.SendAsync([new ChatMessage("user", "ping")], maxTokens: 16, ct: cts.Token);
            results.Add((id, "OK", result.Reasoning));
        }
        catch (OperationCanceledException)
        {
            results.Add((id, "TIMEOUT", false));
        }
        catch
        {
            results.Add((id, "FAIL", false));
        }
        finally
        {
            gate.Release();
        }
    }));

    foreach (var r in results
                 .OrderBy(r => r.Status == "OK" ? 0 : r.Status == "TIMEOUT" ? 1 : 2)
                 .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
    {
        var reasoning = r.Status == "OK" ? $"  ({DescribeReasoning(r.Reasoning)})" : "";
        Console.WriteLine($"{r.Status,-8} {r.Id}{reasoning}");
    }

    registry.ApplyProbe(results);
    registry.Save();

    Console.WriteLine();
    Console.WriteLine($"OK: {results.Count(r => r.Status == "OK")}   " +
                      $"TIMEOUT: {results.Count(r => r.Status == "TIMEOUT")}   " +
                      $"FAIL: {results.Count(r => r.Status == "FAIL")}   " +
                      $"TOTAL: {results.Count}");
    Console.WriteLine($"models.json updated: {registry.SupportedIds.Count} supported.");
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

        var wasSupported = registry.IsSupported(model);
        var changed = false;
        if (reply.Text.Trim().Length > 0 && !wasSupported)
        {
            registry.Mark(model, true, reasoning: reply.Reasoning);
            changed = true;
        }
        else if (registry.SetReasoning(model, reply.Reasoning))
        {
            changed = true;
        }

        if (changed)
        {
            registry.Save();
            Console.Error.WriteLine(wasSupported
                ? $"\n[models.json] {model} updated: {registry.DescribeReasoning(model)}."
                : $"\n[models.json] {model} responded: marked as working ({registry.DescribeReasoning(model)}).");
        }

        return reply.Text.Trim().Length == 0 ? 2 : 0;
    }
    catch (OperationCanceledException)
    {
        Console.ResetColor();
        Console.Error.WriteLine("\n[timeout] the model did not respond within 120s.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.ResetColor();
        Console.Error.WriteLine($"\n[error] {ex.Message}");
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

Console.WriteLine();
Console.WriteLine($"Model: {model}  (max_tokens: {maxTokens}, {registry.DescribeReasoning(model)})");
Console.WriteLine("Type your message. Commands: /model (switch), /thinking, /reset, /quit.");
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

    if (line == "/model")
    {
        var picked = await SelectModelAsync(model);
        if (picked is null)
        {
            Console.WriteLine("(unchanged)\n");
            continue;
        }
        model = picked;
        chat = new NimChat(http, model);
        chatHistory.Clear();
        SaveLastModel(lastModelPath, model);
        Console.WriteLine($"(model: {model}; history cleared)\n");
        continue;
    }

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
            continue;
        }

        var wasSupported = registry.IsSupported(model);
        var changed = false;
        if (!wasSupported)
        {
            registry.Mark(model, true, reasoning: reply.Reasoning);
            changed = true;
        }
        else if (registry.SetReasoning(model, reply.Reasoning))
        {
            changed = true;
        }

        if (changed)
        {
            registry.Save();
            Console.WriteLine(wasSupported
                ? $"\n[models.json] {model} updated: {registry.DescribeReasoning(model)}."
                : $"\n[models.json] {model} responded: marked as working ({registry.DescribeReasoning(model)}).");
        }

        chatHistory.Add(new ChatMessage("assistant", reply.Text));
        Console.WriteLine("\n");
    }
    catch (OperationCanceledException)
    {
        Console.ResetColor();
        chatHistory.RemoveAt(chatHistory.Count - 1);
        Console.Error.WriteLine("\n[timeout] the model did not respond within 120s. Try again or use /model.\n");
    }
    catch (Exception ex)
    {
        Console.ResetColor();
        chatHistory.RemoveAt(chatHistory.Count - 1);
        Console.Error.WriteLine($"\n[error] {ex.Message}\n");
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
        var added = registry.SyncCatalog(catalogCache);
        if (added > 0)
        {
            registry.Save();
            Console.WriteLine($"(models.json: +{added} new models from the official catalog, not tested yet)");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[warning] could not read the official NVIDIA catalog: {ex.Message}");
        catalogCache = [];
    }

    return catalogCache;
}

// --- Model selector ---
async Task<string?> SelectModelAsync(string menuDefault)
{
    var catalog = await GetCatalogAsync();

    var all = catalog.Concat(registry.Ids)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
        .ToList();

    var mode = "supported";
    var search = "";

    while (true)
    {
        List<string> shown;
        string header;

        if (mode == "all")
        {
            shown = all;
            header = $"Full catalog ({all.Count}) — ✔ works | ✘ failed | blank = untested";
        }
        else if (mode == "search")
        {
            shown = all.Where(id => id.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
            header = $"Results for \"{search}\" ({shown.Count})";
        }
        else
        {
            shown = registry.SupportedIds.ToList();
            if (shown.Count == 0)
            {
                shown = all;
                header = $"No tested models yet — full catalog ({all.Count})";
            }
            else
            {
                header = $"Models that work with your account ({shown.Count})";
            }
        }

        PrintModelList(shown, header, registry.GetMarker);
        Console.WriteLine($"  Enter = {menuDefault}");
        Console.WriteLine($"  number = select · all = full catalog ({all.Count}) · text = search · q = quit");
        Console.Write("model> ");

        var input = (Console.ReadLine() ?? "").Trim();

        if (input.Length == 0) input = menuDefault;
        if (input is "q" or "Q" or "quit") return null;

        if (input.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            mode = "all";
            continue;
        }

        if (int.TryParse(input, out var n))
        {
            if (n < 1 || n > shown.Count)
            {
                Console.WriteLine($"  There is no number {n} (the list has {shown.Count}).");
                Console.WriteLine();
                continue;
            }
            var picked = shown[n - 1];
            if (await EnsureSupportedAsync(picked)) return picked;
            mode = "supported";
            continue;
        }

        var exact = all.FirstOrDefault(id => id.Equals(input, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            if (await EnsureSupportedAsync(exact)) return exact;
            mode = "supported";
            continue;
        }

        var matches = all.Where(id => id.Contains(input, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            Console.WriteLine($"  No results for \"{input}\". Try another text or type all.");
            Console.WriteLine();
            continue;
        }
        if (matches.Count == 1)
        {
            Console.WriteLine($"  → {matches[0]}");
            if (await EnsureSupportedAsync(matches[0])) return matches[0];
            mode = "supported";
            continue;
        }

        mode = "search";
        search = input;
    }
}

// If the model is not known to work, test it. If it responds, save it to models.json.
async Task<bool> EnsureSupportedAsync(string id)
{
    if (registry.IsSupported(id)) return true;

    Console.WriteLine();
    Console.Write($"  Testing {id} (max {VerifyTimeoutSeconds}s)... ");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(VerifyTimeoutSeconds));

    try
    {
        var probe = new NimChat(http, id);
        var result = await probe.SendAsync([new ChatMessage("user", "Reply with just: ok")], maxTokens: 32, ct: cts.Token);
        registry.Mark(id, true, reasoning: result.Reasoning);
        registry.Save();
        Console.WriteLine($"✔ responds ({registry.DescribeReasoning(id)}). Saved to models.json.");
        return true;
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine($"✘ no response within {VerifyTimeoutSeconds}s. Marked as failed.");
        registry.Mark(id, false, "timeout");
        registry.Save();
        return false;
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"✘ {ex.Message.Split('\n')[0]}");
        if (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            registry.Mark(id, false, $"HTTP {(int)ex.StatusCode}");
            registry.Save();
            Console.WriteLine("  Saved as failed in models.json.");
        }
        return false;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"✘ {ex.Message.Split('\n')[0]}");
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

static void PrintModelList(IReadOnlyList<string> ids, string header, Func<string, string> marker)
{
    Console.WriteLine();
    Console.WriteLine(header + ":");

    if (ids.Count == 0)
    {
        Console.WriteLine("  (empty list)");
        Console.WriteLine();
        return;
    }

    var numberWidth = ids.Count.ToString().Length;
    var cells = new List<string>(ids.Count);
    for (var i = 0; i < ids.Count; i++)
        cells.Add((i + 1).ToString().PadLeft(numberWidth) + $". {marker(ids[i])} {ids[i]}");

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
    Console.WriteLine("  R = reasoning | - = no reasoning | ? = unknown");
    Console.WriteLine();
}

static int SafeWidth()
{
    try { return Console.WindowWidth > 0 ? Console.WindowWidth : 120; }
    catch { return 120; }
}

static string DescribeReasoning(bool reasoning) => reasoning ? "reasoning: yes" : "reasoning: no";

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

static void PrintHelp()
{
    Console.WriteLine("""
        nim-hello — console chat client for NVIDIA NIM

        Usage:
          dotnet run                       interactive chat (with model selector)
          dotnet run -- "question"         one-shot question, no menu
          dotnet run -- --model <id>       use that model and skip the menu
          dotnet run -- --models           list the catalog (85 ids)
          dotnet run -- --probe            test 5 models
          dotnet run -- --probe-all        test the whole catalog and update models.json

        Environment variables:
          NVIDIA_MODEL       model to use (skips the menu)
          NVIDIA_MAX_TOKENS  max tokens per reply (default 2048)
          NIM_HOME           folder for models.json / .model / .key
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

    /// <summary>Regular request (no streaming). Used to test models.</summary>
    public async Task<(string Text, bool Reasoning)> SendAsync(
        IReadOnlyList<ChatMessage> history,
        int maxTokens = 500,
        CancellationToken ct = default)
    {
        var payload = new
        {
            model = _model,
            messages = history,
            temperature = 0.6,
            max_tokens = maxTokens,
            stream = false
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(Endpoint, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(BuildHttpError((int)response.StatusCode, body), null, response.StatusCode);

        using var doc = JsonDocument.Parse(body);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");

        var reasoning = GetText(message, "reasoning_content").Length > 0
                        || GetText(message, "reasoning").Length > 0
                        || HasReasoningTokens(doc.RootElement);

        return (GetText(message, "content"), reasoning);
    }

    /// <summary>Streaming SSE request. Separates "thinking" (reasoning) from the reply.</summary>
    public async Task<(string Text, bool Reasoning)> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        Action<string> onContent,
        Action<string>? onReasoning = null,
        int maxTokens = 1024,
        CancellationToken ct = default)
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
                onReasoning?.Invoke(reasoning);
            }

            var content = GetText(delta, "content");
            if (content.Length > 0)
            {
                reply.Append(content);
                onContent(content);
            }
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

    /// <summary>True when usage.completion_tokens_details.reasoning_tokens is greater than zero.</summary>
    private static bool HasReasoningTokens(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)) return false;
        if (!usage.TryGetProperty("completion_tokens_details", out var details)) return false;
        if (!details.TryGetProperty("reasoning_tokens", out var tokens)) return false;
        return tokens.ValueKind == JsonValueKind.Number && tokens.TryGetInt32(out var count) && count > 0;
    }

    private string BuildHttpError(int status, string body)
    {
        var hint = status switch
        {
            401 or 403 => "Invalid key or no permission for this model.",
            402 => "No credit on the account.",
            404 => $"Model '{_model}' is not enabled for your account. Use /model or --probe-all.",
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
            foreach (var entry in file?.Models ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry.Id)) continue;
                entry.Id = entry.Id.Trim();
                if (entry.State is not (ModelState.Ok or ModelState.Failed or ModelState.Unprobed))
                    entry.State = ModelState.Unprobed;
                registry._entries[entry.Id] = entry;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[warning] could not read {Path.GetFileName(path)}: {ex.Message}");
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

    /// <summary>
    /// Marker for the list: state (✔ works, ✘ failed, space = untested) plus
    /// reasoning (R = reasoning, - = no reasoning, ? = unknown).
    /// </summary>
    public string GetMarker(string id)
    {
        if (!_entries.TryGetValue(id, out var entry)) return "  ";

        var state = entry.State switch
        {
            ModelState.Ok => "✔",
            ModelState.Failed => "✘",
            _ => " "
        };

        var reasoning = entry.Reasoning switch
        {
            true => "R",
            false => "-",
            null => "?"
        };

        return state + reasoning;
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

    /// <summary>Stores whether the model exposes reasoning. Returns true when the value changed.</summary>
    public bool SetReasoning(string id, bool reasoning)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            entry = new ModelEntry { Id = id };
            _entries[id] = entry;
        }

        if (entry.Reasoning == reasoning) return false;
        entry.Reasoning = reasoning;
        return true;
    }

    /// <summary>Adds any missing official catalog ids as "unprobed".</summary>
    public int SyncCatalog(IEnumerable<string> catalog)
    {
        var added = 0;
        foreach (var id in catalog)
        {
            if (string.IsNullOrWhiteSpace(id) || _entries.ContainsKey(id)) continue;
            _entries[id] = new ModelEntry { Id = id, State = ModelState.Unprobed };
            added++;
        }
        return added;
    }

    public void Mark(string id, bool supported, string? note = null, bool? reasoning = null)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            entry = new ModelEntry { Id = id };
            _entries[id] = entry;
        }

        entry.State = supported ? ModelState.Ok : ModelState.Failed;
        entry.Note = supported ? null : note;
        if (reasoning.HasValue) entry.Reasoning = reasoning;
    }

    public void ApplyProbe(IEnumerable<(string Id, string Status, bool Reasoning)> results)
    {
        foreach (var (id, status, reasoning) in results)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                entry = new ModelEntry { Id = id };
                _entries[id] = entry;
            }

            entry.State = status == "OK" ? ModelState.Ok : ModelState.Failed;
            if (status != "OK")
            {
                entry.Note = status == "TIMEOUT" ? "timeout" : "no response";
            }
            else
            {
                entry.Reasoning = reasoning;
            }
        }
    }

    public void Save()
    {
        try
        {
            var file = new ModelFile
            {
                Updated = DateTimeOffset.Now.ToString("O"),
                Models = _entries.Values
                    .OrderBy(e => e.State switch
                    {
                        ModelState.Ok => 0,
                        ModelState.Failed => 1,
                        _ => 2
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
}

internal static class ModelState
{
    public const string Ok = "ok";             // tested and it works
    public const string Failed = "failed";     // tested and it does not work
    public const string Unprobed = "unprobed"; // in the catalog, never tested
}

internal sealed class ModelFile
{
    public string? Updated { get; set; }
    public List<ModelEntry> Models { get; set; } = [];
}

internal sealed class ModelEntry
{
    public string Id { get; set; } = "";
    public string State { get; set; } = ModelState.Unprobed;

    /// <summary>True = the model emits reasoning, false = it does not, null = not observed yet.</summary>
    public bool? Reasoning { get; set; }

    public string? Note { get; set; }
}

internal sealed record ChatMessage(string Role, string Content);
