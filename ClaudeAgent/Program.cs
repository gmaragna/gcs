using System.Text.Json;
using Anthropic;

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

using var api = new AnthropicApi(apiKey);

var tools = new List<Tool>
{
    CreateTool("get_weather",
        "Get the current weather for a given city.",
        new
        {
            type = "object",
            properties = new
            {
                location = new { type = "string", description = "City and country, e.g. 'Rome, Italy'" },
            },
            required = new[] { "location" },
        }),
    CreateTool("calculate",
        "Evaluate a mathematical expression and return the numeric result.",
        new
        {
            type = "object",
            properties = new
            {
                expression = new { type = "string", description = "A math expression, e.g. '2 + 2' or '(3.14 * 10^2)'" },
            },
            required = new[] { "expression" },
        }),
};

var messages = new List<Message>();
var systemPrompt =
    "You are a helpful assistant. Use the available tools when the user asks about " +
    "weather or math. Be concise in your answers.";

Console.WriteLine("Claude Agent (C#) — type 'exit' to quit.");
Console.WriteLine("Available tools: get_weather, calculate");
Console.WriteLine(new string('-', 50));

while (true)
{
    Console.Write("\nYou: ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input) || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    messages.Add(input.AsUserMessage());

    Message response;
    try
    {
        response = await SendMessage(messages, systemPrompt, tools);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[API error] {ExtractApiError(ex)}");
        messages.RemoveAt(messages.Count - 1);
        continue;
    }

    while (response.StopReason == StopReason.ToolUse)
    {
        messages.Add(response.AsRequestMessage());

        var toolResults = new List<Block>();
        foreach (var block in response.Content.Value2!)
        {
            if (!block.IsToolUse) continue;
            var toolUse = block.ToolUse!;

            Console.WriteLine($"  [tool] {toolUse.Name}({toolUse.Input})");
            var result = ExecuteTool(toolUse.Name, toolUse.Input?.ToString());
            Console.WriteLine($"  [result] {result}");

            toolResults.Add(new ToolResultBlock
            {
                ToolUseId = toolUse.Id,
                Content = result,
            });
        }

        messages.Add(new Message { Role = MessageRole.User, Content = new(toolResults) });
        try
        {
            response = await SendMessage(messages, systemPrompt, tools);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[API error] {ExtractApiError(ex)}");
            goto nextPrompt;
        }
    }

    messages.Add(response.AsRequestMessage());

    var text = string.Join("", response.Content.Value2!
        .Where(b => b.IsText)
        .Select(b => b.Text!.Text));

    Console.WriteLine($"\nClaude: {text}");
    nextPrompt:;
}

Console.WriteLine("\nGoodbye!");
return 0;

async Task<Message> SendMessage(List<Message> msgs, string system, List<Tool> t)
{
    return await api.CreateMessageAsync(
        new CreateMessageRequest
        {
            Model = CreateMessageRequestModel.Claude35Sonnet20240620,
            MaxTokens = 1024,
            System = system,
            Messages = msgs,
            Tools = t,
            ToolChoice = new ToolChoice { Type = ToolChoiceType.Auto },
        });
}

static Tool CreateTool(string name, string description, object inputSchema)
{
    var schemaJson = JsonSerializer.Serialize(inputSchema);
    var schema = JsonSerializer.Deserialize<ToolInputSchema>(schemaJson)
                 ?? new ToolInputSchema();
    return new Tool
    {
        Name = name,
        Description = description,
        InputSchema = schema,
    };
}

static string ExecuteTool(string name, string? argsJson)
{
    var args = string.IsNullOrEmpty(argsJson)
        ? new Dictionary<string, JsonElement>()
        : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson)
          ?? new Dictionary<string, JsonElement>();

    return name switch
    {
        "get_weather" => GetWeather(args.TryGetValue("location", out var loc) ? loc.GetString()! : "unknown"),
        "calculate" => Calculate(args.TryGetValue("expression", out var expr) ? expr.GetString()! : "0"),
        _ => $"Unknown tool: {name}",
    };
}

static string ExtractApiError(Exception ex)
{
    var raw = ex.Message ?? "";
    try
    {
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("error", out var err) &&
            err.TryGetProperty("message", out var msg))
        {
            return msg.GetString() ?? raw;
        }
    }
    catch (JsonException) { }
    return raw;
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
