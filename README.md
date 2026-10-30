# Starter repository

Те, що студент клонує на першому уроці й дороблює до capstone.

**➡ Повний гайд для студента (вимоги, запуск, кожна папка, troubleshooting): [GETTING_STARTED.md](GETTING_STARTED.md).**
**➡ Домашні завдання — окремий файл на кожен тиждень: [homework/](homework/README.md).**

## Що дається готовим, що добудовує студент

| Компонент | Дається готовим | Студент добудовує |
|---|---|---|
| UI (Angular) | готові в'юхи: Chat + Console/Observability | не змінює |
| App service (.NET) | skeleton | control plane: routing, fallback, cost, policies |
| Observability API (.NET) | skeleton | агрегати з Postgres (traces, p95, cost, error-taxonomy) у Console-в'юху |
| Gateway (LiteLLM) | базовий конфіг | моделі/провайдери, порядок fallback |
| Mock provider | готовий | використовує для тестів і failure-сценаріїв |
| Postgres | базова схема | розширює під logs / cost / prompt records |
| Eval runner (Python) | skeleton | dataset, graders, пороги |
| CI (GitHub Actions) | шаблон `.github/workflows/eval-gate.yml` | eval gate |
| Redis / дашборди | опційний шаблон | advanced extension |

## Запуск (мінімальний стек)

```sh
docker compose up --build
```

Піднімаються: UI (`:4200`), сервіс (`:8080`), LiteLLM (`:4000`), Postgres, mock provider.
Для mock реальні ключі не потрібні. Redis і дашборди — опційні: `docker compose --profile advanced up`.
Для оцінки якості (model-based evals): `cp gateway/.env.example gateway/.env` і заповнити ключі.

## SLO (W5)

SLI — частка звернень зі змістовною відповіддю: `status = '200'` у `requests`.
Заглушка деградації пишеться як 503/429, чесна відповідь на сміття — як 502, тож обидві
рахуються невдачею, хоча клієнт отримав HTTP 200.

SLO — **99% за тиждень**. При 10 000 звернень/тиждень бюджет помилок — **100 невдач**.
Бюджет спалено до кінця тижня → заморозка релізів промптів/моделей, лише стабільність.

```sql
SELECT round(100.0 * count(*) FILTER (WHERE status = '200') / count(*), 2) AS sli_pct,
       count(*) FILTER (WHERE status <> '200') AS failures
FROM requests WHERE created_at > now() - interval '7 days';
```
## Інциденти

Ранбук: [INCIDENT_RUNBOOK.md](INCIDENT_RUNBOOK.md) — провайдер недоступний, погіршення якості,
зростання витрат; rollback-критерії; хронологія навчального інциденту.

## Quality gate (W6)

На кожен PR `.github/workflows/eval-gate.yml` піднімає стек без UI і проганяє golden dataset.
`main` захищений: required status check **`eval`**, merge лише через PR, правило діє й на адмінів.
Поріг **9/10** (N−1 при N ≤ 10, правило hw5) — обґрунтування й навмисні регресії:
[evals/README.md](evals/README.md). Локально той самий прогін:

```bash
SERVICE_URL=http://localhost:8080 python evals/run.py --dataset evals/golden.jsonl --threshold 9
```

## 30-day rollout

Пілот в агенції: AI-асистент підтримки для WooCommerce-магазину клієнта — WordPress-плагін
з REST-ендпоінтом `/wp-json/support-ai/v1/chat`, виклик LLM-провайдера з PHP, дані — у MySQL
сайту, деплой через Coolify. Асистент відповідає про доставку й повернення, статус замовлення
бере з WooCommerce, повернення коштів — лише після підтвердження менеджера в адмінці.
Один складник на тиждень, кожен спирається на попередній.

1. **Тиждень 1 — уніфікований лог і вартість:** кожен виклик LLM пише рядок у таблицю `wp_ai_requests` (model, prompt/completion tokens, latency, status, cost_usd, prompt_version); готово, коли «скільки коштував асистент учора» — один SQL-запит.
2. **Тиждень 2 — реєстр промптів:** системний промпт переїжджає з коду плагіна в таблицю `wp_ai_prompts` з версіями й рівно одним активним, відкат — `wp support-ai prompt activate <версія>`; готово, коли відкат займає секунди і фіксується в лозі.
3. **Тиждень 3 — evals:** golden set на 10 кейсів із реальних листів у підтримку магазину (доставка, повернення, «де моє замовлення», плюс негативні: prompt injection, сміття, збій провайдера), прогін Python-runner'ом курсу проти REST-ендпоінта з exit code; готово, коли навмисно зламаний промпт стабільно дає exit 1.
4. **Тиждень 4 — CI-гейт:** на кожен PR GitHub Actions піднімає WordPress + MySQL у Docker з плагіном і проганяє evals, required check на `main`, Coolify деплоїть лише з `main`; готово, коли червоний прогін блокує merge і лишається в історії Actions.
