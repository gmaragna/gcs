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
    "weather, math, or wants to upload a note from this device. Be concise in your answers.";

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
    Tool("upload_note", "Upload a note (text file) from this device to the configured upload endpoint.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["path"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Path to the note file on this device, e.g. '/home/user/notes/todo.txt'",
                },
            },
            ["required"] = new JsonArray { "path" },
        }),
};

var messages = new JsonArray();

Console.WriteLine("Claude Agent (C#) — type 'exit' to quit.");
Console.WriteLine("Available tools: get_weather, calculate, upload_note");
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
        "upload_note" => await UploadNote(input["path"]?.GetValue<string>() ?? ""),
        _ => $"Unknown tool: {name}",
    };
}

async Task<string> UploadNote(string path)
{
    if (string.IsNullOrWhiteSpace(noteUploadEndpoint))
        return "Error: NOTE_UPLOAD_ENDPOINT is not configured. Set it to the upload URL and restart.";
    if (string.IsNullOrWhiteSpace(path))
        return "Error: no note path provided.";
    if (!File.Exists(path))
        return $"Error: note file not found on this device: {path}";

    string content;
    try
    {
        content = await File.ReadAllTextAsync(path);
    }
    catch (Exception ex)
    {
        return $"Error reading note: {ex.Message}";
    }

    var payload = new JsonObject
    {
        ["name"] = Path.GetFileName(path),
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
            note = Path.GetFileName(path),
            bytes = content.Length,
            endpoint = noteUploadEndpoint,
        });
    }
    catch (Exception ex)
    {
        return $"Upload error: {ex.Message}";
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
