# Incident runbook

Команди виконуються з кореня репозиторію. Сервіс — `localhost:8080`.
Якщо стандартні порти зайняті — піднімай із `demo-ports.yml` (§0) і підстав `8180` замість `8080`
(UI — `4300`).
Потрібні: `curl`, `jq`; для §2 — Python 3 і `pip install -r evals/requirements.txt`.

## 0. Чистий старт демо

```bash
docker compose down -v
docker compose up --build -d
# або, якщо 8080 / 4200 / 4000 / 5432 зайняті:
# docker compose -f docker-compose.yml -f demo-ports.yml up --build -d
for i in $(seq 1 40); do curl -sf localhost:8080/health && break; sleep 5; done; echo
sleep 30   # холодний gateway дає 500 на перший запит
```

`down -v` чистить лог (`requests`) і чергу approvals; рестарт сервісу обнуляє кеш і лічильники
`fallback_events` / cache-hit (вони в пам'яті процесу). Час у БД (`created_at`) — UTC.

## 1. Провайдер недоступний (503)

| Крок | Що | Як |
|---|---|---|
| Симптом | «бот вибачається», росте частка помилок | плитки Errors і Fallback у консолі |
| Підтвердження | ростуть `fallback_events` і `error_rate_pct`, у лозі статус `503` | 1.3 |
| Дія | вручну нічого не вмикати: fallback і деградація спрацьовують автоматично — перевірити, що користувач отримує заглушку, а не 500 | 1.2 |
| Перевірка відновлення | **нове** питання відповідає по суті і в лозі має токени > 0 (відповідь моделі, не кешу); `fallback_events` перестав рости, `error_rate_pct` спадає | 1.4 |

### 1.1 Норма

```bash
curl -s -X POST localhost:8080/chat -H 'Content-Type: application/json' \
  -d '{"message":"Як скинути пароль?"}' | jq '{content, latency_ms}'
curl -s localhost:8080/observability | jq
```

### 1.2 Інцидент — два запити з маркером збою провайдера

```bash
for i in 1 2; do
  curl -s -X POST localhost:8080/chat -H 'Content-Type: application/json' \
    -d '{"message":"__fail_503 Як скинути пароль?"}' | jq -r .content
done
```

Очікується заглушка: «Вибачте, у нас тимчасова технічна проблема. Спробуйте, будь ласка, трохи згодом.» — не HTTP 500.

### 1.3 Підтвердження

```bash
curl -s localhost:8080/observability | jq '{fallback_events, error_rate_pct, requests}'
curl -s localhost:8080/providers | jq -c
docker compose exec -T postgres psql -U llmops -d llmops -c \
  "SELECT created_at::time(0), model, latency_ms, prompt_tokens, completion_tokens, status
   FROM requests ORDER BY created_at DESC LIMIT 5;"
docker compose logs service --since 10m | grep -E "fallback|all models failed"
```

Очікується: `fallback_events` = 2 (по одному переходу mini → strong на запит), `error_rate_pct` ≈ 66.7,
два рядки `mock-strong · 503`, у логах `fallback mock-mini -> mock-strong` і `all models failed`.
`/providers` після двох збоїв ще `ok` — `degraded` ставиться з третього збою поспіль; щоб побачити
червону картку провайдера, у 1.2 зроби `for i in 1 2 3`.

### 1.4 Відновлення — нове питання, якого ще не було в кеші

```bash
curl -s -X POST localhost:8080/chat -H 'Content-Type: application/json' \
  -d '{"message":"Що ти можеш?"}' | jq '{content, latency_ms}'
curl -s localhost:8080/observability | jq '{fallback_events, error_rate_pct}'
docker compose exec -T postgres psql -U llmops -d llmops -c \
  "SELECT created_at::time(0), model, prompt_tokens, completion_tokens, status
   FROM requests ORDER BY created_at DESC LIMIT 1;"
```

- відповідь по суті, в останньому рядку лога `200` і токени > 0 → відповідь моделі, не кешу
  (cache hit пишеться з токенами 0/0);
- `fallback_events` не зріс відносно 1.3 (лічильник кумулятивний — він не спадає);
- `error_rate_pct` нижчий, ніж у 1.3 (66.7 → 50.0).

Не перевіряти відновлення питанням «Як скинути пароль?»: воно закешоване в 1.1 і відповість
навіть посеред інциденту (токени 0/0 у лозі).

### 1.5 Кеш

Очищати не треба: заглушка деградації в кеш не пишеться (кешується лише успішна відповідь
основної моделі без tool-виклику). Якщо колись знадобиться — `docker compose restart service`
(кеш у пам'яті; разом із ним обнуляться `fallback_events` і cache-hit).

## 2. Погіршення якості (HTTP-метрики в нормі)

| Крок | Що | Як |
|---|---|---|
| Симптом | скарги на некоректні відповіді, error rate у нормі | — |
| Підтвердження | `prompt_version` останніх відповідей збігається з часом скарг; evals падають | 2.1 |
| Дія | відкат на попередню версію промпта однією командою | 2.2 |
| Перевірка | evals ≥ 9/10, скарг менше | 2.3 |

```bash
# 2.1
curl -s localhost:8080/prompts | jq
docker compose exec -T postgres psql -U llmops -d llmops -c \
  "SELECT prompt_version, count(*), min(created_at), max(created_at)
   FROM requests WHERE created_at > now() - interval '1 hour' GROUP BY 1;"
python evals/run.py --dataset evals/golden.jsonl --threshold 9; echo "exit=$?"
# 2.2
curl -s -X POST localhost:8080/prompts/v2/activate | jq
# 2.3
python evals/run.py --dataset evals/golden.jsonl --threshold 9; echo "exit=$?"
```

`run.py` за замовчуванням ходить на `localhost:8080`; на зсунутих портах — `SERVICE_URL=http://localhost:8180`.
Відтворити для демо: `curl -s -X POST localhost:8080/prompts/v1/activate` → evals `7/10`, exit 1 → 2.2.

## 3. Зростання витрат

| Крок | Що | Як |
|---|---|---|
| Симптом | плитка вартості росте швидше за трафік / `budget alert` у лозі | `docker compose logs service \| grep "budget alert"` |
| Підтвердження | 3 зрізи: коли → яка модель → яка версія промпта і довжина входу | SQL нижче |
| Дія | причина — промпт → відкат (2.2); причина — routing (strong на звичайних запитах) → revert PR роутера | — |
| Перевірка | ті самі 3 зрізи після дії: розподіл моделей і `avg_in` повернулись | — |

```bash
curl -s localhost:8080/cost | jq
docker compose exec -T postgres psql -U llmops -d llmops -c \
  "SELECT created_at::date AS day, round(sum(cost_usd), 6) FROM requests GROUP BY 1 ORDER BY 1;"
docker compose exec -T postgres psql -U llmops -d llmops -c \
  "SELECT model, count(*), round(sum(cost_usd), 6) FROM requests
   WHERE created_at::date = CURRENT_DATE GROUP BY 1;"
docker compose exec -T postgres psql -U llmops -d llmops -c \
  "SELECT prompt_version, count(*), round(avg(prompt_tokens)) AS avg_in FROM requests
   WHERE created_at::date = CURRENT_DATE GROUP BY 1;"
```

## Rollback-критерії (записані до релізу)

Відкочуємо промпт, якщо після активації нової версії:
- evals < 9/10 (прогін одразу після активації), **або**
- `error_rate_pct` > 5 % протягом 15 хв, **або**
- `fallback_events` росте протягом 15 хв.

Хто: черговий, без погоджень. Як: `curl -s -X POST localhost:8080/prompts/v2/activate`
(остання перевірена версія). Розбір — після відкату, не замість нього.

## Хронологія навчального інциденту

Прогін 2026-10-30 на чистому клоні (`demo-ports.yml`, сервіс на 8180), кроки §1 виконані підряд —
тому інтервали в секундах. Час локальний (у БД той самий момент — 02:00 UTC).

| Час | Подія |
|---|---|
| 04:00:37 | норма: «Як скинути пароль?» → відповідь по суті, `mock-mini · 200` |
| 04:00:38 | початок: 2 запити з `__fail_503` → `mock-mini` 503 → fallback на `mock-strong` 503 → заглушка |
| 04:00:38 | виявлено: `fallback_events` 0 → 2, `error_rate_pct` 0 → 66.7; у лозі `mock-strong · 503` ×2 |
| 04:00:38 | користувач бачив: «Вибачте, у нас тимчасова технічна проблема…» — не 500 |
| 04:00:38 | відновлено: «Що ти можеш?» → відповідь по суті; `fallback_events` лишився 2, `error_rate_pct` 66.7 → 50.0 |

**Що зробимо, щоб не повторилося?** Регресійна перевірка вже є: кейси з `forbid` на текст
заглушки і прогін «0/10 під `__fail_503`» (`evals/README.md`). Наступний механізм — circuit
breaker: під час збою запити не чекатимуть таймаутів кожної моделі ланцюга.

**Як ми дізналися?** З консолі — лише тому, що на неї дивились. Алерту немає: наступний крок —
алерт черговому на `error_rate_pct` > 5 % протягом 15 хв (Тема 9), щоб інцидент було видно
раніше за скарги.
