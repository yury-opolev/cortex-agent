using Cortex.Contained.Evals.Setup;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<EvalConfigStore>();

var app = builder.Build();
app.UseStaticFiles();

// --- API ---

app.MapGet("/api/eval/provider", (EvalConfigStore store) =>
{
    var config = store.Load();
    var hasKey = store.LoadApiKey() is not null;
    return Results.Ok(new
    {
        config.ProviderName,
        config.Api,
        config.BaseUrl,
        config.Model,
        config.IsConfigured,
        HasApiKey = hasKey,
    });
});

app.MapPut("/api/eval/provider", (SaveProviderRequest request, EvalConfigStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.ProviderName))
        return Results.BadRequest(new { error = "providerName is required" });
    if (string.IsNullOrWhiteSpace(request.Api))
        return Results.BadRequest(new { error = "api is required" });

    var config = new EvalConfig
    {
        ProviderName = request.ProviderName,
        Api = request.Api,
        BaseUrl = request.BaseUrl,
        Model = request.Model ?? string.Empty,
        IsConfigured = true,
    };

    store.Save(config, request.ApiKey);
    return Results.Ok(new { saved = true });
});

app.MapGet("/api/eval/provider/test", async (EvalConfigStore store) =>
{
    var config = store.Load();
    var apiKey = store.LoadApiKey();

    if (!config.IsConfigured || string.IsNullOrEmpty(apiKey))
        return Results.BadRequest(new { error = "Eval provider not configured" });

    // Quick connectivity test — send a tiny completion request
    try
    {
        using var http = new HttpClient();

        var baseUrl = config.Api switch
        {
            "anthropic-messages" => config.BaseUrl ?? "https://api.anthropic.com",
            _ => config.BaseUrl ?? "https://api.openai.com",
        };

        if (config.Api == "anthropic-messages")
        {
            http.DefaultRequestHeaders.Add("x-api-key", apiKey);
            http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            var body = new
            {
                model = config.Model,
                max_tokens = 5,
                messages = new[] { new { role = "user", content = "Hi" } },
            };
            var resp = await http.PostAsJsonAsync($"{baseUrl}/v1/messages", body);
            if (resp.IsSuccessStatusCode)
                return Results.Ok(new { success = true, model = config.Model });

            var err = await resp.Content.ReadAsStringAsync();
            return Results.Ok(new { success = false, error = $"HTTP {(int)resp.StatusCode}: {err}" });
        }
        else
        {
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            var body = new
            {
                model = config.Model,
                max_tokens = 5,
                messages = new[] { new { role = "user", content = "Hi" } },
            };
            var resp = await http.PostAsJsonAsync($"{baseUrl}/v1/chat/completions", body);
            if (resp.IsSuccessStatusCode)
                return Results.Ok(new { success = true, model = config.Model });

            var err = await resp.Content.ReadAsStringAsync();
            return Results.Ok(new { success = false, error = $"HTTP {(int)resp.StatusCode}: {err}" });
        }
    }
    catch (Exception ex)
    {
        return Results.Ok(new { success = false, error = ex.Message });
    }
});

// Redirect root to setup page
app.MapGet("/", () => Results.Redirect("/index.html"));

Console.WriteLine();
Console.WriteLine("Eval LLM Setup Server");
Console.WriteLine("=====================");
Console.WriteLine($"Open http://localhost:{builder.Configuration["Urls"]?.Split(':').LastOrDefault() ?? "5090"} in your browser");
Console.WriteLine("Press Ctrl+C to stop.");
Console.WriteLine();

app.Run("http://localhost:5090");

// --- Request DTO ---

sealed record SaveProviderRequest
{
    public string ProviderName { get; init; } = string.Empty;
    public string Api { get; init; } = string.Empty;
    public string? BaseUrl { get; init; }
    public string? Model { get; init; }
    public string? ApiKey { get; init; }
}
