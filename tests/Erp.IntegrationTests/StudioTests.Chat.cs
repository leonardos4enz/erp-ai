using System.Text.Json.Nodes;
using Erp.Api.Studio;
using Erp.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;
using static Erp.IntegrationTests.Harness.Concurrency;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 036 · ERP AI Studio como chat: varios turnos en una conversación (historial exacto, topes acumulados, la propuesta
/// vigente), la copia aislada que se conserva, lo leído que se borra en cada turno y el flujo SSE en vivo. Proveedor
/// simulado (<see cref="FakeStudioModel"/>): ninguna prueba llama a Anthropic.
/// </summary>
public partial class StudioTests
{
    private async Task<Res> SendAsync(Client api, int id, string text) =>
        await api.PostAsync($"/api/studio/sessions/{id}/messages", new { text });

    private async Task<JsonNode> SendOkAsync(Client api, int id, string text)
    {
        var res = await SendAsync(api, id, text);
        Assert.True(res.Status == 202, res.Text);
        return await WaitAsync(api, id);
    }

    /// <summary>El texto del último mensaje de la persona que recibió el modelo (para saber en qué turno está el guion).</summary>
    private static string LastAsk(FakeStudioModel.Turn t) =>
        string.Concat(t.Request.Messages.Last(m => m.FromUser && m.Blocks.OfType<StudioText>().Any()).Blocks.OfType<StudioText>().Select(b => b.Text));

    private async Task<Client> NewModelerAsync(string username)
    {
        using var admin = await host.AdminAsync();
        if (!host.Users.ContainsKey(username))
        {
            var res = await admin.PostAsync("/api/admin/users", new { username, displayName = username, password = Pwd, roleIds = new[] { await admin.RoleIdAsync("Studio Modelador") } });
            Assert.True(res.Status == 201, res.Text);
            host.Users[username] = res.Body!.Id();
        }
        return await host.LoginAsync(username, Pwd);
    }

    /// <summary>Una persona sin capacidad de modelador (propia de estas pruebas).</summary>
    private async Task<Client> LectorAsync()
    {
        if (!host.Users.ContainsKey("st-chat-lector"))
        {
            using var admin = await host.AdminAsync();
            var res = await admin.PostAsync("/api/admin/users", new { username = "st-chat-lector", displayName = "Lector 036", password = Pwd,
                roleIds = new[] { await admin.RoleIdAsync("Usuario") } });
            Assert.True(res.Status == 201, res.Text);
            host.Users["st-chat-lector"] = res.Body!.Id();
        }
        return await host.LoginAsync("st-chat-lector", Pwd);
    }

    // ---------- H1 ----------

    [Fact]
    public async Task Chat_H1_CA1_CA2_un_seguimiento_recibe_todo_el_historial()
    {
        using var ana = await host.LoginAsync("st-ana", Pwd);
        host.Llm.Scripts["seguir"] = t => Task.FromResult(LastAsk(t).Contains("¿Y las tareas?")
            ? Reply.Text("StTarea tiene Nombre y Monto.")
            : t.Index == 0 ? Reply.Tool("listar_catalogos", text: "Reviso el modelo.") : Reply.Text("Hay varios catálogos."));
        var s = await WaitAsync(ana, (await StartAsync(ana, "seguir", "¿Qué catálogos hay?")).Id());
        var id = s.Id();
        Assert.Equal("answered", s.Str("status"));
        Assert.Equal(2, s["steps"]!.GetValue<int>());

        s = await SendOkAsync(ana, id, "¿Y las tareas?");
        Assert.Equal("answered", s.Str("status"));
        Assert.Equal("StTarea tiene Nombre y Monto.", s.Str("summary"));
        // H2 CA1: los contadores son de la conversación.
        Assert.Equal(3, s["steps"]!.GetValue<int>());
        Assert.Equal(2, s["turns"]!.GetValue<int>());

        // El tercer llamado lleva todo: pedido, texto y uso de herramienta con su id, su resultado delimitado y el mensaje nuevo.
        var last = host.Llm.Requests.Last(r => r.Tag == "seguir").Text;
        Assert.Contains("¿Qué catálogos hay?", last);
        Assert.Contains("Reviso el modelo.", last);
        Assert.Contains("listar_catalogos", last);
        Assert.Contains("<datos_no_confiables herramienta=\"listar_catalogos\">", last);
        Assert.Contains("Hay varios catálogos.", last);
        Assert.Contains("<pedido>\n¿Y las tareas?\n</pedido>", last);

        // H1 CA2: los mensajes de la persona son pasos «user» y cada paso dice su turno.
        var trail = Trail(s).ToList();
        Assert.Equal(["user", "text", "tool", "text", "user", "text"], trail.Select(x => x.Str("kind")));
        Assert.Equal([1, 1, 1, 1, 2, 2], trail.Select(x => x["turn"]!.GetValue<int>()));
        Assert.Equal("¿Y las tareas?", trail[4]["output"]!.Str("text"));
        Assert.Equal(Enumerable.Range(1, 6), trail.Select(x => x["seq"]!.GetValue<int>()));
        // Bitácora: el turno y la longitud, nunca el texto.
        var audit = await host.ScalarAsync<string>($"SELECT AfterJson FROM meta.AuditLog WHERE Action = 'studio.message' AND RecordId = '{id}'");
        Assert.Contains("\"turn\":2", audit);
        Assert.DoesNotContain("tareas", audit);
        Assert.Equal(2, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.AuditLog WHERE Action = 'studio.finish' AND RecordId = '{id}'"));
        // La lista pone primero la conversación con actividad más reciente.
        Assert.Equal(id, (await ana.GetAsync("/api/studio/sessions")).Array[0]!.Id());
    }

    [Fact]
    public async Task Chat_H1_CA3_un_seguimiento_se_rechaza_con_su_motivo()
    {
        using var beto = await host.LoginAsync("st-beto", Pwd);
        using var carla = await host.LoginAsync("st-carla", Pwd);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Llm.Scripts["rechazo"] = async t =>
        {
            if (LastAsk(t).Contains("lento")) await release.Task.WaitAsync(t.Ct);
            return Reply.Text("Listo.");
        };
        var id = (await WaitAsync(beto, (await StartAsync(beto, "rechazo", "Hola")).Id())).Id();

        Assert.Equal(400, (await SendAsync(beto, id, "   ")).Status);
        Assert.Equal(400, (await SendAsync(beto, id, new string('a', 4001))).Status);
        Assert.Equal(404, (await SendAsync(carla, id, "Hola")).Status); // de otra persona
        Assert.Equal(404, (await SendAsync(beto, 999_999, "Hola")).Status);
        using (var lector = await LectorAsync())
            Assert.Equal(403, (await SendAsync(lector, id, "Hola")).Status);

        // Con un turno en curso: 409, y también desde otra conversación de la misma persona.
        var other = (await WaitAsync(beto, (await StartAsync(beto, "rechazo-b", "Otra")).Id())).Id();
        Assert.Equal(202, (await SendAsync(beto, id, "algo lento")).Status);
        var running = await SendAsync(beto, id, "otra cosa");
        Assert.Equal(409, running.Status);
        Assert.Contains("trabajando", running.Text);
        var elsewhere = await SendAsync(beto, other, "Hola");
        Assert.Equal(409, elsewhere.Status);
        Assert.Contains("otra conversación", elsewhere.Text);
        release.SetResult();
        await WaitAsync(beto, id);

        // Una sesión anterior a la 036 (sin historial) no se continúa.
        await host.ExecAsync($"DELETE meta.StudioMessage WHERE SessionId = {other}");
        var old = await SendAsync(beto, other, "Hola");
        Assert.Equal(409, old.Status);
        Assert.Contains("anterior al chat", old.Text);
        Assert.False((await beto.GetAsync($"/api/studio/sessions/{other}")).Body!["canSend"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Chat_H1_CA3_diez_mensajes_a_la_vez_encolan_uno()
    {
        using var dora = await host.LoginAsync("st-dora", Pwd);
        host.Llm.Scripts["diez"] = _ => Task.FromResult(Reply.Text("Ok."));
        var id = (await WaitAsync(dora, (await StartAsync(dora, "diez", "Hola")).Id())).Id();
        var results = await Concurrently(10, i => SendAsync(dora, id, $"mensaje {i}"));
        Assert.Equal(1, results.Count(r => r.Status == 202));
        Assert.Equal(9, results.Count(r => r.Status == 409));
        var s = await WaitAsync(dora, id);
        Assert.Equal(2, s["turns"]!.GetValue<int>());
        Assert.Equal(2, Trail(s).Count(x => x.Str("kind") == "user"));
        Assert.Equal(2, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioMessage WHERE SessionId = {id} AND FromUser = 1 AND ContentJson LIKE '%pedido%'"));
    }

    [Fact]
    public async Task Chat_H1_CA4_cada_mensaje_toma_la_identidad_actual()
    {
        var eva = await NewModelerAsync("st-chat-eva");
        host.Llm.Scripts["sello"] = _ => Task.FromResult(Reply.Text("Sigo aquí."));
        var id = (await WaitAsync(eva, (await StartAsync(eva, "sello", "Hola")).Id())).Id();
        // La persona cambia de sello (contraseña): su sesión anterior ya no sirve y vuelve a entrar.
        await host.ExecAsync($"UPDATE meta.[User] SET SecurityStamp = NEWID() WHERE Id = {host.Users["st-chat-eva"]}");
        eva.Dispose();
        using var again = await host.LoginAsync("st-chat-eva", Pwd);
        var s = await SendOkAsync(again, id, "¿Sigues?");
        // Antes de la 036: con el sello guardado al crearla, la conversación fallaba con «identity» tras cambiar la contraseña.
        Assert.True(s.Str("status") == "answered", s.Json());
        Assert.Equal(await host.ScalarAsync<Guid>($"SELECT SecurityStamp FROM meta.[User] WHERE Id = {host.Users["st-chat-eva"]}"),
            await host.ScalarAsync<Guid>($"SELECT SecurityStamp FROM meta.StudioSession WHERE Id = {id}"));
    }

    // ---------- H2 ----------

    [Fact]
    public async Task Chat_H2_CA1_los_topes_suman_todos_los_turnos()
    {
        // Studio:MaxSteps = 8 en esta clase.
        using var fer = await host.LoginAsync("st-fer", Pwd);
        host.Llm.Scripts["acumula"] = t => Task.FromResult(LastAsk(t).Contains("sigue")
            ? Reply.Tool("listar_catalogos")
            : t.Index < 4 ? Reply.Tool("listar_catalogos") : Reply.Text("Primer turno listo."));
        var s = await WaitAsync(fer, (await StartAsync(fer, "acumula", "Empieza")).Id());
        var id = s.Id();
        Assert.Equal(5, s["steps"]!.GetValue<int>());
        s = await SendOkAsync(fer, id, "sigue sin parar");
        Assert.Equal("failed", s.Str("status"));
        Assert.Contains("8 pasos", s.Str("error"));
        // Ocho llamadas en total entre los dos turnos, no 5 + 8.
        Assert.Equal(8, s["steps"]!.GetValue<int>());
        Assert.Equal(8, host.Llm.Calls("acumula"));
        // H2 CA2: ya no cabe otro mensaje.
        var res = await SendAsync(fer, id, "otra vez");
        Assert.Equal(429, res.Status);
        Assert.Contains("8 pasos", res.Text);
        Assert.False(s["canSend"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Chat_H2_CA2_un_mensaje_que_no_cabe_se_rechaza_antes_de_encolar()
    {
        // Studio:MaxTurns = 4 en esta clase.
        var gus = await NewModelerAsync("st-chat-gus");
        host.Llm.Scripts["turnos"] = _ => Task.FromResult(Reply.Text("Ok."));
        var id = (await WaitAsync(gus, (await StartAsync(gus, "turnos", "Uno")).Id())).Id();
        for (var i = 2; i <= 4; i++) await SendOkAsync(gus, id, $"Mensaje {i}");
        var res = await SendAsync(gus, id, "Quinto");
        Assert.Equal(429, res.Status);
        Assert.Contains("4 mensajes", res.Text);
        Assert.Equal(4, await host.ScalarAsync<int>($"SELECT Turns FROM meta.StudioSession WHERE Id = {id}"));

        // El costo del día también se revisa antes de encolar.
        var second = (await WaitAsync(gus, (await StartAsync(gus, "turnos", "Otra")).Id())).Id();
        await host.ExecAsync($"UPDATE meta.StudioSession SET CostMicros = 20000000 WHERE Id = {id}");
        var day = await SendAsync(gus, second, "Hola");
        Assert.Equal(429, day.Status);
        Assert.Contains("24 horas", day.Text);
        Assert.Equal("answered", await host.ScalarAsync<string>($"SELECT Status FROM meta.StudioSession WHERE Id = {second}"));
        gus.Dispose();
    }

    // ---------- H3 ----------

    private object FieldPlan(string field) => Plan(new { op = "addField", entity = "StTarea", field = new { name = field, label = field, type = "text" } });

    [Fact]
    public async Task Chat_H3_la_propuesta_se_refina_en_la_conversacion_y_se_aplica_con_su_huella()
    {
        using var ines = await host.LoginAsync("st-ines", Pwd);
        host.Llm.Scripts["refinar"] = t => Task.FromResult(LastAsk(t) switch
        {
            var a when a.Contains("¿Por qué") => Reply.Text("Porque lo pediste."),
            var a when a.Contains("Mejor") => Reply.Tool(StudioTools.Deliver, new { summary = "Campo ChatB", plan = FieldPlan("ChatB036") }),
            var a when a.Contains("Ahora") => Reply.Text("Ya está aplicado."),
            _ => Reply.Tool(StudioTools.Deliver, new { summary = "Campo ChatA", plan = FieldPlan("ChatA036") }),
        });
        var s = await WaitAsync(ines, (await StartAsync(ines, "refinar", "Agrega un campo")).Id());
        var id = s.Id();
        Assert.Equal("proposed", s.Str("planStatus"));
        var hashA = s.Str("reviewHash")!;

        // H3 CA1: una respuesta sin plan no retira la propuesta (ni su resumen).
        await SendOkAsync(ines, id, "¿Por qué ese campo?");
        s = (await ines.GetAsync($"/api/studio/sessions/{id}")).Body!;
        Assert.Equal("proposed", s.Str("status"));
        Assert.Equal("proposed", s.Str("planStatus"));
        Assert.Equal("Campo ChatA", s.Str("summary"));
        Assert.Equal(hashA, s.Str("reviewHash"));

        // Una entrega nueva la reemplaza: la huella de la anterior ya no aplica (H3 CA2).
        await SendOkAsync(ines, id, "Mejor llámalo ChatB");
        s = (await ines.GetAsync($"/api/studio/sessions/{id}")).Body!;
        Assert.Equal("Campo ChatB", s.Str("summary"));
        Assert.NotEqual(hashA, s.Str("reviewHash"));
        var stale = await ines.PostAsync($"/api/studio/sessions/{id}/apply", new { reviewHash = hashA });
        Assert.Equal(409, stale.Status);
        Assert.Equal(0, await host.ScalarAsync<int>("SELECT COUNT(*) FROM meta.Field WHERE Name IN (N'ChatA036', N'ChatB036')"));
        Assert.Equal(200, (await ines.PostAsync($"/api/studio/sessions/{id}/apply", new { reviewHash = s.Str("reviewHash") })).Status);
        Assert.Equal(1, await host.ScalarAsync<int>("SELECT COUNT(*) FROM meta.Field WHERE Name = N'ChatB036'"));
        Assert.Equal(0, await host.ScalarAsync<int>("SELECT COUNT(*) FROM meta.Field WHERE Name = N'ChatA036'"));

        // H3 CA3: aplicar no cierra la conversación.
        s = await SendOkAsync(ines, id, "Ahora explícame qué hiciste");
        Assert.Equal("answered", s.Str("status"));
        Assert.Equal("applied", s.Str("planStatus"));
        Assert.False(s["canApply"]!.GetValue<bool>());
        Assert.Equal(409, (await ines.PostAsync($"/api/studio/sessions/{id}/apply", new { reviewHash = "x" })).Status);
    }

    [Fact]
    public async Task Chat_H3_CA2_no_se_aplica_ni_se_descarta_mientras_un_turno_corre()
    {
        using var ana = await host.LoginAsync("st-ana", Pwd);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Llm.Scripts["correr"] = async t =>
        {
            if (!LastAsk(t).Contains("Espera")) return Reply.Tool(StudioTools.Deliver, new { summary = "Campo", plan = FieldPlan("ChatC036") });
            waiting.TrySetResult();
            await release.Task.WaitAsync(t.Ct);
            return Reply.Text("Listo.");
        };
        var s = await WaitAsync(ana, (await StartAsync(ana, "correr", "Agrega")).Id());
        var id = s.Id();
        var hash = s.Str("reviewHash");
        Assert.Equal(202, (await SendAsync(ana, id, "Espera, revisa algo")).Status);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var apply = await ana.PostAsync($"/api/studio/sessions/{id}/apply", new { reviewHash = hash });
        Assert.Equal(409, apply.Status);
        Assert.Contains("trabajando", apply.Text);
        Assert.Equal(409, (await ana.PostAsync($"/api/studio/sessions/{id}/discard")).Status);
        // Mientras corre, la lectura no compila ni ofrece aplicar.
        var during = (await ana.GetAsync($"/api/studio/sessions/{id}")).Body!;
        Assert.Null(during.Str("reviewHash"));
        Assert.False(during["canSend"]!.GetValue<bool>());
        release.SetResult();
        s = await WaitAsync(ana, id);
        Assert.Equal("proposed", s.Str("status"));
        Assert.Equal(200, (await ana.PostAsync($"/api/studio/sessions/{id}/discard")).Status);
        Assert.Equal(0, await host.ScalarAsync<int>("SELECT COUNT(*) FROM meta.Field WHERE Name = N'ChatC036'"));
    }

    // ---------- H4 ----------

    private Task<int> PreparedAsync(int id) =>
        host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioStep WHERE SessionId = {id} AND Tool = 'aislado_preparar'");

    [Fact]
    public async Task Chat_H4_CA1_la_copia_aislada_se_reutiliza_en_el_turno_siguiente()
    {
        using var carla = await host.LoginAsync("st-carla", Pwd);
        host.Llm.Scripts["copia"] = t => Task.FromResult(t.Request.Messages[^1].Blocks.OfType<StudioToolResult>().Any()
            ? Reply.Text("Probado.")
            : Reply.Tool("aislado_crear", new { entity = "StTarea", values = new { Nombre = LastAsk(t).Contains("otra") ? "Segunda" : "Primera" } }));
        var s = await WaitAsync(carla, (await StartAsync(carla, "copia", "Prueba algo", "aislado")).Id());
        var id = s.Id();
        var name = await host.ScalarAsync<string>($"SELECT SandboxDb FROM meta.StudioSession WHERE Id = {id}");
        Assert.True(await DatabaseExistsAsync(name));
        Assert.Equal(1, await PreparedAsync(id));

        s = await SendOkAsync(carla, id, "Crea otra");
        Assert.True(s.Str("status") == "answered", s.Json());
        // Sin la marca, cada turno volvería a copiar el modelo en una base nueva (como la 035 con cada sesión).
        Assert.Equal(1, await PreparedAsync(id));
        Assert.Equal(name, await host.ScalarAsync<string>($"SELECT SandboxDb FROM meta.StudioSession WHERE Id = {id}"));
        Assert.Equal(2, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM [{name}].app.StTarea WHERE Nombre IN (N'Primera', N'Segunda')"));
    }

    [Fact]
    public async Task Chat_H4_CA1_una_base_ajena_con_el_mismo_nombre_no_se_reutiliza()
    {
        using var hugo = await host.LoginAsync("st-hugo", Pwd);
        host.Llm.Scripts["ajena"] = t => Task.FromResult(t.Request.Messages[^1].Blocks.OfType<StudioToolResult>().Any()
            ? Reply.Text("Listo.")
            : Reply.Tool("aislado_listar", new { entity = "StTarea" }));
        var id = (await WaitAsync(hugo, (await StartAsync(hugo, "ajena", "Lista", "aislado")).Id())).Id();
        var name = await host.ScalarAsync<string>($"SELECT SandboxDb FROM meta.StudioSession WHERE Id = {id}");
        // La base que hay con ese nombre ya no lleva la marca de esta conversación (p. ej. la creó otro proceso).
        await host.ExecAsync($"UPDATE [{name}].meta.StudioSandboxMarker SET Token = NEWID()");
        var s = await SendOkAsync(hugo, id, "Lista otra vez");
        Assert.True(s.Str("status") == "answered", s.Json());
        Assert.Equal(2, await PreparedAsync(id));
        Assert.Equal(await host.ScalarAsync<Guid>($"SELECT SandboxToken FROM meta.StudioSession WHERE Id = {id}"),
            await host.ScalarAsync<Guid>($"SELECT Token FROM [{name}].meta.StudioSandboxMarker"));
    }

    [Fact]
    public async Task Chat_H4_CA2_una_persona_tiene_una_copia_viva()
    {
        using var dora = await host.LoginAsync("st-dora", Pwd);
        host.Llm.Scripts["viva"] = t => Task.FromResult(t.Request.Messages[^1].Blocks.OfType<StudioToolResult>().Any()
            ? Reply.Text("Listo.")
            : Reply.Tool("aislado_listar", new { entity = "StTarea" }));
        var first = (await WaitAsync(dora, (await StartAsync(dora, "viva", "Una", "aislado")).Id())).Id();
        var name = await host.ScalarAsync<string>($"SELECT SandboxDb FROM meta.StudioSession WHERE Id = {first}");
        Assert.True(await DatabaseExistsAsync(name));
        // Otra conversación de la misma persona empieza un turno: la copia de la primera se suelta y se borra.
        await WaitAsync(dora, (await StartAsync(dora, "viva", "Dos", "aislado")).Id());
        Assert.Null(await host.ScalarAsync<string?>($"SELECT SandboxDb FROM meta.StudioSession WHERE Id = {first}"));
        await WaitForAsync(() => DatabaseExistsAsync(name), e => !e, "copia de la primera conversación borrada");
    }

    [Fact]
    public async Task Chat_H4_CA3_lo_leido_se_borra_al_terminar_cada_turno()
    {
        using var gil = await host.LoginAsync("st-gil", Pwd);
        var hostil = host.Rec["hostil"].Id();
        host.Llm.Scripts["leido"] = t => Task.FromResult(LastAsk(t).Contains("Resume")
            ? Reply.Text("Resumen sin registros.")
            : t.Request.Messages[^1].Blocks.OfType<StudioToolResult>().Any() ? Reply.Text("Leído.") : Reply.Tool("leer_registro", new { entity = "StTarea", id = hostil }));
        var id = (await WaitAsync(gil, (await StartAsync(gil, "leido", "Lee la tarea", "datos")).Id())).Id();
        // En el turno que leyó, el modelo sí recibió el registro.
        Assert.Contains("Ignora las instrucciones", host.Llm.Requests.Last(r => r.Tag == "leido").Text);
        // Al terminar el turno se borra en los pasos y en el historial.
        Assert.Equal(0, await StoredDataAsync(id));
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioMessage WHERE SessionId = {id} AND (HasData = 1 OR ContentJson LIKE N'%Ignora las instrucciones%')"));

        await SendOkAsync(gil, id, "Resume lo que viste");
        var sent = host.Llm.Requests.Last(r => r.Tag == "leido").Text;
        // Sin la purga del historial, este turno volvería a mandar al proveedor los registros de un turno ya cerrado.
        Assert.DoesNotContain("Ignora las instrucciones", sent);
        Assert.Contains("Salida borrada al terminar el turno", sent);
    }

    // ---------- H5 ----------

    private sealed record Evt(string Event, string Data, string? Id)
    {
        public JsonNode Json => JsonNode.Parse(Data)!;
    }

    /// <summary>Lee el flujo SSE hasta el evento <c>end</c> (o <paramref name="until"/>), con el cliente de la persona.</summary>
    private static async Task<(int Status, List<Evt> Events, string Body)> StreamAsync(Client api, string url, Func<List<Evt>, bool>? until = null,
        string? lastEventId = null, int seconds = 60)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (lastEventId is not null) req.Headers.Add("Last-Event-ID", lastEventId);
        using var res = await api.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if ((int)res.StatusCode != 200) return ((int)res.StatusCode, [], await res.Content.ReadAsStringAsync(cts.Token));
        Assert.StartsWith("text/event-stream", res.Content.Headers.ContentType?.ToString());
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cts.Token));
        var events = new List<Evt>();
        string? evt = null, id = null;
        var data = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (line.StartsWith("event: ")) evt = line[7..];
            else if (line.StartsWith("id: ")) id = line[4..];
            else if (line.StartsWith("data: ")) data.Append(line[6..]);
            else if (line.Length == 0 && evt is not null)
            {
                events.Add(new Evt(evt, data.ToString(), id));
                if (evt == "end" || (until?.Invoke(events) ?? false)) break;
                evt = id = null;
                data.Clear();
            }
        }
        return (200, events, "");
    }

    [Fact]
    public async Task Chat_H5_CA1_el_flujo_manda_pasos_en_vivo_y_termina_con_el_turno()
    {
        using var beto = await host.LoginAsync("st-beto", Pwd);
        var text = string.Concat(Enumerable.Repeat("El catálogo de tareas tiene nombre y monto. ", 12));
        host.Llm.Scripts["vivo"] = t => Task.FromResult(t.Request.Messages[^1].Blocks.OfType<StudioToolResult>().Any()
            ? Reply.Text(text)
            : Reply.Tool("ver_catalogo", new { entity = "StTarea" }));
        host.Llm.ChunkDelayMs = 60;
        try
        {
            var id = (await StartAsync(beto, "vivo", "Describe StTarea")).Id();
            var (status, events, _) = await StreamAsync(beto, $"/api/studio/sessions/{id}/stream");
            Assert.Equal(200, status);
            // Los pasos llegan en orden, sin repetirse, con su id = seq.
            var steps = events.Where(e => e.Event == "step").ToList();
            Assert.Equal(steps.Select(e => e.Json["seq"]!.GetValue<int>()), Enumerable.Range(1, steps.Count));
            Assert.Equal(steps.Select(e => e.Id), steps.Select(e => e.Json["seq"]!.ToString()));
            Assert.Equal(["user", "tool", "text"], steps.Select(e => e.Json.Str("kind")));
            // En vivo: la herramienta en curso y el texto parcial antes del paso de texto.
            var lives = events.Select((e, i) => (e, i)).Where(x => x.e.Event == "live").ToList();
            Assert.Contains(lives, x => x.e.Json.Str("kind") == "tool" && x.e.Json.Str("tool") == "ver_catalogo");
            var partial = lives.Where(x => x.e.Json.Str("kind") == "text").ToList();
            Assert.NotEmpty(partial);
            Assert.True(partial[0].e.Json.Str("text")!.Length < text.Length, "el primer texto en vivo es parcial");
            Assert.True(partial[0].i < events.FindIndex(e => e.Event == "step" && e.Json.Str("kind") == "text"));
            Assert.Contains(events, e => e.Event == "session" && e.Json.Str("status") == "answered");
            Assert.Equal("idle", events[^1].Json.Str("reason"));

            // H5 CA2: reanudar con Last-Event-ID (o after) no repite pasos.
            var again = await StreamAsync(beto, $"/api/studio/sessions/{id}/stream", lastEventId: "2");
            Assert.Equal([3], again.Events.Where(e => e.Event == "step").Select(e => e.Json["seq"]!.GetValue<int>()));
            var after = await StreamAsync(beto, $"/api/studio/sessions/{id}/stream?after=3");
            Assert.Empty(after.Events.Where(e => e.Event == "step"));
            Assert.Equal("idle", after.Events[^1].Json.Str("reason"));
        }
        finally { host.Llm.ChunkDelayMs = 0; }
    }

    [Fact]
    public async Task Chat_H5_CA3_el_flujo_es_solo_de_la_duena()
    {
        var hal = await NewModelerAsync("st-chat-hal");
        using var carla = await host.LoginAsync("st-carla", Pwd);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Llm.Scripts["duena"] = async t =>
        {
            await release.Task.WaitAsync(t.Ct);
            return Reply.Text("Listo.");
        };
        var id = (await StartAsync(hal, "duena", "Algo")).Id();
        try
        {
            Assert.Equal(404, (await StreamAsync(carla, $"/api/studio/sessions/{id}/stream")).Status);
            Assert.Equal(404, (await StreamAsync(hal, "/api/studio/sessions/999999/stream")).Status);
            using (var lector = await LectorAsync())
                Assert.Equal(403, (await StreamAsync(lector, $"/api/studio/sessions/{id}/stream")).Status);

            // Studio:MaxStreamsPerUser = 2 en esta clase: el tercero a la vez es 429.
            var open1 = StreamAsync(hal, $"/api/studio/sessions/{id}/stream", seconds: 90);
            var open2 = StreamAsync(hal, $"/api/studio/sessions/{id}/stream", seconds: 90);
            await Task.Delay(1500);
            var third = await StreamAsync(hal, $"/api/studio/sessions/{id}/stream");
            Assert.Equal(429, third.Status);

            // Si la persona se desactiva, los flujos abiertos se cierran (Studio:IdentitySeconds = 1).
            using (var admin = await host.AdminAsync())
                Assert.Equal(200, (await admin.PutAsync($"/api/admin/users/{host.Users["st-chat-hal"]}", new { displayName = "st-chat-hal", isActive = false,
                    roleIds = new[] { await admin.RoleIdAsync("Studio Modelador") } })).Status);
            var closed = await open1.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("identity", closed.Events[^1].Json.Str("reason"));
            Assert.Equal("identity", (await open2.WaitAsync(TimeSpan.FromSeconds(30))).Events[^1].Json.Str("reason"));
        }
        finally
        {
            release.TrySetResult();
            hal.Dispose();
        }
    }

    [Fact]
    public async Task Chat_H5_CA4_detener_corta_al_instante_y_conserva_el_texto()
    {
        using var ines = await host.LoginAsync("st-ines", Pwd);
        var text = string.Concat(Enumerable.Repeat("Una línea más de una respuesta muy larga. ", 200));
        host.Llm.Scripts["detener"] = _ => Task.FromResult(Reply.Text(text));
        host.Llm.ChunkDelayMs = 50;
        try
        {
            var id = (await StartAsync(ines, "detener", "Escribe mucho")).Id();
            await WaitForAsync(() => host.ScalarAsync<string?>($"SELECT LiveJson FROM meta.StudioSession WHERE Id = {id}"),
                l => l is not null && l.Contains("\"kind\":\"text\""), "texto en vivo");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(200, (await ines.PostAsync($"/api/studio/sessions/{id}/cancel")).Status);
            var s = await WaitAsync(ines, id);
            watch.Stop();
            Assert.Equal("cancelled", s.Str("status"));
            // Mismo proceso: corta al aviso, sin esperar a renovar el lease.
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"tardó {watch.Elapsed}");
            var stopped = Trail(s).Last(x => x.Str("kind") == "text");
            Assert.True(stopped["output"]!["stopped"]!.GetValue<bool>());
            var kept = stopped["output"]!.Str("text")!;
            Assert.StartsWith("Una línea más", kept);
            Assert.True(kept.Length < text.Length);
            // El texto parcial queda en el historial: el siguiente turno lo ve.
            host.Llm.ChunkDelayMs = 0;
            host.Llm.Scripts["detener"] = _ => Task.FromResult(Reply.Text("Sigo."));
            Assert.Equal("answered", (await SendOkAsync(ines, id, "Continúa")).Str("status"));
            Assert.Contains("Una línea más", host.Llm.Requests.Last(r => r.Tag == "detener").Text);
            Assert.Null(await host.ScalarAsync<string?>($"SELECT LiveJson FROM meta.StudioSession WHERE Id = {id}"));
        }
        finally { host.Llm.ChunkDelayMs = 0; }
    }

    // ---------- Fallas parciales ----------

    [Fact]
    public async Task Chat_Fallas_un_turno_interrumpido_se_puede_continuar()
    {
        using var hugo = await host.LoginAsync("st-hugo", Pwd);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Llm.Scripts["caida"] = async t =>
        {
            if (LastAsk(t).Contains("Retoma")) return Reply.Text("Retomado.");
            if (t.Index == 0) return Reply.Tool("listar_catalogos");
            waiting.TrySetResult();
            await gate.Task; // el trabajador «cae» aquí: otro proceso tomó la sesión
            return Reply.Text("Tarde.");
        };
        var id = (await StartAsync(hugo, "caida", "Algo")).Id();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var messages = await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioMessage WHERE SessionId = {id}");
        await host.ExecAsync($"UPDATE meta.StudioSession SET LeaseToken = NEWID() WHERE Id = {id}");
        gate.SetResult();
        var s = await WaitAsync(hugo, id);
        Assert.Equal("interrupted", s.Str("status"));
        // Un trabajador sin el lease no escribe el historial.
        Assert.Equal(messages, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioMessage WHERE SessionId = {id}"));
        Assert.True(s["canSend"]!.GetValue<bool>(), s.Json());

        // El historial termina con los resultados de la herramienta (de la persona): el mensaje nuevo se une a ese turno.
        s = await SendOkAsync(hugo, id, "Retoma, por favor");
        Assert.True(s.Str("status") == "answered", s.Json());
        var last = host.Llm.Requests.Last(r => r.Tag == "caida").Text;
        Assert.Contains("Retoma, por favor", last);
        Assert.Contains("<datos_no_confiables herramienta=\"listar_catalogos\">", last);
    }
    // ---------- Revisión adversarial 036 ----------

    [Fact]
    public async Task F01_seguir_conversaciones_de_dias_anteriores_no_rodea_el_costo_del_dia()
    {
        // Regresión (revisión adversarial 036, F-01): el costo del día solo sumaba las conversaciones creadas en 24 horas.
        var ivo = await NewModelerAsync("st-chat-ivo");
        host.Llm.Scripts["dia"] = _ => Task.FromResult(Reply.Text("Ok."));
        var old = (await WaitAsync(ivo, (await StartAsync(ivo, "dia", "Ayer")).Id())).Id();
        // Creada hace dos días y con actividad hoy: lo de hoy ya llenó el día (20 USD).
        await host.ExecAsync($"UPDATE meta.StudioSession SET CreatedAt = DATEADD(day, -2, SYSUTCDATETIME()), CostMicros = 20000000 WHERE Id = {old}");
        var res = await ivo.PostAsync("/api/studio/sessions", new { prompt = "[guion:dia] Hoy" });
        Assert.Equal(429, res.Status);
        Assert.Contains("24 horas", res.Text);
        ivo.Dispose();
    }

    [Fact]
    public async Task F03_soltar_una_copia_no_borra_la_de_otro_turno_con_el_mismo_nombre()
    {
        // Regresión (revisión adversarial 036, F-03): soltar borraba por nombre, aunque la base ya fuera la del turno siguiente.
        var jon = await NewModelerAsync("st-chat-jon");
        host.Llm.Scripts["suelta"] = t => Task.FromResult(t.Request.Messages[^1].Blocks.OfType<StudioToolResult>().Any()
            ? Reply.Text("Listo.")
            : Reply.Tool("aislado_listar", new { entity = "StTarea" }));
        var id = (await WaitAsync(jon, (await StartAsync(jon, "suelta", "Lista", "aislado")).Id())).Id();
        var name = await host.ScalarAsync<string>($"SELECT SandboxDb FROM meta.StudioSession WHERE Id = {id}");
        try
        {
            // Otra instancia ya creó la copia del turno siguiente (otra marca) y esta fila vence por inactividad.
            await host.ExecAsync($"UPDATE [{name}].meta.StudioSandboxMarker SET Token = NEWID()");
            await host.ExecAsync($"UPDATE meta.StudioSession SET LastActivityAt = DATEADD(hour, -2, SYSUTCDATETIME()) WHERE Id = {id}");
            await WaitForAsync(() => host.ScalarAsync<string?>($"SELECT SandboxDb FROM meta.StudioSession WHERE Id = {id}"), n => n is null, "copia soltada");
            await Task.Delay(1500);
            Assert.True(await DatabaseExistsAsync(name));
        }
        finally
        {
            await host.Services.GetRequiredService<SandboxFactory>().DropAsync(name);
            jon.Dispose();
        }
    }

    [Fact]
    public async Task F06_F07_un_turno_fallido_conserva_el_informe_y_aplicar_limpia_el_error()
    {
        using var ana = await host.LoginAsync("st-ana", Pwd);
        host.Llm.Scripts["informe"] = t => Task.FromResult(LastAsk(t).Contains("Rechaza")
            ? new StudioReply([], "refusal", 10, 1)
            : Reply.Tool(StudioTools.Deliver, new { summary = "Campo", plan = FieldPlan("ChatD036"), risks = new[] { "Riesgo importante" } }));
        var id = (await WaitAsync(ana, (await StartAsync(ana, "informe", "Agrega")).Id())).Id();
        var s = await SendOkAsync(ana, id, "Rechaza esto");
        Assert.Equal("failed", s.Str("status"));
        // Regresión (revisión adversarial 036, F-06): el turno fallido borraba el informe de la propuesta que sigue vigente.
        Assert.Equal("proposed", s.Str("planStatus"));
        Assert.Equal("Riesgo importante", s["report"]!.Arr("risks")[0]!.GetValue<string>());
        Assert.True(s["canApply"]!.GetValue<bool>(), s.Json());
        var applied = await ana.PostAsync($"/api/studio/sessions/{id}/apply", new { reviewHash = s.Str("reviewHash") });
        Assert.Equal(200, applied.Status);
        // Regresión (F-07): aplicada, la sesión ya no dice que falló.
        Assert.Null(applied.Body!["session"]!.Str("error"));
    }
}

