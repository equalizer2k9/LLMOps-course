# Incident runbook

Команди виконуються з кореня репозиторію. Сервіс — `localhost:8080`.
Якщо стек піднято з `demo-ports.yml` — підстав `8180` замість `8080` (UI — `4300`).
Потрібні: `curl`, `jq`; для §2 — Python 3 і `pip install -r evals/requirements.txt`.

## 0. Чистий старт демо

```bash
docker compose down -v
docker compose up --build -d
until curl -sf localhost:8080/health; do sleep 3; done; echo
sleep 30   # холодний gateway дає 500 на перший запит
```

`down -v` чистить лог (`requests`) і чергу approvals; рестарт сервісу обнуляє кеш і лічильники
`fallback_events` / cache-hit (вони в пам'яті процесу).

## 1. Провайдер недоступний (503)

| Крок | Що | Як |
|---|---|---|
| Симптом | «бот вибачається», росте частка помилок | плитки Errors і Fallback у консолі |
| Підтвердження | ростуть `fallback_events` і `error_rate_pct`, у лозі статус `503` | 1.3 |
| Дія | вручну нічого не вмикати: fallback і деградація спрацьовують автоматично — перевірити, що користувач отримує заглушку, а не 500 | 1.2 |
| Перевірка відновлення | **нове** питання (не з кешу) відповідає по суті; `fallback_events` перестав рости, `error_rate_pct` спадає | 1.4 |

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
curl -s localhost:8080/providers | jq
docker compose exec -T postgres psql -U llmops -d llmops -c \
  "SELECT created_at, model, latency_ms, prompt_tokens, completion_tokens, status
   FROM requests ORDER BY created_at DESC LIMIT 5;"
docker compose logs service --since 10m | grep -E "fallback|all models failed"
```

### 1.4 Відновлення — нове питання, якого ще не було в кеші

```bash
curl -s -X POST localhost:8080/chat -H 'Content-Type: application/json' \
  -d '{"message":"Що ти можеш?"}' | jq '{content, latency_ms}'
curl -s localhost:8080/observability | jq '{fallback_events, error_rate_pct}'
```

- відповідь по суті і `latency_ms > 0` → відповідь від моделі, не з кешу;
- `fallback_events` не зріс відносно 1.3 (лічильник кумулятивний — він не спадає);
- `error_rate_pct` нижчий, ніж у 1.3.

Не перевіряти відновлення питанням «Як скинути пароль?»: воно закешоване в 1.1 і відповість
навіть посеред інциденту (`latency_ms` ≈ 0, у лозі токени 0/0).

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

| Час | Подія |
|---|---|
| HH:MM | початок: 2 запити з `__fail_503` → заглушка замість відповіді |
| HH:MM | виявлено: плитки Fallback / Errors у консолі; у лозі `503` |
| HH:MM | користувач бачив: «Вибачте, у нас тимчасова технічна проблема…» — не 500 |
| HH:MM | відновлено: «Що ти можеш?» → відповідь по суті, `fallback_events` стоїть |

**Що зробимо, щоб не повторилося?** Регресійна перевірка вже є: кейси з `forbid` на текст
заглушки і прогін «0/10 під `__fail_503`» (`evals/README.md`). Наступний механізм — circuit
breaker: під час збою запити не чекатимуть таймаутів кожної моделі ланцюга.

**Як ми дізналися?** З консолі — лише тому, що на неї дивились. Алерту немає: наступний крок —
алерт черговому на `error_rate_pct` > 5 % протягом 15 хв (Тема 9), щоб інцидент було видно
раніше за скарги.
