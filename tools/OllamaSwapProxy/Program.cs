// OllamaSwapProxy — a sole-residency model-swapper between ember and Ollama.
//
//     ember  ->  OllamaSwapProxy (:11500/v1)  ->  Ollama (:11434)
//
// The planning loop is sequential (ADR 10), so only one model is ever needed at
// once. Before each /v1/chat/completions call this proxy evicts every other model
// from Ollama and loads the requested one — timing the load and recording the
// GPU/CPU split from Ollama's /api/ps and from nvidia-smi. A round that fails with
// a model alone on the card is a model problem; one that only fails under
// contention is an environment problem.
//
// Every chat request is appended, as one JSON line, to a run log (swaps.jsonl) —
// durable, comparable data across runs. Every non-chat path is a pass-through.

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

var logPath = Environment.GetEnvironmentVariable("SWAP_LOG")
    ?? Path.Combine(app.Environment.ContentRootPath, "swaps.jsonl");

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
        SwapInfo? swap = null;
        if (model is null)
            log.LogWarning("[swap] /v1/chat/completions has no model field — forwarding without a swap");
        else
            swap = await EnsureSoleResidencyAsync(model);

        var fwd = await ForwardAsync(ctx, "/v1/chat/completions", body, buffer: true);
        log.LogInformation("[swap]   <- {Status} in {Secs:0.0}s{Tokens}", fwd.Status, fwd.Seconds,
            fwd.Usage is { } u ? $"  ({u.PromptTokens} prompt + {u.CompletionTokens} completion tok)" : "");
        await AppendRunLogAsync(model, swap, fwd);
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
    await ForwardAsync(ctx, "/" + path, body, buffer: false);
});

log.LogInformation("OllamaSwapProxy on http://localhost:{Port} -> {Ollama}  (run log: {LogPath})",
    port, ollamaBase, logPath);
app.Run();

// ── swap ──────────────────────────────────────────────────────────────────────

async Task<SwapInfo> EnsureSoleResidencyAsync(string target)
{
    log.LogInformation("[swap] request: model={Model}", target);
    var evicted = new List<string>();
    var loaded = await LoadedModelsAsync();
    double loadSeconds = 0;

    if (loaded.Count == 1 && NameEq(loaded[0].Name, target))
    {
        log.LogInformation("[swap]   {Model} already sole-resident — {Vram}", target, loaded[0].VramLine);
    }
    else
    {
        foreach (var m in loaded.Where(m => !NameEq(m.Name, target)))
        {
            await GenerateAsync(new { model = m.Name, keep_alive = 0, stream = false });
            evicted.Add(m.Name);
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
        loadSeconds = sw.Elapsed.TotalSeconds;
    }

    var resident = (await LoadedModelsAsync()).FirstOrDefault(m => NameEq(m.Name, target));
    if (loadSeconds > 0)
        log.LogInformation("[swap]   loaded {Model} in {Secs:0.0}s — {Vram}",
            target, loadSeconds, resident?.VramLine ?? "NOT resident — is it pulled?");

    var gpu = await GpuSnapshotAsync();
    if (gpu is not null)
        log.LogInformation("[swap]   nvidia-smi: {Used} MiB used, {Free} MiB free, {Util}% util",
            gpu.UsedMiB, gpu.FreeMiB, gpu.UtilPct);

    return new SwapInfo(evicted, loadSeconds, resident, gpu);
}

// ── forward ───────────────────────────────────────────────────────────────────

async Task<ForwardResult> ForwardAsync(HttpContext ctx, string path, byte[]? body, bool buffer)
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
        resp = await http.SendAsync(msg,
            buffer ? HttpCompletionOption.ResponseContentRead : HttpCompletionOption.ResponseHeadersRead,
            ctx.RequestAborted);
    }
    catch (Exception ex)
    {
        log.LogError("[proxy] {Path} -> Ollama unreachable: {Err}", path, ex.Message);
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsync($"swap-proxy: could not reach Ollama at {ollamaBase} — {ex.Message}");
        return new ForwardResult(502, sw.Elapsed.TotalSeconds, null);
    }

    ctx.Response.StatusCode = (int)resp.StatusCode;
    if (resp.Content.Headers.ContentType is { } rct)
        ctx.Response.ContentType = rct.ToString();

    Usage? usage = null;
    if (buffer)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
        sw.Stop();
        usage = TryReadUsage(bytes);
        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
    }
    else
    {
        await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        sw.Stop();
    }

    var status = (int)resp.StatusCode;
    resp.Dispose();
    return new ForwardResult(status, sw.Elapsed.TotalSeconds, usage);
}

// ── run log ───────────────────────────────────────────────────────────────────

async Task AppendRunLogAsync(string? model, SwapInfo? swap, ForwardResult fwd)
{
    try
    {
        var resident = swap?.Resident;
        var record = new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.Now.ToString("o"),
            ["model"] = model,
            ["evicted"] = swap?.Evicted ?? new List<string>(),
            ["load_s"] = swap is null ? 0.0 : Math.Round(swap.LoadSeconds, 1),
            ["size_gb"] = resident is null ? null : Math.Round(resident.Size / 1073741824.0, 2),
            ["vram_gb"] = resident is null ? null : Math.Round(resident.VramBytes / 1073741824.0, 2),
            ["gpu_pct"] = resident?.GpuPct,
            ["smi_used_mib"] = swap?.Gpu?.UsedMiB,
            ["smi_free_mib"] = swap?.Gpu?.FreeMiB,
            ["smi_util_pct"] = swap?.Gpu?.UtilPct,
            ["generate_s"] = Math.Round(fwd.Seconds, 1),
            ["status"] = fwd.Status,
            ["prompt_tokens"] = fwd.Usage?.PromptTokens,
            ["completion_tokens"] = fwd.Usage?.CompletionTokens,
            ["total_tokens"] = fwd.Usage?.TotalTokens,
        };
        await File.AppendAllTextAsync(logPath, JsonSerializer.Serialize(record) + "\n");
    }
    catch (Exception ex)
    {
        log.LogWarning("[swap]   run-log write failed: {Err}", ex.Message);
    }
}

// ── ollama + gpu helpers ──────────────────────────────────────────────────────

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

async Task<GpuSnapshot?> GpuSnapshotAsync()
{
    try
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            ArgumentList = { "--query-gpu=memory.used,memory.free,utilization.gpu", "--format=csv,noheader,nounits" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (proc is null)
            return null;

        var output = await proc.StandardOutput.ReadToEndAsync();
        if (!proc.WaitForExit(5000))
        {
            try { proc.Kill(); } catch { /* best effort */ }
            return null;
        }
        if (proc.ExitCode != 0)
            return null;

        var first = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (first is null)
            return null;

        var f = first.Split(',', StringSplitOptions.TrimEntries);
        if (f.Length < 3)
            return null;

        return new GpuSnapshot(
            int.TryParse(f[0], out var used) ? used : 0,
            int.TryParse(f[1], out var free) ? free : 0,
            int.TryParse(f[2], out var util) ? util : 0);
    }
    catch (Exception ex)
    {
        log.LogWarning("[swap]   nvidia-smi unavailable: {Err}", ex.Message);
        return null;
    }
}

// ── small helpers ─────────────────────────────────────────────────────────────

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

static Usage? TryReadUsage(byte[] body)
{
    try
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
            return null;
        return new Usage(
            u.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0,
            u.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : 0,
            u.TryGetProperty("total_tokens", out var t) ? t.GetInt32() : 0);
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
    public int GpuPct => Size <= 0 ? 0 : (int)Math.Round(100.0 * VramBytes / Size);

    public string VramLine => Size <= 0
        ? "size unknown"
        : $"{Gb(Size)} total, {Gb(VramBytes)} VRAM ({GpuPct}% GPU)";

    static string Gb(long bytes) => $"{bytes / 1073741824.0:0.0} GB";
}

record GpuSnapshot(int UsedMiB, int FreeMiB, int UtilPct);

record Usage(int PromptTokens, int CompletionTokens, int TotalTokens);

record SwapInfo(List<string> Evicted, double LoadSeconds, ModelInfo? Resident, GpuSnapshot? Gpu);

record ForwardResult(int Status, double Seconds, Usage? Usage);
