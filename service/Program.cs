// Сервіс — це наш control plane. LiteLLM тільки ходить у модель, а рішення
// (яку модель брати, коли ретраїти, скільки коштує) робимо тут.
// MODEL=mock — дефолт, грошей не треба. MODEL=gpt-5-mini або claude-haiku + ключ у gateway/.env — реальна модель.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
var app = builder.Build();

// налаштування беремо з оточення (задаються в docker-compose.yml)
var gateway = Environment.GetEnvironmentVariable("GATEWAY_URL") ?? "http://gateway:4000";
var dbConn = Environment.GetEnvironmentVariable("DB_CONN")
    ?? "Host=postgres;Database=llmops;Username=llmops;Password=llmops";
var defaultModel = Environment.GetEnvironmentVariable("MODEL") ?? "mock";

// Прайс (W2): USD за 1k токенів, навчальні значення.
var prices = new Dictionary<string, (decimal InPer1K, decimal OutPer1K)>
{
    ["mock-mini"]   = (0.00015m, 0.0006m),
    ["mock-strong"] = (0.0025m,  0.01m),
};
// W5: послідовні збої на модель → статус провайдера для консолі. Seed з прайсу — картка
// не «—» до першого запиту. 3 = рівно стільки, скільки ментор робить __fail_503 поспіль
// (кожен валить обидві моделі ланцюга → кожна +1).
const int DegradedAfter = 3;
var providerFails = new ConcurrentDictionary<string, int>(prices.Keys.Select(k => KeyValuePair.Create(k, 0)));

// Денний бюджет (W2). Константа — свідоме спрощення курсу. Лежить поруч із прайсом: той самий control plane.
var budgetUsd = 5.0m;

// Кеш відповідей (W3): у пам'яті процесу, живе до рестарту. Без TTL і ліміту розміру — свідоме
// спрощення курсу; кілька реплік → винесене сховище (Redis у профілі advanced).
var cache = new ConcurrentDictionary<string, string>();
var cacheStats = new CacheStats(); // hit/miss — з них у W5 рахується плитка Cache-hit

// HITL (W4): черга заявок на незворотні дії. У пам'яті процесу, як і кеш: рестарт = черга порожня
// (свідоме спрощення; у проді — таблиця з durable термінальним станом).
var approvals = new ConcurrentDictionary<string, Approval>();

// Fallback (W4): лічильник переходів між моделями ланцюга — з нього у W5 живе плитка Fallback.
var fallbackStats = new FallbackStats();

// Graceful degradation (W4): усі моделі ланцюга впали. Чесно і без предметних фактів —
// ні статусу замовлення, ні тікета, ні «зроблено», ні цифр: заглушка не має права щось стверджувати.
const string DegradedAnswer = "Вибачте, у нас тимчасова технічна проблема. Спробуйте, будь ласка, трохи згодом.";
// W5: модель відповіла 200, але без змісту (урок 1: сміття = збій формату). Текст свідомо
// інший, ніж DegradedAnswer: eval має розрізняти «система лежить» і «відповідь непридатна».
const string GarbageAnswer = "Не вдалося сформувати змістовну відповідь. Переформулюйте, будь ласка, питання або попросіть з'єднати з оператором.";

app.MapPost("/chat", async (ChatIn body, IHttpClientFactory httpFactory) =>
{
    var requestId = Guid.NewGuid();
    var startedAt = DateTimeOffset.UtcNow;

    // guardrails (W4): PII маскується ПЕРШИМ рядком — до routing, ключа кешу, виклику моделі й логів.
    // Нижче body.Message не використовується ніде: лише userMessage.
    var userMessage = Guards.MaskPii(body.Message);

    // budget (W2): умова з лога → дія control plane. Політика — біля Route().
    await CheckBudget(app.Logger, dbConn, budgetUsd);

    // routing (W2): ескалація -> сильна модель, решта -> дешева. Політика — у Route().
    var model = Route(userMessage, defaultModel);

    string promptVersion, systemPrompt;
    try
    {
        (promptVersion, systemPrompt) = await GetActivePrompt(dbConn);
    }
    catch (Exception ex)
    {
        return RegistryUnavailable(app.Logger, ex, requestId);
    }

    // cache (W3): ключ = модель + промпт (версія і текст) + повідомлення.
    // Промпт у ключі: promote/rollback дає інші ключі → старі відповіді недосяжні без коду інвалідації.
    // Текст, а не лише версія: ловить і правку body «на місці» без нової версії.
    // W4: з маскованого повідомлення — email не осідає в ключах, а повідомлення, що різняться лише email-ом, дають hit.
    var cacheKey = $"{model}|{promptVersion}|{systemPrompt}|{userMessage}";
    if (cache.TryGetValue(cacheKey, out var cached))
    {
        Interlocked.Increment(ref cacheStats.Hits);
        var hitLatencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        // виклику моделі не було: 0 токенів і cost_usd = 0 (не null) — економія видима в лозі
        await LogRequest(dbConn, requestId, model, promptVersion, hitLatencyMs, 0, 0, 0m, 200);
        return Results.Json(new { request_id = requestId, content = cached, tool = (string?)null, latency_ms = hitLatencyMs });
    }
    Interlocked.Increment(ref cacheStats.Misses);

    // fallback (W4): ідемо по ланцюгу з Chain() (політика — біля Route()). Збій спроби —
    // 429/5xx, таймаут, мережа або нерозбірна відповідь → наступна модель; кожен перехід
    // рахується у fallbackStats. Інші 4xx (400/401/404) — помилка запиту чи конфігу:
    // інша модель її не виправить, тому ланцюг обривається одразу.
    var http = httpFactory.CreateClient();
    var chain = Chain(model);
    var r = new GatewayResult(false, 0, "", null, 0, 0);
    var ok = false;
    var answeredBy = -1; // індекс моделі, що відповіла; 0 = основна
    var garbage = false; // W5: остання спроба — 200 без змісту
    for (var i = 0; i < chain.Length; i++)
    {
        if (i > 0)
        {
            Interlocked.Increment(ref fallbackStats.Transitions);
            app.Logger.LogWarning("fallback {From} -> {To} after status={Status}, request_id={RequestId}",
                chain[i - 1], chain[i], r.Status, requestId);
        }
        r = await CallGateway(http, app.Logger, gateway, chain[i], systemPrompt, userMessage, requestId);
        model = chain[i];

        // W5: 200 без змісту = збій спроби, як нерозбірний JSON → наступна модель.
        // ДО providerFails: сміття — теж ознака нездоров'я моделі.
        garbage = r.Ok && IsGarbage(r);
        if (garbage)
        {
            app.Logger.LogWarning("garbage answer model={Model} request_id={RequestId}", chain[i], requestId);
            r = r with { Ok = false, Status = 502 }; // upstream віддав непридатну відповідь; 502 retryable
        }

        if (r.Ok) providerFails[chain[i]] = 0;
        else if (IsRetryable(r.Status)) providerFails.AddOrUpdate(chain[i], 1, (_, n) => n + 1);
        if (r.Ok) { ok = true; answeredBy = i; break; }
        if (!IsRetryable(r.Status)) break;
    }

    var answer = r.Answer;
    var toolCall = r.Tool;
    var status = r.Status;

    if (!ok)
    {
        // graceful degradation (W4): клієнту — HTTP 200 і чесна заглушка. У requests — статус збою,
        // а не 200: інакше error rate (W5) не побачить інциденту. 0 (таймаут/мережа) і 2xx
        // з нерозбірним тілом → 503, бо HTTP-статусу, що описує збій, немає.
        answer = garbage ? GarbageAnswer : DegradedAnswer;
        if (status is 0 or (>= 200 and < 300)) status = 503;
        app.Logger.LogWarning("all models failed, degraded answer, chain={Chain} status={Status} request_id={RequestId}",
            string.Join(" -> ", chain), status, requestId);
    }

    // tools (W3): виконуємо інструмент і підклеюємо результат до відповіді (один хоп, без
    // повторного виклику моделі — спрощення курсу). Невідомий інструмент → не виконуємо, лише лог.
    // HITL (W4): незворотна дія тут НЕ виконується — стає заявкою в черзі; виконає її лише
    // POST /approvals/{id}/approve. Рішення «чи потрібне підтвердження» — в одному місці: RequiresApproval().
    if (ok && toolCall != null)
    {
        if (RequiresApproval(toolCall))
        {
            var approvalId = Guid.NewGuid().ToString("N")[..8];
            approvals[approvalId] = new Approval(toolCall, "pending", null, DateTimeOffset.UtcNow, requestId);
            app.Logger.LogInformation("approval queued id={ApprovalId} action={Action} request_id={RequestId}",
                approvalId, toolCall, requestId);
            // Текст моделі («Створюю тікет…») замінюємо: до approve дії не було, стверджувати її не можна.
            // Без номерів і id — номер тікета з'явиться лише після approve, у GET /approvals.
            answer = "Передав ваш запит на створення тікета оператору — тікет буде створено після його підтвердження.";
        }
        else
        {
            var toolResult = RunTool(toolCall);
            if (toolResult != null) answer += $" ({toolResult})";
            else app.Logger.LogWarning("unknown tool {Tool}, request_id={RequestId}", toolCall, requestId);
        }
    }

    // у кеш — лише відповідь, у якій впевнені. Не кешуємо: збій або нерозібрану відповідь
    // (заглушка «тимчасово недоступний» закріпилась би після відновлення), tool-виклик
    // (результат = стан зовнішньої системи на момент виклику), резервний промпт "none"
    // (деградація реєстру), порожню відповідь. W4: і відповідь резервної моделі (answeredBy > 0) —
    // ключ рахується з основної моделі, тож відповідь резерву закріпилась би під ним і після відновлення.
    if (ok && answeredBy == 0 && status == 200 && toolCall == null && promptVersion != "none"
        && !string.IsNullOrWhiteSpace(answer))
        cache[cacheKey] = answer;

    var latencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

    // cost (W2): usage → тариф моделі → cost_usd. Вартість відома лише після відповіді.
    // null = невідомо (usage не прийшов або моделі немає в прайсі). Це не 0:
    // 0 = «виклик нічого не коштував», легально лише для cache hit (W3).
    // W5: за сміття токени реально витрачені → рахуємо (лише останньої спроби — спрощення)
    var costUsd = ok || garbage ? CostUsd(prices, model, r.PromptTokens, r.CompletionTokens) : null;

    // лог кожного запиту — з цього живе observability (W1) і cost (W2)
    await LogRequest(dbConn, requestId, model, promptVersion, latencyMs, r.PromptTokens, r.CompletionTokens, costUsd, status);

    return Results.Json(new { request_id = requestId, content = answer, tool = toolCall, latency_ms = latencyMs });
});

// ці ендпоінти читає готова консоль. поверни потрібну форму — картки оживуть.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));                                    // ліфнес, не для консолі

// W5: requests / p95 / error rate — з БД (переживають рестарт); cache-hit і fallback — з пам'яті процесу.
// COALESCE: на порожній таблиці percentile_cont і AVG повертають NULL → читання в double/decimal = 500.
app.MapGet("/observability", async () =>
{
    try
    {
        await using var db = new NpgsqlConnection(dbConn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*), "
            + "COALESCE(percentile_cont(0.95) WITHIN GROUP (ORDER BY latency_ms), 0), "
            + "COALESCE(AVG(CASE WHEN status <> '200' THEN 1.0 ELSE 0 END) * 100, 0) "
            + "FROM requests WHERE created_at::date = CURRENT_DATE", db);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        var requests  = rd.GetInt64(0);    // count(*)        → bigint
        var p95       = rd.GetDouble(1);   // percentile_cont → double precision
        var errorRate = rd.GetDecimal(2);  // AVG(numeric)    → numeric

        var hits  = Interlocked.Read(ref cacheStats.Hits);
        var total = hits + Interlocked.Read(ref cacheStats.Misses);
        return Results.Json(new
        {
            p95_ms = (int)Math.Round(p95),   // Round, не (int): ментор звіряє з round(...) у SQL, допуск ±1 мс
            requests,
            cache_hit_pct = total == 0 ? 0 : Math.Round(hits * 100.0 / total, 1),
            error_rate_pct = Math.Round(errorRate, 1),
            fallback_events = Interlocked.Read(ref fallbackStats.Transitions),
        });
    }
    catch (Exception ex) // як у /cost: БД лежить → 503, а не нулі (нулі = «спокійна система» посеред інциденту)
    {
        app.Logger.LogError(ex, "observability query failed");
        return Results.Problem("Observability data unavailable", statusCode: 503);
    }
});

app.MapGet("/cost", async () =>
{
    try
    {
        return Results.Json(new { today_usd = await TodayCostUsd(dbConn), budget_usd = budgetUsd });
    }
    catch (Exception ex) // не лише NpgsqlException: якщо хост БД не резолвиться, Npgsql кидає голий SocketException
    {
        app.Logger.LogError(ex, "cost query failed");
        return Results.Problem("Cost data unavailable", statusCode: 503);
    }
});
// W1: усі версії, консоль сама позначає активну
app.MapGet("/prompts", async () =>
{
    try
    {
        await using var db = new NpgsqlConnection(dbConn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT name, version, active FROM prompts ORDER BY created_at", db);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<object>();
        while (await reader.ReadAsync())
            list.Add(new { name = reader.GetString(0), version = reader.GetString(1), active = reader.GetBoolean(2) });
        return Results.Json(list);
    }
    catch (Exception ex)
    {
        return RegistryUnavailable(app.Logger, ex, Guid.NewGuid());
    }
});

// W1: promote і rollback — одна атомарна операція. Невідома версія → 404, стан не змінюється.
app.MapPost("/prompts/{version}/activate", async (string version) =>
{
    try
    {
        await using var db = new NpgsqlConnection(dbConn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE prompts SET active = (version = @v) WHERE name = 'support-system' "
            + "AND EXISTS (SELECT 1 FROM prompts WHERE name = 'support-system' AND version = @v)", db);
        cmd.Parameters.AddWithValue("v", version);
        var affected = await cmd.ExecuteNonQueryAsync();
        return affected == 0
            ? Results.NotFound(new { error = $"prompt version '{version}' not found" })
            : Results.Ok(new { name = "support-system", version, active = true });
    }
    catch (Exception ex)
    {
        return RegistryUnavailable(app.Logger, ex, Guid.NewGuid());
    }
});

// W5: чесний статус із результатів ланцюга, а не константа "ok" (вона бреше саме під час інциденту).
// Cache hit статус не оновлює: виклику моделі не було.
app.MapGet("/providers", () => Results.Json(new
{
    providers = providerFails.OrderBy(p => p.Key)
        .Select(p => new { name = p.Key, status = p.Value >= DegradedAfter ? "degraded" : "ok" }),
}));

// W4: черга HITL. pending — для картки Approvals у консолі; done — слід виконання (result = номер тікета):
// без нього «рівно один тікет» неможливо перевірити.
app.MapGet("/approvals", () =>
{
    var all = approvals.ToArray(); // знімок: черга може мінятися під час серіалізації
    return Results.Json(new
    {
        pending = all.Where(p => p.Value.State == "pending")
            .OrderBy(p => p.Value.CreatedAt)
            .Select(p => new { id = p.Key, action = p.Value.Action, created_at = p.Value.CreatedAt })
            .ToArray(),
        done = all.Where(p => p.Value.State != "pending")
            .OrderByDescending(p => p.Value.CreatedAt)
            .Select(p => new { id = p.Key, action = p.Value.Action, state = p.Value.State, result = p.Value.Result })
            .ToArray(),
    });
});

// W4: approve. Check-then-act атомарно: TryUpdate переводить pending → running лише якщо заявка не
// змінилась між читанням і записом — з двох паралельних approve виграє один, другий отримує 404.
// Повтор і невідомий id → однаковий 404: відповідь не підказує, які id існують.
app.MapPost("/approvals/{id}/approve", (string id) =>
{
    if (!approvals.TryGetValue(id, out var a) || a.State != "pending"
        || !approvals.TryUpdate(id, a with { State = "running" }, a)) // record → порівняння за значенням
        return Results.NotFound(new { error = "already done" });

    try
    {
        var result = RunTool(a.Action) ?? "виконано";
        approvals[id] = a with { State = "approved", Result = result };
        app.Logger.LogInformation("approval approved id={ApprovalId} action={Action} result={Result} request_id={RequestId}",
            id, a.Action, result, a.RequestId);
        return Results.Ok(new { id, action = a.Action, result });
    }
    catch (Exception ex)
    {
        // термінальний стан, а не вічне running; повторно не виконується (повтор → 404)
        approvals[id] = a with { State = "failed" };
        app.Logger.LogError(ex, "approval failed id={ApprovalId} request_id={RequestId}", id, a.RequestId);
        return Results.Problem("tool failed", statusCode: 500);
    }
});

app.Run("http://0.0.0.0:8080");

// Routing-політика (W2): повернення, скарги й термінові звернення -> mock-strong,
// бо помилка на них дорожча; решта -> mock-mini. Консервативно: хоч один маркер -> strong.
// Маркери — UA-основи + EN, бо роутер на одній мові = половина роутера.
// Реальна модель у MODEL -> поки без маршрутизації (gpt-5-mini/gpt-5 — наступний крок).
//
// fallback chain (W4) — див. Chain() нижче. mock-strong і mock-mini ведуть на один
// mock-provider, тож це перевірка логіки перемикання, а не справжній резерв.
//
// budget policy (80% денного бюджету): алерт — warning у лог сервісу (у проді — черговому), маршрути не змінюємо.
// Чому не деградація: звичайні запити й так ідуть на mock-mini, тож деградувати можна лише
// ескалації, а там помилка (повернення, скарга) дорожча за перевитрату.
// Чому не відсікання: підтримка без відповіді гірша за перевищення бюджету на центи.
static string Route(string message, string def)
{
    if (def != "mock") return def;

    string[] escalationMarkers = { "поверн", "терміново", "скарг", "refund", "urgent", "complain" };
    var u = message.ToLowerInvariant();
    return escalationMarkers.Any(m => u.Contains(m)) ? "mock-strong" : "mock-mini";
}

// Fallback-ланцюг (W4): основна модель → резерв → (усі впали) graceful degradation.
//   mock-strong -> mock-mini   : ескалація не лишається без відповіді; резерв ще й дешевший.
//   mock-mini   -> mock-strong : дорожче, але відповідь важливіша за центи (та сама логіка, що в budget policy).
//   реальна     -> інша родина : резерв в іншого провайдера = інший домен відмови (потрібні обидва ключі).
// Retry тієї самої моделі свідомо не робимо (рішення L07): при 429 у проді тут стояв би backoff.
static string[] Chain(string primary) => primary switch
{
    "mock-strong" => new[] { "mock-strong", "mock-mini" },
    "mock-mini"   => new[] { "mock-mini", "mock-strong" },
    _             => new[] { primary, primary.StartsWith("claude") ? "gpt-5-mini" : "claude-haiku" },
};

// Чи має сенс іти на наступну модель: так — 429, 5xx, 0 (таймаут/мережа), 2xx з нерозбірним тілом.
// Ні — решта 4xx: це помилка запиту чи конфігу, інша модель її не виправить.
static bool IsRetryable(int status) => status == 429 || status is not (>= 400 and < 500);

// W5: сміття = менше 3 літер/цифр і без tool-виклику. Tool без тексту — легально
// (у реальних моделей content при tool_calls часто порожній).
static bool IsGarbage(GatewayResult r) => r.Tool is null && r.Answer.Count(char.IsLetterOrDigit) < 3;

// Одна спроба виклику моделі через gateway. Збій — значення (Ok=false), а не виняток:
// ланцюг у /chat вирішує, що робити далі. Таймаут на спробу 5 с: __delay (2 с) проходить,
// два елементи ланцюга вкладаються в 10 с — менше за таймаут evals (30 с).
// Тіло запиту/відповіді не логуємо (там повідомлення користувача) — лише тип збою, модель і request_id.
static async Task<GatewayResult> CallGateway(HttpClient http, ILogger log, string gateway, string model,
    string systemPrompt, string userMessage, Guid requestId)
{
    var payload = JsonSerializer.Serialize(new
    {
        model,
        messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userMessage }
        }
    });

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var status = 0; // 0 = HTTP-відповіді не було
    try
    {
        using var response = await http.PostAsync(
            $"{gateway}/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"), cts.Token);
        status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            log.LogWarning("gateway status={Status} model={Model} request_id={RequestId}", status, model, requestId);
            return new GatewayResult(false, status, "", null, 0, 0);
        }

        var rawJson = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(rawJson);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var answer = message.GetProperty("content").GetString() ?? "";

        // tools (W3): тут лише читаємо, ЩО модель попросила. Виконання — у /chat після ланцюга,
        // щоб побічна дія не стартувала на відповіді, яка ще може впасти на розборі.
        // Один хоп і перший виклик: mock завжди просить один інструмент.
        string? toolCall = null;
        if (message.TryGetProperty("tool_calls", out var tools)
            && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
        {
            toolCall = tools[0].GetProperty("function").GetProperty("name").GetString();
        }

        var usage = doc.RootElement.GetProperty("usage");
        return new GatewayResult(true, status, answer, toolCall,
            usage.GetProperty("prompt_tokens").GetInt32(), usage.GetProperty("completion_tokens").GetInt32());
    }
    catch (Exception ex) // таймаут (TaskCanceledException), мережа, нерозбірний JSON
    {
        log.LogWarning("gateway call failed: {Error} status={Status} model={Model} request_id={RequestId}",
            ex.GetType().Name, status, model, requestId);
        return new GatewayResult(false, status, "", null, 0, 0);
    }
}

// Мінімальний реєстр інструментів (W3): назва → результат. Заглушки замість CRM/тікет-системи.
// lookup_order — read-only, повтор безпечний. create_ticket — side-effecting: повтор = ще один тікет,
// тому йде через HITL-чергу (W4, RequiresApproval). Невідома назва → null.
static string? RunTool(string name) => name switch
{
    "lookup_order"  => "статус: оплачено, доставку призначено",
    "create_ticket" => "тікет #T-" + Guid.NewGuid().ToString("N")[..4],
    _ => null,
};

// HITL-гейт (W4): ЄДИНЕ місце, де вирішується «чи потрібне підтвердження людини».
// Незворотні (side-effecting) дії — так, read-only — ні. Новий незворотний інструмент = рядок тут.
static bool RequiresApproval(string tool) => tool is "create_ticket";

// Вартість одного виклику: вхідні й вихідні токени — кожні за своїм тарифом (за 1k).
// Немає тарифу → null: такі рядки знаходить WHERE cost_usd IS NULL, і прайс правиться як баг.
// Округлення до 6 знаків = NUMERIC(10,6) у requests. AwayFromZero — як round() у Postgres
// (за замовчуванням Math.Round у .NET банківський, ToEven).
// prices — параметром: static-функція не бачить локальних змінних зовні (CS8421).
// Плюс це чиста функція, її легко покрити unit-тестом.
static decimal? CostUsd(Dictionary<string, (decimal InPer1K, decimal OutPer1K)> prices,
    string model, int promptTokens, int completionTokens)
{
    if (!prices.TryGetValue(model, out var p)) return null;
    var cost = promptTokens / 1000m * p.InPer1K + completionTokens / 1000m * p.OutPer1K;
    return Math.Round(cost, 6, MidpointRounding.AwayFromZero);
}

// Budget-політика (W2, опційно): ≥80% денного бюджету → warning у лог сервісу.
// Той самий запит, що й /cost: плитка й алерт не можуть розійтися.
// Пише на кожен запит понад поріг. У проді дедуплікацію робить система алертів.
// БД недоступна → тихо виходимо: облік не блокує відповідь (/chat сам віддасть 503 на реєстрі промптів).
static async Task CheckBudget(ILogger log, string conn, decimal budget)
{
    try
    {
        var today = await TodayCostUsd(conn);
        if (today >= budget * 0.8m)
            log.LogWarning("budget alert: today_usd={Today} >= 80% of budget_usd={Budget}, routing unchanged",
                today, budget);
    }
    catch (Exception ex) // ширше за NpgsqlException — див. /cost
    {
        // облік не блокує відповідь: /chat далі сам віддасть 503 на реєстрі промптів
        log.LogWarning(ex, "budget check skipped: cost data unavailable");
    }
}

// Витрати за сьогодні
static async Task<decimal> TodayCostUsd(string conn)
{
    await using var db = new NpgsqlConnection(conn);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT round(COALESCE(SUM(cost_usd), 0), 4) FROM requests WHERE created_at::date = CURRENT_DATE", db);
    return (decimal)(await cmd.ExecuteScalarAsync())!;
}

// пише один рядок у requests. якщо лог впав — запит користувача все одно віддаємо.
static async Task LogRequest(string conn, Guid id, string model, string promptVersion, int latency,
    int promptTokens, int completionTokens, decimal? cost, int status)
{
    try
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO requests (request_id, model, prompt_version, latency_ms, prompt_tokens, completion_tokens, cost_usd, status) "
            + "VALUES (@id, @model, @pv, @lat, @pt, @ct, @cost, @status)", db);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("model", model);
        cmd.Parameters.AddWithValue("pv", promptVersion);
        cmd.Parameters.AddWithValue("lat", latency);
        cmd.Parameters.AddWithValue("pt", promptTokens);
        cmd.Parameters.AddWithValue("ct", completionTokens);
        cmd.Parameters.AddWithValue("cost", (object?)cost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", status.ToString());
        await cmd.ExecuteNonQueryAsync();
    }
    catch { /* не валимо запит через лог */ }
}

static async Task<(string Version, string Body)> GetActivePrompt(string conn)
{
    await using var db = new NpgsqlConnection(conn);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT version, body FROM prompts WHERE name = 'support-system' AND active = true "
        + "ORDER BY created_at DESC LIMIT 1", db);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (await reader.ReadAsync())
        return (reader.GetString(0), reader.GetString(1));
    return ("none", "You are an assistant.");
}

// БД реєстру недоступна → 503 з request_id: за ним інцидент шукається в логах сервісу
// (у requests рядка не буде — та сама БД лежить).
static IResult RegistryUnavailable(ILogger log, Exception ex, Guid requestId)
{
    log.LogError(ex, "prompt registry unavailable, request_id={RequestId}", requestId);
    return Results.Problem("Prompt registry unavailable", statusCode: 503,
        extensions: new Dictionary<string, object?> { ["request_id"] = requestId });
}

record ChatIn(string Message);

// Лічильники кешу. Поля, а не властивості: Interlocked.Increment(ref ...) приймає лише поле/змінну.
// Клас (reference type), щоб лямбди /chat і /observability (W5) бачили той самий екземпляр.
class CacheStats
{
    public long Hits;
    public long Misses;
}

// Лічильник переходів між моделями ланцюга (W4) → fallback_events у /observability (W5).
// Поле і клас — з тих самих причин, що й CacheStats.
class FallbackStats
{
    public long Transitions;
}

// Результат однієї спроби виклику моделі (W4). Status = 0 — HTTP-відповіді не було.
// Заявка HITL (W4). State: pending → running → approved | failed. Record — щоб TryUpdate порівнював за значенням.
record Approval(string Action, string State, string? Result, DateTimeOffset CreatedAt, Guid RequestId);

record GatewayResult(bool Ok, int Status, string Answer, string? Tool, int PromptTokens, int CompletionTokens);
