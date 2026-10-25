using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Erp.IntegrationTests.Harness;

/// <summary>
/// Cliente de la API con una sesión (cookie) y la cabecera anti-CSRF. Los cuerpos se escriben como objetos anónimos con
/// los nombres tal cual los espera la API (<c>new { op = "createEntity", name = "X" }</c>, <c>new { Clave = "A" }</c>).
/// </summary>
public sealed class Client(HttpClient http) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    public HttpClient Http => http;

    public Task<Res> GetAsync(string path, object? query = null) => SendAsync(HttpMethod.Get, path + Query(query), null);
    public Task<Res> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, Body(body));
    public Task<Res> PutAsync(string path, object? body = null) => SendAsync(HttpMethod.Put, path, Body(body));
    public Task<Res> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path, null);

    public Task<Res> PostBytesAsync(string path, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return SendAsync(HttpMethod.Post, path, content);
    }

    public Task<Res> PostFormAsync(string path, IDictionary<string, string> form) => SendAsync(HttpMethod.Post, path, new FormUrlEncodedContent(form));

    public async Task<Res> SendAsync(HttpMethod method, string path, HttpContent? content)
    {
        using var req = new HttpRequestMessage(method, path) { Content = content };
        using var res = await http.SendAsync(req);
        var headers = res.Headers.Concat(res.Content.Headers)
            .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(h => h.Value)), StringComparer.OrdinalIgnoreCase);
        return new Res((int)res.StatusCode, await res.Content.ReadAsStringAsync(), headers);
    }

    /// <summary>Como el modelador: la vista previa da la huella (spec 021) y aplicar la usa.</summary>
    public async Task ApplyPlanAsync(object plan)
    {
        var preview = await PostAsync("/api/model/plan/preview", plan);
        Assert.True(preview.Status == 200, preview.Text);
        Assert.True(preview["valid"]?.GetValue<bool>() != false, preview.Text);
        var node = JsonSerializer.SerializeToNode(plan, Json)!.AsObject();
        node["baseHash"] = preview["baseHash"]?.DeepClone();
        var res = await SendAsync(HttpMethod.Post, "/api/model/plan/apply", new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.True(res.Status == 200, res.Text);
    }

    /// <summary>Instala un módulo de <c>modules/</c> con la huella de su vista previa (029 D-04).</summary>
    public async Task InstallModuleAsync(string id)
    {
        var preview = await PostAsync($"/api/admin/modules/{id}/preview");
        Assert.True(preview.Status == 200, preview.Text);
        var res = await PostAsync($"/api/admin/modules/{id}/install", new { baseHash = preview["baseHash"]?.GetValue<string>() });
        Assert.True(res.Status == 200, $"instalar {id}: {res.Text}");
    }

    public async Task<int> RoleIdAsync(string name)
    {
        var roles = (await GetAsync("/api/admin/roles")).Array;
        return roles.First(r => r!["name"]!.GetValue<string>() == name)!["id"]!.GetValue<int>();
    }

    /// <summary>Crea un registro y exige 201.</summary>
    public async Task<JsonNode> CreateAsync(string entity, object data)
    {
        var res = await PostAsync($"/api/data/{entity}", data);
        Assert.True(res.Status == 201, $"{entity} → {res.Status} {res.Text}");
        return res.Body!;
    }

    public async Task<JsonNode> RecordAsync(string entity, int id)
    {
        var res = await GetAsync($"/api/data/{entity}/{id}");
        Assert.True(res.Status == 200, $"{entity}/{id} → {res.Status} {res.Text}");
        return res.Body!;
    }

    public Task<Res> TransitionAsync(string entity, JsonNode row, string name, object? values = null) =>
        PostAsync($"/api/data/{entity}/{row["id"]}/transition", new { transition = name, _rowVersion = row["_rowVersion"]!.GetValue<string>(), values });

    public static StringContent JsonBody(object body) => new(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

    private static StringContent? Body(object? body) => body is null ? null : JsonBody(body);

    private static string Query(object? query)
    {
        if (query is null) return "";
        var pairs = query.GetType().GetProperties().Select(p => (p.Name, Value: p.GetValue(query)))
            .Where(p => p.Value is not null)
            .Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture)!)}");
        return "?" + string.Join("&", pairs);
    }

    public void Dispose() => http.Dispose();
}

/// <summary>Respuesta: estado, texto y el JSON (si lo hay).</summary>
public sealed class Res(int status, string text, IReadOnlyDictionary<string, string> headers)
{
    private readonly Lazy<JsonNode?> _body = new(() => { try { return JsonNode.Parse(text); } catch (JsonException) { return null; } });
    public int Status { get; } = status;
    public string Text { get; } = text;
    public IReadOnlyDictionary<string, string> Headers { get; } = headers;
    public JsonNode? Body => _body.Value;
    public JsonNode? this[string key] => Body?[key];
    public JsonArray Array => Body as JsonArray ?? throw new InvalidOperationException("No es un arreglo: " + Text);
    public JsonArray Items => Body?["items"] as JsonArray ?? throw new InvalidOperationException("Sin items: " + Text);
    public override string ToString() => $"{Status} {Text}";
}

public static class JsonExt
{
    public static int Id(this JsonNode n) => n["id"]!.GetValue<int>();
    public static string? Str(this JsonNode n, string key) => n[key] is null ? null : n[key]!.GetValueKind() == JsonValueKind.String ? n[key]!.GetValue<string>() : n[key]!.Json();
    public static decimal? Dec(this JsonNode n, string key) => n[key] is { } v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<decimal>() : null;
    public static JsonArray Arr(this JsonNode n, string key) => n[key] as JsonArray ?? throw new InvalidOperationException($"Sin «{key}»: {n.Json()}");
    public static IEnumerable<JsonNode> Nodes(this JsonArray a) => a.Where(x => x is not null).Select(x => x!);
}

public static class JsonText
{
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// El JSON como texto sin escapar acentos ni comillas angulares: <c>ToJsonString()</c> escribe «á» como <c>á</c>, y un
    /// <c>DoesNotContain("Cliente de otra empresa")</c> sobre ese texto pasaría aunque el dato estuviera.
    /// </summary>
    /// Revisión adversarial 030 (F-B): con un nodo ausente devolvía «null» y un <c>DoesNotContain</c> pasaba con un 403 o 404;
    /// ahora falla.
    public static string Json(this JsonNode? n) => n?.ToJsonString(Relaxed) ?? throw new InvalidOperationException("Se esperaba JSON y la respuesta no lo trae.");
}
