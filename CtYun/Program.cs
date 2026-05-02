using CtYun;
using CtYun.Models;
using System.Reflection;
using System.Text.Json;

Utility.WriteLine(ConsoleColor.Green, $"版本：{Assembly.GetEntryAssembly()?.GetName().Version}");

var builder = WebApplication.CreateSlimBuilder(args);
var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrWhiteSpace(urls))
{
    var port = Environment.GetEnvironmentVariable("PORT");
    urls = string.IsNullOrWhiteSpace(port) ? "http://0.0.0.0:8080" : $"http://0.0.0.0:{port}";
}

builder.WebHost.UseUrls(urls);
builder.Services.ConfigureHttpJsonOptions(options => ConfigureJson(options.SerializerOptions));
builder.Services.AddSingleton<CtYunConfigStore>();
builder.Services.AddSingleton<CtYunKeepAliveService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CtYunKeepAliveService>());

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (CtYunConfigStore store, CtYunKeepAliveService service) => Results.Ok(new StatusResponse
{
    Configured = store.GetConfig().Accounts.Count > 0,
    DataDir = store.DataDir,
    ConfigPath = store.ConfigPath,
    KeepAliveSeconds = store.GetConfig().KeepAliveSeconds,
    Running = service.IsRunning,
    Accounts = service.GetAccountStates(),
    Events = service.GetEvents()
}));

app.MapGet("/api/config", (CtYunConfigStore store) => Results.Ok(store.GetSanitizedConfig()));

app.MapPut("/api/config", async (AppConfig config, CtYunConfigStore store, CtYunKeepAliveService service, CancellationToken ct) =>
{
    var normalized = store.Normalize(config, keepExistingPasswords: true);
    await store.SaveAsync(normalized, ct);
    await service.ReloadAsync(ct);
    return Results.Ok(store.GetSanitizedConfig());
});

app.MapPost("/api/accounts/test-login", async Task<IResult> (AccountConfig account, CtYunConfigStore store, CtYunKeepAliveService service, CancellationToken ct) =>
{
    var normalized = store.NormalizeAccount(account, keepExistingPassword: true);
    if (string.IsNullOrWhiteSpace(normalized.User) || string.IsNullOrWhiteSpace(normalized.Password))
    {
        return Results.BadRequest(new ApiMessage("账号和密码不能为空。"));
    }

    normalized.DeviceCode = store.ResolveDeviceCode(normalized);
    var result = await service.TestLoginAsync(normalized, ct);
    return Results.Ok(result);
});

app.MapPost("/api/accounts/send-sms", async Task<IResult> (AccountConfig account, CtYunConfigStore store, CtYunKeepAliveService service, CancellationToken ct) =>
{
    var normalized = store.NormalizeAccount(account, keepExistingPassword: true);
    if (string.IsNullOrWhiteSpace(normalized.User) || string.IsNullOrWhiteSpace(normalized.Password))
    {
        return Results.BadRequest(new ApiMessage("账号和密码不能为空。"));
    }

    normalized.DeviceCode = store.ResolveDeviceCode(normalized);
    var result = await service.SendSmsAsync(normalized, ct);
    return Results.Ok(result);
});

app.MapPost("/api/accounts/bind-device", async Task<IResult> (BindDeviceRequest request, CtYunConfigStore store, CtYunKeepAliveService service, CancellationToken ct) =>
{
    var account = store.NormalizeAccount(request.Account, keepExistingPassword: true);
    if (string.IsNullOrWhiteSpace(account.User) || string.IsNullOrWhiteSpace(account.Password))
    {
        return Results.BadRequest(new ApiMessage("账号和密码不能为空。"));
    }

    if (string.IsNullOrWhiteSpace(request.Code))
    {
        return Results.BadRequest(new ApiMessage("短信验证码不能为空。"));
    }

    account.DeviceCode = store.ResolveDeviceCode(account);
    var result = await service.BindDeviceAsync(account, request.Code, ct);
    if (result.Success)
    {
        await service.ReloadAsync(ct);
    }

    return Results.Ok(result);
});

app.MapPost("/api/service/restart", async (CtYunKeepAliveService service, CancellationToken ct) =>
{
    await service.ReloadAsync(ct);
    return Results.Ok(new ApiMessage("保活服务已重启。"));
});

app.MapPost("/api/service/stop", async (CtYunKeepAliveService service, CancellationToken ct) =>
{
    await service.StopSessionsAsync(ct);
    return Results.Ok(new ApiMessage("保活服务已停止。"));
});

app.MapFallbackToFile("index.html");

await app.RunAsync();

static void ConfigureJson(JsonSerializerOptions options)
{
    options.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
}
