# YKC 1.6 Emulator

Эмулятор зарядной станции по протоколу YKC 1.6 для проверки шлюза EChargerOCPP.

Протокол (`Protocol/`) — копия `YKC.Gateway/Protocol` из репозитория EChargerBack.

## Запуск

```bash
dotnet run -- --host 127.0.0.1 --port 8767 --pile 32010200000001 --guns 2 --type dc --vin LSVTEST0000000001 --soc 30
```

Команды в консоли: `plug N`, `unplug N`, `stop N`, `bmsabort N` (0x1D), `abort N` (0x21), `bmserr N` (0x1B), `quit`.
Удалённый старт/стоп — из админки/приложения.

## Кадры

Станция → платформа: 0x01, 0x03, 0x05, 0x09, 0x13, 0x15, 0x17, 0x19, 0x1B, 0x1D, 0x21, 0x23, 0x25, 0x33, 0x35, 0x3B, 0x51, 0x55, 0x91.

Зарядка (DC): 0x33 → 0x13 → 0x15 → 0x17 → (0x13 + 0x23 + 0x25 каждый тик) → [0x1D/0x21] → 0x19 → 0x13 → 0x3B.

## Linux

```bash
dotnet publish -c Release -r linux-x64 --self-contained false -o publish
dotnet publish/YKC.Emulator.dll --host 127.0.0.1 --port 8767
```

Консоль интерактивная — запускать в `tmux`/`screen`.
