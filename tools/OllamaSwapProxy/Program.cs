// OllamaSwapProxy — a sole-residency model-swapper between ember and Ollama.
//
//     ember  ->  OllamaSwapProxy (:11500/v1)  ->  Ollama (:11434)
//
// The planning loop is sequential (ADR 10), so only one model is ever needed at
// once. Before each /v1/chat/completions call this proxy evicts every other model
// from Ollama and loads the requested one — timing the load and logging the
// GPU/CPU split from /api/ps. A round that fails with a model alone on the card is
// a model problem; one that only fails under contention is an environment problem.
//
// Every other path is a transparent pass-through to Ollama.

using System.Diagnostics;
using System.Text;
using System.Text.Json;

var ollamaBase = (Environment.GetEnvironmentVariable("OLLAMA_BASE") ?? "http://localhost:11434").TrimEnd('/');
var port = Environment.GetEnvironmentVariable("SWAP_PROXY_PORT") ?? "11500";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://localhost:{port}");
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

var app = builder.Build();
var log = app.Logger;

// One forwarded request spans a cold model load plus a full generation — be generous.
var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
// The loop is sequential; serialize anyway so a swap can never interleave with a call.
var gate = new SemaphoreSlim(1, 1);

app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
{
    var body = await ReadBodyAsync(ctx.Request);
    var model = TryReadModel(body);

    await gate.WaitAsync(ctx.RequestAborted);
    try
    {
        if (model is null)
            log.LogWarning("[swap] /v1/chat/completions has no model field — forwarding without a swap");
        else
            await EnsureSoleResidencyAsync(model);

        await ForwardAsync(ctx, "/v1/chat/completions", body, logResult: true);
    }
    finally
    {
        gate.Release();
    }
});

// Everything else (e.g. /v1/models, /api/ps) is a transparent pass-through.
app.Map("/{**path}", async (HttpContext ctx, string path) =>
{
    var body = ctx.Request.ContentLength is > 0 ? await ReadBodyAsync(ctx.Request) : null;
    await ForwardAsync(ctx, "/" + path, body, logResult: false);
});

log.LogInformation("OllamaSwapProxy listening on http://localhost:{Port}  ->  {Ollama}", port, ollamaBase);
app.Run();

// ── helpers ───────────────────────────────────────────────────────────────────

async Task EnsureSoleResidencyAsync(string target)
{
    log.LogInformation("[swap] request: model={Model}", target);
    var loaded = await LoadedModelsAsync();

    if (loaded.Count == 1 && NameEq(loaded[0].Name, target))
    {
        log.LogInformation("[swap]   {Model} already sole-resident — {Vram}", target, loaded[0].VramLine);
        return;
    }

    foreach (var m in loaded.Where(m => !NameEq(m.Name, target)))
    {
        await GenerateAsync(new { model = m.Name, keep_alive = 0, stream = false });
        log.LogInformation("[swap]   evicted {Model}", m.Name);
    }

    var settled = await WaitUntilAsync(
        async () => (await LoadedModelsAsync()).All(m => NameEq(m.Name, target)),
        TimeSpan.FromSeconds(30));
    if (!settled)
        log.LogWarning("[swap]   eviction did not settle within 30s — continuing anyway");

    var sw = Stopwatch.StartNew();
    await GenerateAsync(new { model = target, stream = false });   // empty prompt → just load
    sw.Stop();

    var now = (await LoadedModelsAsync()).FirstOrDefault(m => NameEq(m.Name, target));
    if (now is null)
        log.LogWarning("[swap]   {Model} is NOT resident after {Secs:0.0}s — is it pulled?", target, sw.Elapsed.TotalSeconds);
    else
        log.LogInformation("[swap]   loaded {Model} in {Secs:0.0}s — {Vram}", target, sw.Elapsed.TotalSeconds, now.VramLine);
}

async Task ForwardAsync(HttpContext ctx, string path, byte[]? body, bool logResult)
{
    var url = $"{ollamaBase}{path}{ctx.Request.QueryString}";
    using var msg = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), url);
    if (body is { Length: > 0 })
    {
        msg.Content = new ByteArrayContent(body);
        if (ctx.Request.ContentType is { } ctype)
            msg.Content.Headers.TryAddWithoutValidation("Content-Type", ctype);
    }

    var sw = Stopwatch.StartNew();
    HttpResponseMessage resp;
    try
    {
        resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
    }
    catch (Exception ex)
    {
        log.LogError("[proxy] {Path} -> Ollama unreachable: {Err}", path, ex.Message);
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsync($"swap-proxy: could not reach Ollama at {ollamaBase} — {ex.Message}");
        return;
    }
    sw.Stop();

    if (logResult)
        log.LogInformation("[swap]   <- {Status} in {Secs:0.0}s", (int)resp.StatusCode, sw.Elapsed.TotalSeconds);

    ctx.Response.StatusCode = (int)resp.StatusCode;
    if (resp.Content.Headers.ContentType is { } rct)
        ctx.Response.ContentType = rct.ToString();
    await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    resp.Dispose();
}

async Task<List<ModelInfo>> LoadedModelsAsync()
{
    try
    {
        using var resp = await http.GetAsync($"{ollamaBase}/api/ps");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var list = new List<ModelInfo>();
        if (doc.RootElement.TryGetProperty("models", out var models))
            foreach (var m in models.EnumerateArray())
                list.Add(new ModelInfo(
                    m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    m.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                    m.TryGetProperty("size_vram", out var v) ? v.GetInt64() : 0));
        return list;
    }
    catch (Exception ex)
    {
        log.LogWarning("[swap]   /api/ps unavailable: {Err}", ex.Message);
        return [];
    }
}

async Task GenerateAsync(object payload)
{
    try
    {
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync($"{ollamaBase}/api/generate", content);
        await resp.Content.ReadAsStringAsync();   // drain; residency is verified by the caller
    }
    catch (Exception ex)
    {
        log.LogWarning("[swap]   /api/generate failed: {Err}", ex.Message);
    }
}

static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    do
    {
        if (await condition()) return true;
        await Task.Delay(500);
    }
    while (DateTime.UtcNow < deadline);
    return false;
}

static async Task<byte[]> ReadBodyAsync(HttpRequest req)
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    return ms.ToArray();
}

static string? TryReadModel(byte[] body)
{
    try
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;
    }
    catch
    {
        return null;
    }
}

static bool NameEq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

// ── types ─────────────────────────────────────────────────────────────────────

record ModelInfo(string Name, long Size, long VramBytes)
{
    public string VramLine => Size <= 0
        ? "size unknown"
        : $"{Gb(Size)} total, {Gb(VramBytes)} VRAM ({(int)Math.Round(100.0 * VramBytes / Size)}% GPU)";

    static string Gb(long bytes) => $"{bytes / 1073741824.0:0.0} GB";
}
