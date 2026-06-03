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
    "weather or math. Be concise in your answers.";

using var http = new HttpClient { BaseAddress = new Uri("https://api.anthropic.com/") };
http.DefaultRequestHeaders.Add("x-api-key", apiKey);
http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

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
};

var messages = new JsonArray();

Console.WriteLine("Claude Agent (C#) — type 'exit' to quit.");
Console.WriteLine("Available tools: get_weather, calculate");
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
            var result = ExecuteTool(name, toolInput);
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

static string ExecuteTool(string name, JsonNode input)
{
    return name switch
    {
        "get_weather" => GetWeather(input["location"]?.GetValue<string>() ?? "unknown"),
        "calculate" => Calculate(input["expression"]?.GetValue<string>() ?? "0"),
        _ => $"Unknown tool: {name}",
    };
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
