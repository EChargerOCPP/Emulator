# YKC 1.6 Station Simulator

Симулятор зарядной станции YKC (云快充) v1.6, повторяет Mindian MDDC-120KW. Python 3.10+, без зависимостей.

- `ykc_station_sim.py` — симулятор (TCP к платформе + веб-панель)
- `ykc_sim_ui.html` — веб-панель
- `ykc_decode.py` — разбор кадров YKC

## Запуск

```bash
python ykc_station_sim.py                                   # 127.0.0.1:8767, станция 999
python ykc_station_sim.py --host platform.example.com --id 32010200000001 --power 120
```

Веб-панель: http://127.0.0.1:8790 (`--ui-port 0` — без панели, `--no-browser` — не открывать браузер).

Вход в панель: задайте переменные окружения `YKC_UI_USER` и `YKC_UI_PASSWORD` — панель покажет форму входа (сессия 30 дней, cookie). Без них панель открыта.

Параметры: `--guns`, `--power`, `--voltage`, `--capacity`, `--soc`, `--vin`, `--hb`, `--rt`, `--lock` и др. — `python ykc_station_sim.py -h`.

## Команды (консоль или панель; N — номер пистолета)

| Команда | Действие |
|---|---|
| `plug N` / `unplug N` | вставить / вынуть пистолет |
| `card N D14B0A54` | старт с карты (0x31) |
| `vin N [VIN]` | старт по VIN (0x31, способ 0x03) |
| `pcard 1,2 D14B0A54` | параллельная зарядка с карты (0xA1) |
| `stop N` | остановка на станции (0x45) |
| `bmserr N` | ошибка BMS 0x1B и остановка (0x5A) |
| `door open\|close` | датчик двери (неисправность 0x1000) |
| `fault N 0x0001` | биты неисправности (0 — сброс) |
| `lock N up\|down` | замок парковки (с `--lock`) |
| `set power\|voltage\|capacity\|soc\|meter X` | изменить параметр |
| `gunstatus N auto\|0-3` | статус пистолета в 0x13: 0 офлайн, 1 неисправность, 2 свободен, 3 зарядка; `auto` — по состоянию |
| `rt N` | отправить 0x13 сейчас |
| `connect HOST PORT ID [GUNS]` | подключиться к другой платформе / другим номером станции (в панели — «Настройки») |
| `status`, `reconnect`, `quit` | |

Зарядка: 0x15 → 0x17 → (0x13 + 0x23 + 0x25 каждые `--rt` с) → [0x1D/0x21] → 0x19 → 0x3B (повтор до 0x40).

## Linux-сервер

```bash
tmux new -s ykc
python3 ykc_station_sim.py --host 127.0.0.1 --port 8767 --no-browser
```
