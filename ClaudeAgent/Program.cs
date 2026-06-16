using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("ERROR: ANTHROPIC_API_KEY is not set.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Set it in PowerShell with:");
    Console.Error.WriteLine("  $env:ANTHROPIC_API_KEY = \"sk-ant-...\"");
    Console.Error.WriteLine("or add it to Properties/launchSettings.json so F5 picks it up.");
    return 1;
}

const string Model = "claude-haiku-4-5-20251001";
const string SystemPrompt =
    "You are a helpful assistant. Use the available tools when the user asks about " +
    "weather, math, or wants to list or upload notes from the Apple Notes app on this " +
    "Mac (which syncs the user's iPhone notes via iCloud). When the user wants to upload " +
    "a note but you don't know its exact title, call list_notes first. Be concise.";

// Endpoint that notes are uploaded to. Must be configured by the user.
var noteUploadEndpoint = Environment.GetEnvironmentVariable("NOTE_UPLOAD_ENDPOINT");

using var http = new HttpClient { BaseAddress = new Uri("https://api.anthropic.com/") };
http.DefaultRequestHeaders.Add("x-api-key", apiKey);
http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

// Separate client for uploading notes to the configured endpoint.
using var uploadHttp = new HttpClient();

var tools = new JsonArray
{
    Tool("get_weather", "Get the current weather for a given city.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["location"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "City and country, e.g. 'Rome, Italy'",
                },
            },
            ["required"] = new JsonArray { "location" },
        }),
    Tool("calculate", "Evaluate a mathematical expression and return the numeric result.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["expression"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "A math expression, e.g. '2 + 2' or '(3.14 * 10 * 10)'",
                },
            },
            ["required"] = new JsonArray { "expression" },
        }),
    Tool("list_notes", "List the titles of notes in the Apple Notes app on this Mac (synced from the user's iPhone via iCloud).",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        }),
    Tool("upload_note", "Read a note from the Apple Notes app on this Mac (by its title) and upload it to the configured upload endpoint.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["title"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Exact title (name) of the note in Apple Notes, e.g. 'Shopping list'",
                },
            },
            ["required"] = new JsonArray { "title" },
        }),
};

var messages = new JsonArray();

Console.WriteLine("Claude Agent (C#) — type 'exit' to quit.");
Console.WriteLine("Available tools: get_weather, calculate, list_notes, upload_note");
if (!OperatingSystem.IsMacOS())
    Console.WriteLine("Note: list_notes/upload_note read Apple Notes via AppleScript and only work on macOS.");
if (string.IsNullOrWhiteSpace(noteUploadEndpoint))
    Console.WriteLine("Note: NOTE_UPLOAD_ENDPOINT is not set — upload_note will be unavailable until you configure it.");
else
    Console.WriteLine($"Notes will be uploaded to: {noteUploadEndpoint}");
Console.WriteLine(new string('-', 50));

while (true)
{
    Console.Write("\nYou: ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input) || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    messages.Add(new JsonObject { ["role"] = "user", ["content"] = input });

    var response = await SendAsync();
    if (response is null) { messages.RemoveAt(messages.Count - 1); continue; }

    while (response["stop_reason"]?.GetValue<string>() == "tool_use")
    {
        messages.Add(new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = response["content"]!.DeepClone(),
        });

        var toolResults = new JsonArray();
        foreach (var block in response["content"]!.AsArray())
        {
            if (block?["type"]?.GetValue<string>() != "tool_use") continue;

            var name = block["name"]!.GetValue<string>();
            var id = block["id"]!.GetValue<string>();
            var toolInput = block["input"]!;

            Console.WriteLine($"  [tool] {name}({toolInput.ToJsonString()})");
            var result = await ExecuteTool(name, toolInput);
            Console.WriteLine($"  [result] {result}");

            toolResults.Add(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = id,
                ["content"] = result,
            });
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });

        response = await SendAsync();
        if (response is null) goto nextPrompt;
    }

    messages.Add(new JsonObject
    {
        ["role"] = "assistant",
        ["content"] = response["content"]!.DeepClone(),
    });

    var text = string.Concat(response["content"]!.AsArray()
        .Where(b => b?["type"]?.GetValue<string>() == "text")
        .Select(b => b!["text"]!.GetValue<string>()));

    Console.WriteLine($"\nClaude: {text}");
    nextPrompt:;
}

Console.WriteLine("\nGoodbye!");
return 0;

async Task<JsonNode?> SendAsync()
{
    var body = new JsonObject
    {
        ["model"] = Model,
        ["max_tokens"] = 1024,
        ["system"] = SystemPrompt,
        ["messages"] = messages.DeepClone(),
        ["tools"] = tools.DeepClone(),
    };

    try
    {
        using var resp = await http.PostAsJsonAsync("v1/messages", body);
        var json = await resp.Content.ReadFromJsonAsync<JsonNode>();
        if (!resp.IsSuccessStatusCode)
        {
            var msg = json?["error"]?["message"]?.GetValue<string>() ?? $"HTTP {(int)resp.StatusCode}";
            Console.WriteLine($"\n[API error] {msg}");
            return null;
        }
        return json;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[Network error] {ex.Message}");
        return null;
    }
}

static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
{
    ["name"] = name,
    ["description"] = description,
    ["input_schema"] = inputSchema,
};

async Task<string> ExecuteTool(string name, JsonNode input)
{
    return name switch
    {
        "get_weather" => GetWeather(input["location"]?.GetValue<string>() ?? "unknown"),
        "calculate" => Calculate(input["expression"]?.GetValue<string>() ?? "0"),
        "list_notes" => ListNotes(),
        "upload_note" => await UploadNote(input["title"]?.GetValue<string>() ?? ""),
        _ => $"Unknown tool: {name}",
    };
}

// Reads the titles of all notes from the Apple Notes app via AppleScript.
string ListNotes()
{
    const string script =
        "tell application \"Notes\"\n" +
        "    set out to \"\"\n" +
        "    repeat with n in notes\n" +
        "        set out to out & (name of n) & linefeed\n" +
        "    end repeat\n" +
        "    return out\n" +
        "end tell";

    var (ok, output, error) = RunOsascript(script);
    if (!ok)
        return $"Error listing notes: {error}";

    var titles = output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToArray();
    return JsonSerializer.Serialize(new { count = titles.Length, titles });
}

// Reads a note's plain text from Apple Notes (by title) and uploads it to the endpoint.
async Task<string> UploadNote(string title)
{
    if (string.IsNullOrWhiteSpace(noteUploadEndpoint))
        return "Error: NOTE_UPLOAD_ENDPOINT is not configured. Set it to the upload URL and restart.";
    if (string.IsNullOrWhiteSpace(title))
        return "Error: no note title provided.";

    const string script =
        "on run argv\n" +
        "    set theTitle to item 1 of argv\n" +
        "    tell application \"Notes\"\n" +
        "        set matches to (notes whose name is theTitle)\n" +
        "        if (count of matches) is 0 then return \"__NOT_FOUND__\"\n" +
        "        return plaintext of item 1 of matches\n" +
        "    end tell\n" +
        "end run";

    var (ok, content, error) = RunOsascript(script, title);
    if (!ok)
        return $"Error reading note from Apple Notes: {error}";
    if (content.TrimEnd() == "__NOT_FOUND__")
        return $"No note titled '{title}' found in Apple Notes. Use list_notes to see available titles.";

    var payload = new JsonObject
    {
        ["title"] = title,
        ["content"] = content,
    };

    try
    {
        using var resp = await uploadHttp.PostAsJsonAsync(noteUploadEndpoint, payload);
        if (!resp.IsSuccessStatusCode)
            return $"Upload failed: HTTP {(int)resp.StatusCode} from {noteUploadEndpoint}";
        return JsonSerializer.Serialize(new
        {
            uploaded = true,
            title,
            bytes = content.Length,
            endpoint = noteUploadEndpoint,
        });
    }
    catch (Exception ex)
    {
        return $"Upload error: {ex.Message}";
    }
}

// Runs an AppleScript via osascript, passing any extra args to the script's `on run argv`.
static (bool ok, string output, string error) RunOsascript(string script, params string[] args)
{
    if (!OperatingSystem.IsMacOS())
        return (false, "", "Apple Notes access requires macOS (osascript).");

    var psi = new ProcessStartInfo
    {
        FileName = "osascript",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add("-e");
    psi.ArgumentList.Add(script);
    foreach (var arg in args)
        psi.ArgumentList.Add(arg);

    try
    {
        using var proc = Process.Start(psi);
        if (proc is null)
            return (false, "", "Failed to start osascript.");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        return proc.ExitCode == 0
            ? (true, stdout, "")
            : (false, stdout, string.IsNullOrWhiteSpace(stderr) ? $"osascript exited with {proc.ExitCode}" : stderr.Trim());
    }
    catch (Exception ex)
    {
        return (false, "", ex.Message);
    }
}

static string GetWeather(string location)
{
    var random = new Random(location.GetHashCode());
    var temp = random.Next(-10, 40);
    var conditions = new[] { "Sunny", "Cloudy", "Rainy", "Partly cloudy", "Windy", "Snowy" };
    var condition = conditions[random.Next(conditions.Length)];
    return JsonSerializer.Serialize(new { location, temperature_c = temp, condition });
}

static string Calculate(string expression)
{
    try
    {
        var result = new System.Data.DataTable().Compute(expression, null);
        return result?.ToString() ?? "error";
    }
    catch (Exception ex)
    {
        return $"Error: {ex.Message}";
    }
}
