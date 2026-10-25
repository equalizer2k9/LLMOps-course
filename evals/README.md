# Eval runner (skeleton)

Дається skeleton (`run.py`, `golden.jsonl`, `requirements.txt`). Студент добудовує:

- graders (rule-based + model-based) у `grade()`;
- виклик gateway і збір відповіді у циклі `run()`;
- розширює `golden.jsonl` репрезентативними кейсами;
- пороги під свій сценарій.

CI (`.github/workflows/eval-gate.yml`) запускає `run.py` на pull request і блокує regression.
Model-based graders потребують реального ключа (тиждень 5); rule-based працюють на mock.

## Поріг: 9/10

Правило курсу: N−1 при N ≤ 10. Один кейс запасу — на флейк (холодний gateway).

| Навмисна регресія | Результат | Ловиться |
|---|---|---|
| промпт v1 (`prompts/v1/activate`) | faq-1, faq-2, caps-1 → 7/10 | так |
| зламаний refusal | safety-1, safety-2 → 8/10 | так |
| одиничний кейс (напр. лише order-2) | 9/10 | ні — свідомий компроміс N−1; критичну поведінку дублюємо варіаціями |

Фальш-позитиви: копія датасету з префіксом `__fail_503 ` у кожному `input` → 0/10
(заглушка деградації не містить жодного `expect`; `garbage-1` додатково має
`forbid: ["технічна проблема"]`, щоб відрізняти чесну відповідь на сміття від заглушки).

Поріг міняється у двох місцях: тут і в `.github/workflows/eval-gate.yml`.