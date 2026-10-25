using System.Collections.Concurrent;
using System.Net.Sockets;
using YKC.Gateway.Protocol;
using static YKC.Gateway.Protocol.YkcCodec;

namespace YKC.Emulator;

/// <summary>
/// Эмулятор станции YKC 1.6: вход, пульс, данные в реальном времени, удалённый старт и стоп,
/// кадры BMS (DC), итог зарядки. Состояние пистолетов переживает переподключение (<see cref="ConnectAsync"/> заново).
/// </summary>
public sealed class PileEmulator : IAsyncDisposable
{
    private const decimal OutVoltageV = 380m, OutCurrentA = 50m;

    private readonly byte[] _pile;
    private readonly Dictionary<int, Gun> _guns;
    private readonly YkcFrameReader _reader = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentQueue<YkcFrame> _received = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _loops;
    private ushort _seq;
    private TaskCompletionSource<bool> _loggedIn = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PileEmulator(string pileCode, int gunCount = 2, bool isAc = false, string firmware = "EMU-1.0")
    {
        PileCode = pileCode;
        _pile = StringToBcd(pileCode, 7);
        IsAc = isAc;
        Firmware = firmware;
        _guns = Enumerable.Range(1, gunCount).ToDictionary(n => n, n => new Gun(n));
    }

    public string PileCode { get; }
    public bool IsAc { get; }
    public string Firmware { get; }
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ChargingTick { get; init; } = TimeSpan.FromSeconds(15);
    public decimal EnergyPerTickKwh { get; init; } = 0.25m;
    public Action<string>? Log { get; init; }
    /// <summary>Батарея автомобиля для кадров BMS; на AC-станции BMS-кадры не шлются.</summary>
    public BmsProfile Bms { get; init; } = new();

    /// <summary>Ответ платформы на вход: true — принят.</summary>
    public Task<bool> LoggedIn => _loggedIn.Task;

    public int AckedRecords;
    public decimal? TariffPerKwh { get; private set; }
    public DateTime? LastTimeSync { get; private set; }

    public bool IsCharging(int gun) { lock (_guns) return _guns[gun].Charging; }
    public decimal EnergyKwh(int gun) { lock (_guns) return _guns[gun].EnergyKwh; }
    public int Soc(int gun) { lock (_guns) return _guns[gun].Soc; }

    public IReadOnlyCollection<YkcFrame> Received => _received.ToArray();

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        await DisconnectAsync();
        _loggedIn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(host, port, ct);
        _stream = _tcp.GetStream();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _seq = 0;

        var login = Concat(_pile, [IsAc ? (byte)1 : (byte)0, (byte)_guns.Count, 0x10],
            System.Text.Encoding.ASCII.GetBytes(Firmware.PadRight(8, '\0')[..8]), [0x01], new byte[10], [0x04]);
        await SendAsync(YkcType.Login, login);

        _loops = Task.WhenAll(ReadLoopAsync(_cts.Token), HeartbeatLoopAsync(_cts.Token), ChargingLoopAsync(_cts.Token));
    }

    public async Task DisconnectAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        _tcp?.Dispose();
        if (_loops is not null) await _loops.ContinueWith(_ => { });
        _cts.Dispose();
        _cts = null;
        _tcp = null;
        _stream = null;
    }

    // ---------- действия «пользователя на станции» ----------

    public async Task PlugAsync(int gun)
    {
        bool startPending;
        lock (_guns)
        {
            _guns[gun].Plugged = true;
            startPending = _guns[gun].PendingTrade is not null && _guns[gun].PendingUntil > DateTime.UtcNow;
        }
        if (startPending) await BeginChargingAsync(gun);
        else await SendRealtimeAsync(gun);
    }

    public async Task UnplugAsync(int gun)
    {
        lock (_guns) _guns[gun].Plugged = false;
        await SendRealtimeAsync(gun);
    }

    /// <summary>Остановка кнопкой на станции (причина 0x45 — ручная остановка).</summary>
    public Task LocalStopAsync(int gun) => FinishChargingAsync(gun, 0x45);

    /// <summary>Аварийная остановка со стороны BMS (0x1D: перегрев батареи), затем итог зарядки.</summary>
    public Task BmsAbortAsync(int gun, byte stopReason = 0x4A) =>
        FinishChargingAsync(gun, stopReason, (YkcType.BmsAbort, [0x00, 0x00, 0x01, 0x00]));

    /// <summary>Аварийная остановка со стороны станции (0x21: неисправность «другое»), затем итог зарядки.</summary>
    public Task ChargerAbortAsync(int gun, byte stopReason = 0x4A) =>
        FinishChargingAsync(gun, stopReason, (YkcType.ChargerAbort, [0x10, 0x00, 0x04, 0x00]));

    /// <summary>Ошибка связи с BMS (0x1B: таймаут BRM) и остановка с причиной 0x5A.</summary>
    public async Task BmsErrorAsync(int gun)
    {
        byte[] trade;
        lock (_guns) trade = _guns[gun].Trade ?? new byte[16];
        await SendAsync(YkcType.BmsError, Concat(trade, _pile, [IntToBcd(gun)], new byte[4], [0x01], new byte[3]));
        await FinishChargingAsync(gun, 0x5A);
    }

    // ---------- обмен ----------

    private async Task SendAsync(byte type, byte[] body, ushort? seq = null)
    {
        if (_stream is null) return;
        await _sendLock.WaitAsync();
        try
        {
            var frame = new YkcFrame(seq ?? _seq++, false, type, body);
            await _stream.WriteAsync(frame.Encode());
            Log?.Invoke($"→ {YkcType.Name(type)} {Convert.ToHexString(body)}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await _stream!.ReadAsync(buffer, ct);
                if (read == 0) break;
                foreach (var frame in _reader.Feed(buffer.AsSpan(0, read), p => Log?.Invoke("! " + p)))
                {
                    _received.Enqueue(frame);
                    Log?.Invoke($"← {YkcType.Name(frame.Type)} {Convert.ToHexString(frame.Body)}");
                    await HandleAsync(frame);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            _loggedIn.TrySetResult(false);
        }
    }

    private async Task HandleAsync(YkcFrame f)
    {
        var b = f.Body;
        switch (f.Type)
        {
            case YkcType.LoginReply:
                var ok = b[7] == 0;
                _loggedIn.TrySetResult(ok);
                if (ok)
                {
                    await SendAsync(YkcType.BillingCheck, Concat(_pile, [0x00, 0x00]));
                    foreach (var gun in _guns.Keys) await SendRealtimeAsync(gun);
                }
                break;
            case YkcType.BillingCheckReply when b[9] != 0:
                await SendAsync(YkcType.BillingRequest, _pile);
                break;
            case YkcType.BillingModel:
                TariffPerKwh = U32(b.AsSpan(9)) / 100000m;
                break;
            case YkcType.TimeSync:
                LastTimeSync = FromCp56(b.AsSpan(7, 7));
                await SendAsync(YkcType.TimeSyncReply, b, f.Seq);
                break;
            case YkcType.RealtimeRequest:
                await SendRealtimeAsync(BcdToInt(b[7]));
                break;
            case YkcType.RemoteStart:
                await OnRemoteStartAsync(f);
                break;
            case YkcType.RemoteStop:
                await OnRemoteStopAsync(f);
                break;
            case YkcType.TransactionRecordAck:
                Interlocked.Increment(ref AckedRecords);
                break;
            case YkcType.Reboot:
                await SendAsync(YkcType.RebootReply, Concat(_pile, [0x01]), f.Seq);
                break;
            case YkcType.WorkParams:
                await SendAsync(YkcType.WorkParamsReply, Concat(_pile, [0x01]), f.Seq);
                break;
        }
    }

    private async Task OnRemoteStartAsync(YkcFrame f)
    {
        var trade = f.Body.AsSpan(0, 16).ToArray();
        var gunNo = BcdToInt(f.Body[23]);
        byte reason;
        lock (_guns)
        {
            var gun = _guns[gunNo];
            reason = gun.Charging ? (byte)0x02 : !gun.Plugged ? (byte)0x05 : (byte)0x00;
            if (reason != 0x02)
            {
                gun.PendingTrade = trade;
                gun.PendingUntil = DateTime.UtcNow.AddSeconds(60);
            }
        }

        if (reason == 0x00)
        {
            await BeginChargingAsync(gunNo);
            return;
        }
        await SendAsync(YkcType.RemoteStartReply, Concat(trade, _pile, [IntToBcd(gunNo), 0x00, reason]));
    }

    private async Task BeginChargingAsync(int gunNo)
    {
        byte[] trade;
        lock (_guns)
        {
            var gun = _guns[gunNo];
            trade = gun.PendingTrade!;
            gun.PendingTrade = null;
            gun.Trade = trade;
            gun.Charging = true;
            gun.EnergyKwh = 0;
            gun.Soc = Bms.StartSoc;
            gun.StartLocal = DateTime.UtcNow.AddHours(5);
        }
        await SendAsync(YkcType.RemoteStartReply, Concat(trade, _pile, [IntToBcd(gunNo), 0x01, 0x00]));
        await SendRealtimeAsync(gunNo);
        await SendBmsStartAsync(gunNo);
        await SendBmsProgressAsync(gunNo);
    }

    private async Task OnRemoteStopAsync(YkcFrame f)
    {
        var gunNo = BcdToInt(f.Body[7]);
        bool charging;
        lock (_guns) charging = _guns[gunNo].Charging;
        await SendAsync(YkcType.RemoteStopReply, Concat(_pile, [IntToBcd(gunNo), charging ? (byte)1 : (byte)0, charging ? (byte)0 : (byte)2]), f.Seq);
        if (charging) await FinishChargingAsync(gunNo, 0x40);
    }

    /// <param name="abort">Кадр аварийной остановки (0x1D/0x21) с полями после пистолета — уходит перед 0x19.</param>
    private async Task FinishChargingAsync(int gunNo, byte stopReason, (byte Type, byte[] Payload)? abort = null)
    {
        byte[] trade;
        decimal energy;
        DateTime start;
        byte[]? end = null;
        lock (_guns)
        {
            var gun = _guns[gunNo];
            if (!gun.Charging) return;
            gun.Charging = false;
            trade = gun.Trade!;
            energy = gun.EnergyKwh;
            start = gun.StartLocal;
            if (!IsAc)
                end = BmsBody(gun, [(byte)gun.Soc], Le16(Bms.MaxCellVoltageV * 100 - 30), Le16(Bms.MaxCellVoltageV * 100 - 10),
                    [28 + 50, 33 + 50], Le16(ChargeMinutes(gun)), Le16(energy * 10), U32Bytes(1));
        }
        if (end is not null)
        {
            if (abort is { } a) await SendAsync(a.Type, Concat(trade, _pile, [IntToBcd(gunNo)], a.Payload));
            await SendAsync(YkcType.BmsChargingEnd, end);
        }
        await SendRealtimeAsync(gunNo);
        await SendRecordAsync(gunNo, trade, energy, start, stopReason);
    }

    /// <summary>Итог зарядки 0x3B (158 байт тела).</summary>
    public async Task SendRecordAsync(int gunNo, byte[] trade, decimal energyKwh, DateTime startLocal, byte stopReason)
    {
        var e = (uint)(energyKwh * 10000);
        var price = U32Bytes((uint)((TariffPerKwh ?? 1m) * 100000));
        var group = Concat(price, U32Bytes(0), U32Bytes(0), U32Bytes(0));
        var flat = Concat(price, U32Bytes(e), U32Bytes(e), U32Bytes(e * (uint)(TariffPerKwh ?? 1m)));
        var body = Concat(
            trade, _pile, [IntToBcd(gunNo)], ToCp56(startLocal), ToCp56(DateTime.UtcNow.AddHours(5)),
            group, group, flat, group,
            new byte[5], Concat(U32Bytes(e), [0]),
            U32Bytes(e), U32Bytes(e), U32Bytes(0),
            new byte[17], [0x01], ToCp56(DateTime.UtcNow.AddHours(5)), [stopReason], new byte[8]);
        await SendAsync(YkcType.TransactionRecord, body);
    }

    private async Task SendRealtimeAsync(int gunNo)
    {
        byte[] body;
        lock (_guns)
        {
            var g = _guns[gunNo];
            var status = g.Charging ? YkcGunStatus.Charging : YkcGunStatus.Idle;
            var voltage = g.Charging ? (ushort)(OutVoltageV * 10) : (ushort)0;
            var current = g.Charging ? (ushort)(OutCurrentA * 10) : (ushort)0;
            body = Concat(
                g.Charging ? g.Trade! : new byte[16], _pile, [IntToBcd(gunNo), (byte)status, 0x01, g.Plugged ? (byte)1 : (byte)0],
                BitConverter.GetBytes(voltage), BitConverter.GetBytes(current), [0], new byte[8],
                [g.Charging && !IsAc ? (byte)g.Soc : (byte)0, 0], BitConverter.GetBytes((ushort)0), BitConverter.GetBytes((ushort)0),
                U32Bytes((uint)(g.EnergyKwh * 10000)), U32Bytes((uint)(g.EnergyKwh * 10000)), U32Bytes(0), [0, 0]);
        }
        await SendAsync(YkcType.Realtime, body);
    }

    /// <summary>0x15 рукопожатие (VIN, батарея) и 0x17 параметры зарядки — один раз после старта.</summary>
    private async Task SendBmsStartAsync(int gunNo)
    {
        if (IsAc) return;
        byte[] handshake, config;
        lock (_guns)
        {
            var g = _guns[gunNo];
            if (!g.Charging) return;
            handshake = BmsBody(g, [0x01, 0x01, 0x00, Bms.BatteryType], Le16(Bms.CapacityAh * 10), Le16(Bms.RatedVoltageV * 10),
                [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00], [0xFF, 0x0C, 0x1F], [0xFF, 0xFF, 0x00], [0xFF, 0x00],
                System.Text.Encoding.ASCII.GetBytes(Bms.Vin.PadRight(17, '\0')[..17]), Enumerable.Repeat((byte)0xFF, 8).ToArray());
            config = BmsBody(g, Le16(Bms.MaxCellVoltageV * 100), Current(Bms.MaxChargeCurrentA), Le16(Bms.RatedEnergyKwh * 10),
                Le16(Bms.MaxChargeVoltageV * 10), [(byte)(Bms.MaxTemperatureC + 50)], Le16(g.Soc * 10), Le16(Bms.RatedVoltageV * 10),
                Le16(7500), Le16(2000), Current(250), Current(0));
        }
        await SendAsync(YkcType.BmsHandshake, handshake);
        await SendAsync(YkcType.BmsParams, config);
    }

    /// <summary>0x23 запрос BMS и выход станции, 0x25 состояние батареи — каждый тик зарядки.</summary>
    private async Task SendBmsProgressAsync(int gunNo)
    {
        if (IsAc) return;
        byte[] demand, info;
        lock (_guns)
        {
            var g = _guns[gunNo];
            if (!g.Charging) return;
            var remaining = (100 - g.Soc) / 100m * Bms.RatedEnergyKwh / (OutVoltageV * OutCurrentA / 1000) * 60;
            demand = BmsBody(g, Le16(OutVoltageV * 10), Current(OutCurrentA), [0x02], Le16(OutVoltageV * 10), Current(OutCurrentA),
                Le16(Bms.MaxCellVoltageV * 100 - 25 + (1 << 12)), [(byte)g.Soc], Le16(remaining),
                Le16(OutVoltageV * 10), Current(OutCurrentA), Le16(ChargeMinutes(g)));
            // ячейка №17 с макс. напряжением, 31 °C (точка 3) / 27 °C (точка 8), флаги: всё в норме
            info = BmsBody(g, [17, 31 + 50, 3, 27 + 50, 8], [0x00, 0x00]);
        }
        await SendAsync(YkcType.BmsDemand, demand);
        await SendAsync(YkcType.BmsInfo, info);
    }

    private byte[] BmsBody(Gun g, params byte[][] fields) => Concat([g.Trade!, _pile, [IntToBcd(g.Number)], .. fields]);

    private static int ChargeMinutes(Gun g) => (int)(DateTime.UtcNow.AddHours(5) - g.StartLocal).TotalMinutes;

    private static byte[] Le16(decimal value) => BitConverter.GetBytes((ushort)Math.Clamp(Math.Round(value), 0, ushort.MaxValue));

    /// <summary>Ток в кадрах BMS: 0.1 А со смещением −400 А.</summary>
    private static byte[] Current(decimal amps) => Le16((400 - amps) * 10);

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            await LoggedIn.WaitAsync(ct);
            using var timer = new PeriodicTimer(HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(ct))
                foreach (var gun in _guns.Keys)
                    await SendAsync(YkcType.Heartbeat, Concat(_pile, [IntToBcd(gun), 0x00]));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private async Task ChargingLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(ChargingTick);
            while (await timer.WaitForNextTickAsync(ct))
            {
                List<int> charging, full;
                lock (_guns)
                {
                    charging = _guns.Values.Where(g => g.Charging).Select(g => g.Number).ToList();
                    foreach (var n in charging)
                    {
                        _guns[n].EnergyKwh += EnergyPerTickKwh;
                        if (!IsAc) _guns[n].Soc = Math.Min(100, _guns[n].Soc + Bms.SocPerTick);
                    }
                    full = IsAc ? [] : charging.Where(n => _guns[n].Soc >= 100).ToList();
                }
                foreach (var n in charging)
                {
                    await SendRealtimeAsync(n);
                    await SendBmsProgressAsync(n);
                }
                // BMS: достигнут требуемый SOC (0x1D, причина 0x01) → остановка «SOC 100%»
                foreach (var n in full) await FinishChargingAsync(n, 0x41, (YkcType.BmsAbort, [0x01, 0x00, 0x00, 0x00]));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private sealed class Gun(int number)
    {
        public int Number { get; } = number;
        public bool Plugged { get; set; } = true;
        public bool Charging { get; set; }
        public byte[]? Trade { get; set; }
        public byte[]? PendingTrade { get; set; }
        public DateTime PendingUntil { get; set; }
        public decimal EnergyKwh { get; set; }
        public int Soc { get; set; }
        public DateTime StartLocal { get; set; }
    }
}
