using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true) 
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables()                                              
    .AddCommandLine(args);                                                  

var appSettings = new AppSettings();
builder.Configuration.GetSection("AppSettings").Bind(appSettings);

var validationErrors = ValidateSettings(appSettings);
if (validationErrors.Count != 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Ошибки конфигурации приложения:");
    foreach (var error in validationErrors)
    {
        Console.WriteLine($"  - {error}");
    }
    Console.ResetColor();
    Environment.Exit(1);
}

builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("AppSettings"));
builder.Services.AddSingleton(appSettings);

builder.Services.AddCors(options =>
{
    options.AddPolicy("TrustedOrigins", policy =>
    {
        policy.WithOrigins(appSettings.AllowedOrigins.ToArray())
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("ReadPolicy", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            partition => new FixedWindowRateLimiterOptions
            {
                PermitLimit = appSettings.RateLimit.ReadPermitLimit,
                Window = TimeSpan.FromSeconds(appSettings.RateLimit.WindowInSeconds),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy("WritePolicy", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            partition => new FixedWindowRateLimiterOptions
            {
                PermitLimit = appSettings.RateLimit.WritePermitLimit,
                Window = TimeSpan.FromSeconds(appSettings.RateLimit.WindowInSeconds),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.OnRejected = async (context, token) =>
    {
        var mode = appSettings.Mode;
        context.HttpContext.Response.StatusCode = 429;
        if (mode.Equals("Development", StringComparison.OrdinalIgnoreCase))
        {
            context.HttpContext.Response.ContentType = "text/plain";
            await context.HttpContext.Response.WriteAsync(
                "Слишком много запросов. Пожалуйста, повторите попытку позже.", token);
        }
        else // Production
        {
            context.HttpContext.Response.Headers.RetryAfter = "60";
        }
    };
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    await next();
});

app.UseCors("TrustedOrigins");
app.UseRateLimiter();

var items = new ConcurrentDictionary<int, string>();
int nextId = 1;

app.MapGet("/api/items", () => Results.Ok(items))
   .RequireRateLimiting("ReadPolicy");

app.MapPost("/api/items", ([FromBody] string value) =>
{
    var id = Interlocked.Increment(ref nextId);
    items[id] = value;
    return Results.Created($"/api/items/{id}", new { id, value });
}).RequireRateLimiting("WritePolicy");

app.Run();

List<string> ValidateSettings(AppSettings settings)
{
    var errors = new List<string>();

    if (settings.AllowedOrigins == null || settings.AllowedOrigins.Count == 0)
        errors.Add("Не указано ни одного доверенного источника (AllowedOrigins).");

    if (settings.AllowedOrigins != null)
    {
        foreach (var origin in settings.AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                errors.Add($"Некорректный URL доверенного источника: '{origin}'.");
            else if (uri.Scheme != "https")
                errors.Add($"Источник '{origin}' должен использовать HTTPS.");
        }
    }

    if (settings.RateLimit.ReadPermitLimit <= 0)
        errors.Add("Лимит запросов на чтение должен быть больше 0.");
    if (settings.RateLimit.WritePermitLimit <= 0)
        errors.Add("Лимит запросов на запись должен быть больше 0.");
    if (settings.RateLimit.WindowInSeconds <= 0)
        errors.Add("Окно ограничения (WindowInSeconds) должно быть положительным.");

    if (settings.Mode != null &&
        !settings.Mode.Equals("Development", StringComparison.OrdinalIgnoreCase) &&
        !settings.Mode.Equals("Production", StringComparison.OrdinalIgnoreCase))
        errors.Add($"Неизвестный режим работы: '{settings.Mode}'. Допустимы Development, Production.");

    return errors;
}

public class AppSettings
{
    public List<string> AllowedOrigins { get; set; } = new();
    public RateLimitSettings RateLimit { get; set; } = new();
    public string Mode { get; set; } = "Production";
}

public class RateLimitSettings
{
    public int ReadPermitLimit { get; set; } = 30;
    public int WritePermitLimit { get; set; } = 5;
    public int WindowInSeconds { get; set; } = 60;
}