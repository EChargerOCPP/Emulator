#!/usr/bin/env python3
"""Симулятор зарядной станции YKC (云快充) v1.6 — повторяет Mindian MDDC-120KW.

Подключается к платформе по TCP и ведёт себя как настоящая станция (стенд 2026-10-21):
вход 0x01 каждые 5 с до ответа (через 60 с без ответа — переподключение), проверка
тарифа 0x05 (код 0002), 0x13 + 0x15 (старые BMS-данные) по каждому пистолету,
пульс 0x03 по каждому пистолету. Отвечает на все команды платформы из протокола
v1.6. Во время зарядки шлёт 0x15, 0x17, затем 0x13 + 0x23 + 0x25 каждые 2 с (--rt),
по окончании 0x19 и запись о транзакции 0x3B (повтор каждые 30 с до 0x40, не
больше 3 раз). Неисправность во время зарядки — 0x21, SOC 100% — 0x1D.

Веб-панель: http://127.0.0.1:8790 (открывается сама).

    python ykc_station_sim.py                                  # 127.0.0.1:8767, станция 999
    python ykc_station_sim.py --host platform.example.com --id 32010200000001 --power 120

Команды (в консоли или в панели; N — номер пистолета):
    plug N / unplug N      вставить / вынуть пистолет
    card N D14B0A54        старт с карты (0x31; офлайн — по списку офлайн-карт)
    vin N [VIN]            старт по VIN (0x31, способ 0x03)
    pcard 1,2 D14B0A54     параллельная зарядка с карты (0xA1 на каждый пистолет)
    stop N                 остановить зарядку на станции (причина 0x45)
    bmserr N               ошибка BMS: 0x1B и остановка (причина 0x5A)
    door open|close        датчик двери (неисправность 0x1000 на всех пистолетах)
    fault N 0x0001         задать биты неисправности (0 — сбросить)
    lock N up|down         замок парковки (только с --lock)
    set power|voltage|capacity|soc|meter ЗНАЧЕНИЕ
    rt N                   отправить 0x13 сейчас
    status                 состояние пистолетов
    reconnect              разорвать соединение и подключиться заново
    quit                   выход
"""
import argparse
import asyncio
import collections
import datetime
import json
import os
import sys
import threading
import time
import webbrowser

from ykc_decode import FRAME_NAMES, GUN_STATUS, STOP_REASONS, decode, fault_names

DOOR_OPEN = 0x1000          # Bit13 «дверь открыта» (проверено на Mindian)
STOP_APP, STOP_SOC_FULL, STOP_MANUAL = 0x40, 0x41, 0x45
STOP_FAULT, STOP_UNPLUGGED, STOP_BRM_TIMEOUT = 0x4A, 0x4B, 0x5A
TRADE_APP, TRADE_CARD, TRADE_OFFLINE_CARD, TRADE_VIN = 0x01, 0x02, 0x04, 0x05
HEARTBEAT_TYPES = (0x03, 0x04)

HERE = os.path.dirname(os.path.abspath(__file__))
LOG_PATH = os.path.join(HERE, "ykc_sim.log")
UI_PATH = os.path.join(HERE, "ykc_sim_ui.html")
_log_lock = threading.Lock()


class Bus:
    """События для веб-панели: кадры, сообщения журнала, снимки состояния."""

    def __init__(self):
        self.subs: set[asyncio.Queue] = set()
        self.history = collections.deque(maxlen=1000)

    def publish(self, ev: dict) -> None:
        if ev["kind"] != "state":
            self.history.append(ev)
        for q in list(self.subs):
            if q.qsize() < 2000:
                q.put_nowait(ev)


BUS = Bus()


def now_str() -> str:
    return f"{datetime.datetime.now():%Y-%m-%d %H:%M:%S}"


def log(msg: str, publish: bool = True) -> None:
    line = f"{now_str()} {msg}"
    with _log_lock:
        print(line, flush=True)
        with open(LOG_PATH, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    if publish:
        BUS.publish({"kind": "log", "t": line[11:19], "msg": msg.strip()})


def crc16_modbus(data: bytes) -> int:
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc


def u(value: float, size: int) -> bytes:
    return max(0, int(round(value))).to_bytes(size, "little")


def gun_bcd(n: int) -> bytes:
    return bytes.fromhex(f"{n:02d}")


def cp56(t: datetime.datetime) -> bytes:
    ms = t.second * 1000 + t.microsecond // 1000
    return ms.to_bytes(2, "little") + bytes([
        t.minute, t.hour, t.day | (t.isoweekday() << 5), t.month, t.year - 2000])


def bcd_now() -> bytes:
    return bytes.fromhex(f"{datetime.datetime.now():%y%m%d%H%M%S}")


class Tariff:
    """Тарифная модель 0x0A/0x58: 4 периода (尖峰平谷), 48 получасовых слотов."""

    def __init__(self, model: bytes = b"\x00\x02"):
        self.model = model
        self.prices = [100000, 40000] * 4      # электричество/услуга, 5 знаков: 1.00 + 0.40
        self.loss = 0
        self.slots = [2] * 48                   # весь день — «平»

    def load(self, body: bytes) -> None:
        self.model = body[7:9]
        self.prices = [int.from_bytes(body[9 + i * 4:13 + i * 4], "little") for i in range(8)]
        self.loss = body[41]
        self.slots = list(body[42:90])

    def period_now(self) -> int:
        t = datetime.datetime.now()
        return min(self.slots[(t.hour * 60 + t.minute) // 30], 3)

    def unit_price(self, period: int) -> float:   # за кВт·ч, электричество + услуга
        return (self.prices[period * 2] + self.prices[period * 2 + 1]) / 1e5

    def describe(self) -> str:
        names = "尖峰平谷"
        return ", ".join(f"{names[p]} {self.prices[p * 2] / 1e5:.5f}+{self.prices[p * 2 + 1] / 1e5:.5f}"
                         for p in range(4)) + f"; модель {self.model.hex()}"


class Session:
    def __init__(self, trans: bytes, trade: int, card: bytes, vin: str, soc: float, meter: float):
        self.trans = trans
        self.trade = trade
        self.card = card
        self.vin = vin
        self.start = datetime.datetime.now()
        self.soc = soc
        self.meter_start = meter
        self.kwh = [0.0] * 4
        self.amount = [0.0] * 4
        self.last_rt = 0.0

    @property
    def total_kwh(self) -> float:
        return sum(self.kwh)

    @property
    def total_amount(self) -> float:
        return sum(self.amount)

    @property
    def minutes(self) -> int:
        return int((datetime.datetime.now() - self.start).total_seconds() // 60)


class Gun:
    def __init__(self, no: int):
        self.no = no
        self.plugged = True
        self.fault = 0
        self.session: Session | None = None
        # удалённый старт без пистолета: (дедлайн, транзакция, карта, хвост ответа 0xA3 или None)
        self.pending_start: tuple[float, bytes, bytes, bytes | None] | None = None
        self.card_wait: bytes | None = None     # карта, по которой ждём 0x32/0xA2
        self.lock = 0xFF                        # замок парковки опущен
        self.last_idle_rt = 0.0


class Station:
    def __init__(self, a: argparse.Namespace):
        self.a = a
        self.pile = bytes.fromhex(a.id.zfill(14))
        self.guns = [Gun(i + 1) for i in range(a.guns)]
        self.tariff = Tariff()
        self.door_open = False
        self.disabled = False
        self.max_power_pct = 100
        self.meter = a.meter
        self.seq = 0
        self.writer: asyncio.StreamWriter | None = None
        self.logged_in = False
        self.last_hb_ack = 0.0
        self.pending_records: dict[bytes, list] = {}   # транзакция -> [тело 0x3B, попыток, время]
        self.offline_cards: dict[bytes, bytes] = {}    # физический номер -> логический
        self.parallel: dict[bytes, dict] = {}          # номер параллели -> {пистолет: (ok, транзакция) | None}
        self.trans_counter = 0
        self.conn_tasks: list[asyncio.Task] = []
        self.quit = False

    # ---------- кадры ----------

    def publish_frame(self, direction: str, ftype: int, frame: bytes, body: bytes) -> None:
        summary, fields = decode(ftype, body)
        BUS.publish({"kind": "frame", "t": now_str()[11:], "dir": direction, "type": ftype,
                     "name": FRAME_NAMES.get(ftype, "?"), "hex": frame.hex(" "), "summary": summary,
                     "fields": fields, "hb": ftype in HEARTBEAT_TYPES})

    def send(self, ftype: int, body: bytes, seq: int | None = None) -> None:
        if self.writer is None or (not self.logged_in and ftype != 0x01):
            return
        if seq is None:
            seq, self.seq = self.seq, (self.seq + 1) & 0xFFFF
        payload = seq.to_bytes(2, "little") + b"\x00" + bytes([ftype]) + body
        frame = b"\x68" + bytes([len(payload)]) + payload + crc16_modbus(payload).to_bytes(2, "little")
        log(f"TX 0x{ftype:02X} «{FRAME_NAMES.get(ftype, '?')}»: {frame.hex(' ')}", publish=False)
        self.publish_frame("tx", ftype, frame, body)
        self.writer.write(frame)

    def gun(self, raw: int) -> Gun | None:
        n = int(f"{raw:02x}") if raw < 0xA0 else raw
        return self.guns[n - 1] if 1 <= n <= len(self.guns) else None

    def gun_fault(self, g: Gun) -> int:
        return g.fault | (DOOR_OPEN if self.door_open else 0)

    def gun_status(self, g: Gun) -> int:
        if g.session:
            return 0x03
        return 0x01 if self.gun_fault(g) or self.disabled else 0x02

    def power(self) -> float:
        return self.a.power * self.max_power_pct / 100

    def new_trans(self, g: Gun) -> bytes:
        self.trans_counter = (self.trans_counter + 1) % 10000
        return self.pile + gun_bcd(g.no) + bcd_now() + bytes.fromhex(f"{self.trans_counter:04d}")

    def send_heartbeat(self, g: Gun) -> None:
        self.send(0x03, self.pile + gun_bcd(g.no) + bytes([1 if self.gun_fault(g) else 0]))

    def send_realtime(self, g: Gun, seq: int | None = None) -> None:
        s = g.session
        if s:
            volts, amps = self.a.voltage, self.power() * 1000 / self.a.voltage
            remaining = max(0, (100 - s.soc) / 100 * self.a.capacity / max(self.power(), 1) * 60)
            body = (s.trans + self.pile + gun_bcd(g.no) + bytes([0x03, 0x02, 0x01])
                    + u(volts * 10, 2) + u(amps * 10, 2) + bytes([25 + 50]) + bytes(8)
                    + bytes([int(s.soc), 30 + 50]) + u(s.minutes, 2) + u(remaining, 2)
                    + u(s.total_kwh * 1e4, 4) + u(s.total_kwh * 1e4, 4) + u(s.total_amount * 1e4, 4)
                    + u(self.gun_fault(g), 2))
            s.last_rt = time.monotonic()
        else:
            body = (bytes(16) + self.pile + gun_bcd(g.no) + bytes([self.gun_status(g), 0x02, int(g.plugged)])
                    + bytes(4) + bytes([0x32]) + bytes(8) + bytes(2) + bytes(4) + bytes(12)
                    + u(self.gun_fault(g), 2))
            g.last_idle_rt = time.monotonic()
        self.send(0x13, body, seq)

    def send_bms_handshake(self, g: Gun, trans: bytes, vin: str) -> None:
        cap_ah = self.a.capacity * 1000 / self.a.voltage
        body = (trans + self.pile + gun_bcd(g.no) + b"\x01\x01\x00" + b"\x03"
                + u(cap_ah * 10, 2) + u(self.a.voltage * 10, 2) + b"\xff" * 6 + b"\x00\x00"
                + b"\xff\x0c\x1f" + b"\xff\xff\x00" + b"\xff\x00"
                + vin.encode("ascii")[:17].ljust(17, b"\xff") + b"\xff" * 8)
        self.send(0x15, body)

    def send_bms_params(self, g: Gun, s: Session) -> None:
        body = (s.trans + self.pile + gun_bcd(g.no) + u(365, 2) + u((400 - 250) * 10, 2)
                + u(self.a.capacity * 10, 2) + u(self.a.voltage * 1.1 * 10, 2) + bytes([55 + 50])
                + u(s.soc * 10, 2) + u(self.a.voltage * 10, 2) + u(7500, 2) + u(2000, 2)
                + u((400 - 250) * 10, 2) + u(4000, 2))
        self.send(0x17, body)

    def send_charging_metrics(self, g: Gun, s: Session) -> None:
        volts, amps = self.a.voltage, self.power() * 1000 / self.a.voltage
        remaining = max(0, (100 - s.soc) / 100 * self.a.capacity / max(self.power(), 1) * 60)
        body = (s.trans + self.pile + gun_bcd(g.no) + u(volts * 10, 2) + u((400 - amps) * 10, 2)
                + b"\x02" + u(volts * 10, 2) + u((400 - amps) * 10, 2) + u(340 | (1 << 12), 2)
                + bytes([int(s.soc)]) + u(remaining, 2) + u(volts * 10, 2) + u((400 - amps) * 10, 2)
                + u(s.minutes, 2))
        self.send(0x23, body)
        # 0x25: ячейка №17, макс. 31 °C (точка 3), мин. 27 °C (точка 8), флаги: всё в норме
        self.send(0x25, s.trans + self.pile + gun_bcd(g.no) + bytes([17, 31 + 50, 3, 27 + 50, 8]) + bytes(2))

    def send_lock(self, g: Gun) -> None:
        self.send(0x61, self.pile + bytes([g.no, g.lock, 0xFF if g.plugged else 0x00, 100, 0x00]) + bytes(4))

    # ---------- зарядка ----------

    def start_session(self, g: Gun, trans: bytes, trade: int, card: bytes = bytes(8), vin: str = "") -> None:
        vin = vin or self.a.vin
        g.session = Session(trans, trade, card, vin, self.a.soc, self.meter)
        log(f"   пистолет {g.no}: зарядка началась, транзакция {trans.hex()}")
        self.send_bms_handshake(g, trans, vin)
        self.send_bms_params(g, g.session)
        self.send_realtime(g)
        self.send_charging_metrics(g, g.session)

    def stop_session(self, g: Gun, reason: int) -> None:
        s = g.session
        if not s:
            return
        g.session = None
        end = datetime.datetime.now()
        log(f"   пистолет {g.no}: зарядка окончена (0x{reason:02X} {STOP_REASONS.get(reason, '')}), "
            f"{s.total_kwh:.4f} кВт·ч, {s.total_amount:.2f}, {s.minutes} мин")
        if reason == STOP_SOC_FULL:
            self.send(0x1D, s.trans + self.pile + gun_bcd(g.no) + b"\x01" + bytes(3))   # BMS: достигнут SOC
        elif reason == STOP_FAULT:
            self.send(0x21, s.trans + self.pile + gun_bcd(g.no) + b"\x10" + u(0x0400, 2) + b"\x00")  # аварийно, «другое»
        self.send(0x19, s.trans + self.pile + gun_bcd(g.no) + bytes([int(s.soc)]) + u(320, 2) + u(345, 2)
                  + bytes([28 + 50, 33 + 50]) + u(s.minutes, 2) + u(s.total_kwh * 10, 2) + u(1, 4))
        self.send_realtime(g)
        periods = b"".join(u(self.tariff.unit_price(p) * 1e5, 4) + u(s.kwh[p] * 1e4, 4) * 2
                           + u(s.amount[p] * 1e4, 4) for p in range(4))
        vin = s.vin.encode("ascii")[:17].ljust(17, b"\x00") if s.trade == TRADE_VIN else bytes(17)
        body = (s.trans + self.pile + gun_bcd(g.no) + cp56(s.start) + cp56(end) + periods
                + u(s.meter_start * 1e4, 5) + u(self.meter * 1e4, 5) + u(s.total_kwh * 1e4, 4) * 2
                + u(s.total_amount * 1e4, 4) + vin + bytes([s.trade]) + cp56(end) + bytes([reason]) + s.card)
        self.pending_records[s.trans] = [body, 0, 0.0]
        self.flush_records()

    def flush_records(self) -> None:
        if not self.logged_in:
            return
        now = time.monotonic()
        for trans, rec in list(self.pending_records.items()):
            if rec[1] and now - rec[2] < 30:
                continue
            if rec[1] > 3:
                log(f"   запись о транзакции {trans.hex()} не подтверждена после 3 повторов, удаляю")
                del self.pending_records[trans]
                continue
            rec[1] += 1
            rec[2] = now
            self.send(0x3B, rec[0])

    def accept_start(self, g: Gun, seq: int | None, trans: bytes, card: bytes, reply_type: int, extra: bytes) -> None:
        self.send(reply_type, trans + self.pile + gun_bcd(g.no) + b"\x01\x00" + extra, seq)
        self.start_session(g, trans, TRADE_APP, card)

    async def tick(self) -> None:
        """Раз в секунду: учёт энергии, периодические 0x13/0x23/0x25, повтор 0x3B, ожидание пистолета."""
        while not self.quit:
            await asyncio.sleep(1)
            now = time.monotonic()
            for g in self.guns:
                if g.pending_start and g.plugged:
                    _, trans, card, extra = g.pending_start
                    g.pending_start = None
                    self.accept_start(g, None, trans, card, 0xA3 if extra else 0x33, extra or b"")
                elif g.pending_start and now > g.pending_start[0]:
                    log(f"   пистолет {g.no}: за 60 с пистолет не вставили, старт отменён")
                    g.pending_start = None
                s = g.session
                if s and self.gun_fault(g):
                    self.stop_session(g, STOP_FAULT)
                elif s:
                    kwh = self.power() / 3600
                    period = self.tariff.period_now()
                    s.kwh[period] += kwh
                    s.amount[period] += kwh * self.tariff.unit_price(period)
                    s.soc = min(100.0, s.soc + kwh / self.a.capacity * 100)
                    self.meter += kwh
                    if s.soc >= 100:
                        self.stop_session(g, STOP_SOC_FULL)
                    elif now - s.last_rt >= self.a.rt:
                        self.send_realtime(g)
                        self.send_charging_metrics(g, s)
                elif self.logged_in and now - g.last_idle_rt >= 300:
                    self.send_realtime(g)
            self.flush_records()
            BUS.publish(self.snapshot())

    # ---------- соединение ----------

    async def run(self) -> None:
        tick = asyncio.create_task(self.tick())
        while not self.quit:
            try:
                reader, self.writer = await asyncio.wait_for(
                    asyncio.open_connection(self.a.host, self.a.port), 10)
            except (OSError, asyncio.TimeoutError) as e:
                log(f"нет соединения с {self.a.host}:{self.a.port}: {e!r}, повтор через 5 с")
                await asyncio.sleep(5)
                continue
            log(f"CONNECT {self.a.host}:{self.a.port}")
            self.seq, self.logged_in = 0, False
            self.conn_tasks = [asyncio.create_task(self.login_loop())]
            try:
                await self.read_loop(reader)
            except (OSError, asyncio.IncompleteReadError) as e:
                log(f"ERR {e!r}")
            finally:
                for t in self.conn_tasks:
                    t.cancel()
                self.writer.close()
                self.writer, self.logged_in = None, False
                log("CLOSE")
            if not self.quit:
                await asyncio.sleep(5)
        tick.cancel()

    def drop(self, reason: str) -> None:
        log(f"разрываю соединение: {reason}")
        if self.writer:
            self.writer.close()

    async def login_loop(self) -> None:
        body = (self.pile + b"\x00" + bytes([len(self.guns), 0x10]) + self.a.firmware.encode("ascii")[:8].ljust(8, b"\x00")
                + b"\x01" + bytes(10) + b"\x00")
        started = time.monotonic()
        while not self.logged_in:
            if time.monotonic() - started > 60:
                self.drop("нет ответа на вход 60 с")
                return
            self.send(0x01, body)
            await asyncio.sleep(5)

    async def heartbeat_loop(self) -> None:
        self.last_hb_ack = time.monotonic()
        while True:
            for g in self.guns:
                self.send_heartbeat(g)
                await asyncio.sleep(self.a.hb / len(self.guns))
            if time.monotonic() - self.last_hb_ack > self.a.hb * 3:
                self.drop("3 пульса без ответа")
                return

    async def after_login(self) -> None:
        self.flush_records()
        self.send(0x05, self.pile + self.tariff.model)
        await asyncio.sleep(0.5)
        for g in self.guns:
            self.send_realtime(g)
            if self.a.startup_bms and not g.session:
                self.send_bms_handshake(g, b"2018100503210501", self.a.vin)
            if self.a.lock:
                self.send_lock(g)
        self.conn_tasks.append(asyncio.create_task(self.heartbeat_loop()))

    async def read_loop(self, reader: asyncio.StreamReader) -> None:
        buf = b""
        while True:
            data = await reader.read(4096)
            if not data:
                return
            buf += data
            while buf:
                if buf[0] != 0x68:
                    i = buf.find(b"\x68")
                    log(f"   мусор {buf[:i if i > 0 else len(buf)].hex(' ')}")
                    buf = buf[i:] if i > 0 else b""
                    continue
                if len(buf) < 2 or len(buf) < buf[1] + 4:
                    break
                n = buf[1]
                frame, buf = buf[:n + 4], buf[n + 4:]
                payload = frame[2:2 + n]
                if crc16_modbus(payload).to_bytes(2, "little") != frame[2 + n:]:
                    log(f"RX плохой CRC: {frame.hex(' ')}")
                    continue
                seq, ftype, body = int.from_bytes(payload[0:2], "little"), payload[3], payload[4:]
                log(f"RX 0x{ftype:02X} «{FRAME_NAMES.get(ftype, '?')}»: {frame.hex(' ')}", publish=False)
                self.publish_frame("rx", ftype, frame, body)
                if payload[2]:
                    log("   кадр зашифрован, пропускаю")
                    continue
                try:
                    self.handle(ftype, seq, body)
                except (IndexError, ValueError) as e:
                    log(f"   не удалось разобрать: {e!r}")

    # ---------- команды платформы ----------

    def handle(self, ftype: int, seq: int, body: bytes) -> None:
        if ftype not in (0x32, 0x34, 0x40, 0xA2, 0xA4) and body[0:7] != self.pile:
            log(f"   чужой номер станции {body[0:7].hex()} (мой {self.pile.hex()})")
        if ftype == 0x02:
            if body[7] == 0:
                log("   вход принят")
                self.logged_in = True
                self.conn_tasks.append(asyncio.create_task(self.after_login()))
            else:
                log("   вход отклонён, переподключусь")
                self.drop("вход отклонён")
        elif ftype == 0x04:
            self.last_hb_ack = time.monotonic()
        elif ftype == 0x06:
            if body[9] == 0:
                log("   тариф совпадает")
            else:
                log("   тариф не совпадает, запрашиваю")
                self.send(0x09, self.pile)
        elif ftype == 0x0A:
            self.tariff.load(body)
            log(f"   тариф: {self.tariff.describe()}")
        elif ftype == 0x58:
            self.tariff.load(body)
            log(f"   новый тариф: {self.tariff.describe()}")
            self.send(0x57, self.pile + b"\x01", seq)
        elif ftype == 0x12:
            g = self.gun(body[7])
            if g:
                self.send_realtime(g, seq)
        elif ftype in (0x34, 0xA4):
            self.remote_start(ftype, seq, body)
        elif ftype == 0x36:
            g = self.gun(body[7])
            if g and g.session:
                self.send(0x35, self.pile + gun_bcd(g.no) + b"\x01\x00", seq)
                self.stop_session(g, STOP_APP)
            else:
                self.send(0x35, self.pile + body[7:8] + b"\x00" + (b"\x02" if g else b"\x03"), seq)
        elif ftype == 0x32:
            g = self.gun(body[23])
            if g and body[36] == 0x01:
                log(f"   карта принята, баланс {int.from_bytes(body[32:36], 'little') / 100:.2f}")
                card, g.card_wait = g.card_wait or bytes(8), None
                self.start_session(g, body[0:16], TRADE_CARD, card)
            else:
                log(f"   карта отклонена, причина 0x{body[37]:02X}")
                if g:
                    g.card_wait = None
        elif ftype == 0xA2:
            self.parallel_ack(body)
        elif ftype == 0x40:
            ok = body[16] == 0
            log(f"   транзакция {body[0:16].hex()} {'подтверждена' if ok else 'отклонена (неверная запись)'}")
            self.pending_records.pop(body[0:16], None)
        elif ftype == 0x42:
            log(f"   новый баланс {int.from_bytes(body[16:20], 'little') / 100:.2f}")
            self.send(0x41, self.pile + body[8:16] + b"\x00", seq)
        elif ftype == 0x44:
            n = body[7]
            for i in range(n):
                c = body[8 + i * 16:24 + i * 16]
                self.offline_cards[c[8:16]] = c[0:8]
            log(f"   офлайн-карт загружено: {n}, всего {len(self.offline_cards)}")
            self.send(0x43, self.pile + b"\x01\x00", seq)
        elif ftype == 0x46:
            out = b""
            for i in range(body[7]):
                card = body[8 + i * 8:16 + i * 8]
                found = self.offline_cards.pop(card, None) is not None
                out += card + (b"\x01\x00" if found else b"\x00\x01")
            self.send(0x45, self.pile + out, seq)
        elif ftype == 0x48:
            out = b""
            for i in range(body[7]):
                card = body[8 + i * 8:16 + i * 8]
                out += card + bytes([int(card in self.offline_cards)])
            self.send(0x47, self.pile + out, seq)
        elif ftype == 0x52:
            self.disabled = body[7] == 0x01
            self.max_power_pct = max(30, min(100, body[8])) if body[8] else 100
            log(f"   {'станция заблокирована' if self.disabled else 'станция разрешена'}, мощность {self.max_power_pct}%")
            self.send(0x51, self.pile + b"\x01", seq)
            for g in self.guns:
                self.send_realtime(g)
        elif ftype == 0x56:
            self.send(0x55, self.pile + body[7:14], seq)
        elif ftype == 0x62:
            g = self.gun(body[7]) if body[7] < 0x10 else None
            if not self.a.lock or not g:
                log("   замка парковки нет, без ответа")
                return
            g.lock = body[8]
            self.send(0x63, self.pile + bytes([g.no, 1]) + bytes(4), seq)
            self.send_lock(g)
        elif ftype == 0x92:
            self.send(0x91, self.pile + b"\x01", seq)
            if not any(g.session for g in self.guns) or body[7] == 0x01:
                for g in self.guns:
                    self.stop_session(g, STOP_MANUAL)
                asyncio.get_running_loop().call_later(1, self.drop, "перезагрузка по команде")
        elif ftype == 0x94:
            server = body[10:26].rstrip(b"\x00").decode("ascii", "replace")
            path = body[60:92].rstrip(b"\x00").decode("ascii", "replace")
            port = int.from_bytes(body[26:28], "little")
            log(f"   обновление прошивки: ftp://{server}:{port}/{path}, через 3 с отвечу «{self.a.fw_result}»")
            result = {"ok": 0x00, "model": 0x02, "timeout": 0x03}[self.a.fw_result]

            def finish() -> None:
                self.send(0x93, self.pile + bytes([result]), seq)
                if result == 0x00:
                    asyncio.get_running_loop().call_later(1, self.drop, "перезагрузка после обновления")
            asyncio.get_running_loop().call_later(3, finish)
        else:
            log("   неизвестная команда, без ответа")

    def remote_start(self, ftype: int, seq: int, body: bytes) -> None:
        trans, g = body[0:16], self.gun(body[23])
        balance = int.from_bytes(body[40:44], "little") / 100
        parallel = ftype == 0xA4
        extra = (b"\x00" if g and g.no == 1 else b"\x01") + body[44:50] if parallel else b""
        log(f"   {'параллельный ' if parallel else ''}удалённый старт: транзакция {trans.hex()}, "
            f"карта {body[32:40].hex()}, баланс {balance:.2f}")
        reply_type = 0xA3 if parallel else 0x33

        def reply(ok: bool, reason: int) -> None:
            self.send(reply_type, trans + self.pile + body[23:24] + bytes([int(ok), reason]) + extra, seq)

        if body[16:23] != self.pile or not g:
            reply(False, 0x01)
        elif g.session:
            reply(False, 0x02)
        elif self.gun_fault(g) or self.disabled:
            reply(False, 0x03)
        elif not g.plugged:
            reply(False, 0x05)
            g.pending_start = (time.monotonic() + 60, trans, body[32:40], extra or None)
            log(f"   пистолет {g.no} не вставлен: жду 60 с")
        else:
            self.accept_start(g, seq, trans, body[32:40], reply_type, extra)

    def parallel_ack(self, body: bytes) -> None:
        g, pseq = self.gun(body[23]), body[38:44]
        group = self.parallel.get(pseq)
        if not g or group is None or g.no not in group:
            log(f"   0xA2 для неизвестной параллели {pseq.hex()}")
            return
        group[g.no] = (body[36] == 0x01, body[0:16])
        if body[36] != 0x01:
            log(f"   пистолет {g.no}: отказ, причина 0x{body[37]:02X}")
        if all(v is not None for v in group.values()):
            del self.parallel[pseq]
            if all(ok for ok, _ in group.values()):
                for no, (_, trans) in group.items():
                    gg = self.guns[no - 1]
                    card, gg.card_wait = gg.card_wait or bytes(8), None
                    self.start_session(gg, trans, TRADE_CARD, card)
            else:
                log("   параллельная зарядка не запущена: не все пистолеты получили согласие")
                for no in group:
                    self.guns[no - 1].card_wait = None

    # ---------- консоль и панель ----------

    def command(self, line: str) -> None:
        p = line.split()
        if not p:
            return
        cmd, g = p[0].lower(), None
        if len(p) > 1 and p[1].isdigit():
            g = self.gun(int(p[1]))
        if cmd not in ("status", "help"):
            log(f"> {line}")
        try:
            if cmd in ("plug", "unplug") and g:
                g.plugged = cmd == "plug"
                if not g.plugged and g.session:
                    self.stop_session(g, STOP_UNPLUGGED)
                self.send_realtime(g)
                if self.a.lock:
                    self.send_lock(g)
            elif cmd == "card" and g and len(p) > 2:
                self.card_start(g, bytes.fromhex(p[2].zfill(16)))
            elif cmd == "vin" and g:
                vin = p[2] if len(p) > 2 else self.a.vin
                self.send(0x31, self.pile + gun_bcd(g.no) + b"\x03\x00" + bytes(8) + bytes(16)
                          + vin.encode("ascii")[:17].ljust(17, b"\x00")[::-1])
            elif cmd == "pcard" and len(p) > 2:
                guns = [self.gun(int(x)) for x in p[1].split(",")]
                if None in guns or len(guns) < 2:
                    raise ValueError("нужно 2+ существующих пистолета, например pcard 1,2 D14B0A54")
                card, pseq = bytes.fromhex(p[2].zfill(16)), bcd_now()
                self.parallel[pseq] = {gg.no: None for gg in guns}
                for i, gg in enumerate(guns):
                    gg.card_wait = card
                    self.send(0xA1, self.pile + gun_bcd(gg.no) + b"\x01\x00" + card + bytes(16) + bytes(17)
                              + bytes([0 if i == 0 else 1]) + pseq)
            elif cmd == "stop" and g:
                self.stop_session(g, STOP_MANUAL)
            elif cmd == "bmserr" and g:
                s = g.session
                trans = s.trans if s else bytes(16)
                self.send(0x1B, trans + self.pile + gun_bcd(g.no) + bytes(4) + b"\x01" + bytes(3))  # таймаут BRM
                self.stop_session(g, STOP_BRM_TIMEOUT)
            elif cmd == "door" and len(p) > 1:
                self.door_open = p[1] == "open"
                for gg in self.guns:
                    self.send_realtime(gg)
            elif cmd == "fault" and g and len(p) > 2:
                g.fault = int(p[2], 0)
                self.send_realtime(g)
            elif cmd == "lock" and g and len(p) > 2:
                g.lock = 0x55 if p[2] == "up" else 0xFF
                self.send_lock(g)
            elif cmd == "set" and len(p) > 2:
                key, value = p[1], float(p[2])
                if key == "meter":
                    self.meter = value
                elif key in ("power", "voltage", "capacity", "soc"):
                    setattr(self.a, key, value)
                else:
                    raise ValueError(f"неизвестный параметр {key}")
            elif cmd == "rt" and g:
                self.send_realtime(g)
            elif cmd == "status":
                log(f"связь: {'вход выполнен' if self.logged_in else 'нет'}, дверь {'открыта' if self.door_open else 'закрыта'}, "
                    f"счётчик {self.meter:.4f} кВт·ч, тариф: {self.tariff.describe()}")
                for gg in self.guns:
                    s = gg.session
                    log(f"  пистолет {gg.no}: статус 0x{self.gun_status(gg):02X}, вставлен={gg.plugged}, "
                        f"неисправность 0x{self.gun_fault(gg):04X}"
                        + (f", заряжает: {s.total_kwh:.3f} кВт·ч, {s.total_amount:.2f}, SOC {s.soc:.0f}%" if s else ""))
            elif cmd == "reconnect":
                self.drop("команда reconnect")
            elif cmd in ("quit", "exit"):
                if self.quit:
                    return
                self.quit = True
                self.drop("выход")
            else:
                print(__doc__.split("Команды (в консоли")[1])
        except ValueError as e:
            log(f"ошибка в команде: {e}")
        BUS.publish(self.snapshot())

    def card_start(self, g: Gun, card: bytes) -> None:
        if self.logged_in:
            g.card_wait = card
            self.send(0x31, self.pile + gun_bcd(g.no) + b"\x01\x00" + card + bytes(16) + bytes(17))
        elif card in self.offline_cards:
            log(f"   офлайн: карта {card.hex()} есть в списке, запускаю")
            self.start_session(g, self.new_trans(g), TRADE_OFFLINE_CARD, card)
        else:
            log(f"   офлайн: карты {card.hex()} нет в списке офлайн-карт")

    def snapshot(self) -> dict:
        guns = []
        for g in self.guns:
            s = g.session
            fault = self.gun_fault(g)
            guns.append({
                "no": g.no, "status": self.gun_status(g), "statusName": GUN_STATUS[self.gun_status(g)],
                "plugged": g.plugged, "fault": fault, "faultNames": fault_names(fault), "ownFault": g.fault,
                "lock": g.lock,
                "waiting": "удалённый старт: ждём пистолет" if g.pending_start
                else "ждём ответ на карту" if g.card_wait else None,
                "session": s and {
                    "trans": s.trans.hex(), "trade": s.trade, "kwh": s.total_kwh, "amount": s.total_amount,
                    "soc": s.soc, "minutes": s.minutes, "start": s.start.strftime("%H:%M:%S"),
                    "power": self.power(), "voltage": self.a.voltage, "current": self.power() * 1000 / self.a.voltage,
                },
            })
        return {
            "kind": "state", "host": self.a.host, "port": self.a.port, "pile": self.pile.hex(),
            "conn": "online" if self.logged_in else "login" if self.writer else "offline",
            "door": self.door_open, "disabled": self.disabled, "maxPowerPct": self.max_power_pct,
            "meter": self.meter, "lockEnabled": self.a.lock, "guns": guns,
            "tariff": {"model": self.tariff.model.hex(), "prices": [x / 1e5 for x in self.tariff.prices],
                       "slots": self.tariff.slots, "period": self.tariff.period_now()},
            "params": {"power": self.a.power, "voltage": self.a.voltage, "capacity": self.a.capacity, "soc": self.a.soc},
            "records": [{"trans": t.hex(), "tries": r[1]} for t, r in self.pending_records.items()],
            "offlineCards": [{"card": c.hex(), "logical": lc.hex()} for c, lc in self.offline_cards.items()],
        }


# ---------- веб-панель ----------

async def http_handler(station: Station, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
    try:
        head = (await reader.readuntil(b"\r\n\r\n")).decode("latin-1")
        method, path, _ = head.split("\r\n", 1)[0].split(" ", 2)
        headers = {k.lower(): v for k, _, v in (h.partition(": ") for h in head.split("\r\n")[1:]) if v}

        def respond(status: str, ctype: str, data: bytes) -> None:
            writer.write(f"HTTP/1.1 {status}\r\nContent-Type: {ctype}\r\nContent-Length: {len(data)}\r\n"
                         f"Cache-Control: no-store\r\nConnection: close\r\n\r\n".encode() + data)

        if method == "GET" and path in ("/", "/index.html"):
            with open(UI_PATH, "rb") as f:
                respond("200 OK", "text/html; charset=utf-8", f.read())
        elif method == "GET" and path == "/events":
            writer.write(b"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\n"
                         b"Cache-Control: no-store\r\nConnection: keep-alive\r\n\r\n")
            q: asyncio.Queue = asyncio.Queue()
            BUS.subs.add(q)
            try:
                for ev in [*BUS.history, station.snapshot()]:
                    writer.write(f"data: {json.dumps(ev, ensure_ascii=False)}\n\n".encode())
                await writer.drain()
                while True:
                    try:
                        ev = await asyncio.wait_for(q.get(), 15)
                        writer.write(f"data: {json.dumps(ev, ensure_ascii=False)}\n\n".encode())
                    except asyncio.TimeoutError:
                        writer.write(b": ping\n\n")
                    await writer.drain()
            finally:
                BUS.subs.discard(q)
        elif method == "POST" and path == "/cmd":
            data = await reader.readexactly(int(headers.get("content-length", "0")))
            station.command(json.loads(data or b"{}").get("cmd", ""))
            respond("200 OK", "application/json", b'{"ok":true}')
        else:
            respond("404 Not Found", "text/plain", b"not found")
        await writer.drain()
    except (OSError, asyncio.IncompleteReadError, asyncio.LimitOverrunError, ValueError):
        pass
    finally:
        writer.close()


def main() -> None:
    p = argparse.ArgumentParser(description="Симулятор станции YKC v1.6 (Mindian MDDC-120KW)")
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=8767)
    p.add_argument("--id", default="999", help="номер станции, до 14 цифр")
    p.add_argument("--guns", type=int, default=2)
    p.add_argument("--firmware", default="220422")
    p.add_argument("--power", type=float, default=60, help="мощность зарядки, кВт")
    p.add_argument("--voltage", type=float, default=400, help="напряжение зарядки, В")
    p.add_argument("--capacity", type=float, default=70, help="ёмкость батареи, кВт·ч")
    p.add_argument("--soc", type=float, default=30, help="SOC машины в начале, %%")
    p.add_argument("--meter", type=float, default=130863.219, help="показание счётчика, кВт·ч")
    p.add_argument("--vin", default="LNA0B5PDAE0100022")
    p.add_argument("--hb", type=float, default=10, help="период пульса на каждый пистолет, с")
    p.add_argument("--rt", type=float, default=2, help="период 0x13/0x23/0x25 во время зарядки, с")
    p.add_argument("--lock", action="store_true", help="есть замок парковки (0x61-0x63); у Mindian нет")
    p.add_argument("--fw-result", choices=["ok", "model", "timeout"], default="ok",
                   help="ответ на обновление прошивки 0x94")
    p.add_argument("--no-startup-bms", dest="startup_bms", action="store_false",
                   help="не слать старые 0x15 после входа (настоящая станция шлёт)")
    p.add_argument("--ui-port", type=int, default=8790, help="порт веб-панели, 0 — без панели")
    p.add_argument("--no-browser", action="store_true", help="не открывать браузер")
    a = p.parse_args()
    if sys.stdout.encoding.lower() != "utf-8":
        sys.stdout.reconfigure(encoding="utf-8")

    station = Station(a)
    loop = asyncio.new_event_loop()

    def stdin_reader() -> None:
        for line in sys.stdin:
            loop.call_soon_threadsafe(station.command, line.strip())
        if a.ui_port == 0:
            loop.call_soon_threadsafe(station.command, "quit")

    async def start() -> None:
        server = None
        if a.ui_port:
            server = await asyncio.start_server(lambda r, w: http_handler(station, r, w), "127.0.0.1", a.ui_port)
            url = f"http://127.0.0.1:{a.ui_port}"
            log(f"веб-панель: {url}")
            if not a.no_browser:
                webbrowser.open(url)
        try:
            await station.run()
        finally:
            if server:
                server.close()
            rest = [t for t in asyncio.all_tasks() if t is not asyncio.current_task()]
            for t in rest:
                t.cancel()
            await asyncio.gather(*rest, return_exceptions=True)

    threading.Thread(target=stdin_reader, daemon=True).start()
    log(f"станция {station.pile.hex()} -> {a.host}:{a.port}, пистолетов {a.guns}; лог: {LOG_PATH}")
    try:
        loop.run_until_complete(start())
    except KeyboardInterrupt:
        log("остановлено")


if __name__ == "__main__":
    main()
