using static YKC.Gateway.Protocol.YkcCodec;

namespace YKC.Gateway.Protocol;

/// <summary>Типы кадров YKC 1.6. Нечётные шлёт станция, чётные — платформа.</summary>
public static class YkcType
{
    public const byte Login = 0x01, LoginReply = 0x02;
    public const byte Heartbeat = 0x03, HeartbeatReply = 0x04;
    public const byte BillingCheck = 0x05, BillingCheckReply = 0x06;
    public const byte BillingRequest = 0x09, BillingModel = 0x0A;
    public const byte RealtimeRequest = 0x12, Realtime = 0x13;
    public const byte BmsHandshake = 0x15, BmsParams = 0x17, BmsChargingEnd = 0x19, BmsError = 0x1B;
    public const byte BmsAbort = 0x1D, ChargerAbort = 0x21, BmsDemand = 0x23, BmsInfo = 0x25;
    public const byte CardStartRequest = 0x31, CardStartReply = 0x32;
    public const byte RemoteStartReply = 0x33, RemoteStart = 0x34;
    public const byte RemoteStopReply = 0x35, RemoteStop = 0x36;
    public const byte TransactionRecord = 0x3B, TransactionRecordAck = 0x40;
    public const byte WorkParamsReply = 0x51, WorkParams = 0x52;
    public const byte TimeSyncReply = 0x55, TimeSync = 0x56;
    public const byte RebootReply = 0x91, Reboot = 0x92;

    public static string Name(byte type) => type switch
    {
        Login => "вход", LoginReply => "ответ на вход",
        Heartbeat => "пульс", HeartbeatReply => "ответ на пульс",
        BillingCheck => "проверка тарифа", BillingCheckReply => "ответ на проверку тарифа",
        BillingRequest => "запрос тарифа", BillingModel => "тариф",
        RealtimeRequest => "запрос данных", Realtime => "данные в реальном времени",
        BmsHandshake => "рукопожатие BMS", BmsParams => "параметры BMS", BmsChargingEnd => "окончание зарядки BMS",
        BmsError => "ошибка", BmsAbort => "BMS прервала", ChargerAbort => "станция прервала",
        BmsDemand => "показатели BMS", BmsInfo => "данные BMS",
        CardStartRequest => "запрос старта с карты", CardStartReply => "ответ на старт с карты",
        RemoteStartReply => "ответ на удалённый старт", RemoteStart => "удалённый старт",
        RemoteStopReply => "ответ на удалённый стоп", RemoteStop => "удалённый стоп",
        TransactionRecord => "запись о транзакции", TransactionRecordAck => "подтверждение записи",
        WorkParamsReply => "ответ на параметры работы", WorkParams => "параметры работы",
        TimeSyncReply => "ответ на синхронизацию времени", TimeSync => "синхронизация времени",
        RebootReply => "ответ на перезагрузку", Reboot => "перезагрузка",
        _ => $"тип 0x{type:X2}",
    };
}

/// <summary>Состояние пистолета в данных реального времени (0x13).</summary>
public enum YkcGunStatus : byte { Offline = 0, Fault = 1, Idle = 2, Charging = 3 }

public sealed record YkcLogin(string PileCode, bool IsAc, int GunCount, byte ProtocolVersion, string Firmware, byte NetworkType)
{
    public static YkcLogin Parse(byte[] b) => new(
        PileCode: BcdToString(b.AsSpan(0, 7)),
        IsAc: b[7] == 1,
        GunCount: b[8],
        ProtocolVersion: b[9],
        Firmware: System.Text.Encoding.ASCII.GetString(b, 10, 8).TrimEnd('\0', ' '),
        NetworkType: b[18]);

    public const int MinLength = 19;
}

public sealed record YkcHeartbeat(string PileCode, int Gun, bool Faulted)
{
    public static YkcHeartbeat Parse(byte[] b) => new(BcdToString(b.AsSpan(0, 7)), BcdToInt(b[7]), b[8] != 0);
    public const int MinLength = 9;
}

public sealed record YkcBillingCheck(string PileCode, byte[] ModelCode)
{
    public static YkcBillingCheck Parse(byte[] b) => new(BcdToString(b.AsSpan(0, 7)), b.AsSpan(7, 2).ToArray());
    public const int MinLength = 9;
}

/// <summary>0x13. Энергия и сумма — за текущую сессию, а не показания счётчика.</summary>
public sealed record YkcRealtime(
    string TradeSeq, string PileCode, int Gun, YkcGunStatus Status, bool Plugged,
    decimal VoltageV, decimal CurrentA, int Soc, int ChargeMinutes,
    decimal EnergyKwh, decimal AmountYuan, ushort HardwareFaults)
{
    public static YkcRealtime Parse(byte[] b) => new(
        TradeSeq: BcdToString(b.AsSpan(0, 16)),
        PileCode: BcdToString(b.AsSpan(16, 7)),
        Gun: BcdToInt(b[23]),
        Status: (YkcGunStatus)b[24],
        Plugged: b[26] == 1,
        VoltageV: U16(b.AsSpan(27)) / 10m,
        CurrentA: U16(b.AsSpan(29)) / 10m,
        Soc: b[40],
        ChargeMinutes: U16(b.AsSpan(42)),
        EnergyKwh: U32(b.AsSpan(46)) / 10000m,
        AmountYuan: U32(b.AsSpan(54)) / 10000m,
        HardwareFaults: U16(b.AsSpan(58)));

    public const int MinLength = 60;

    public bool HasTrade => TradeSeq.Any(c => c != '0');
}

public sealed record YkcCardStartRequest(string PileCode, int Gun, string Card)
{
    public static YkcCardStartRequest Parse(byte[] b) =>
        new(BcdToString(b.AsSpan(0, 7)), BcdToInt(b[7]), Convert.ToHexString(b.AsSpan(10, 8)));
    public const int MinLength = 18;
}

public sealed record YkcRemoteStartReply(string TradeSeq, string PileCode, int Gun, bool Success, byte Reason)
{
    public static YkcRemoteStartReply Parse(byte[] b) =>
        new(BcdToString(b.AsSpan(0, 16)), BcdToString(b.AsSpan(16, 7)), BcdToInt(b[23]), b[24] == 1, b[25]);
    public const int MinLength = 26;

    public const byte ReasonNotPlugged = 0x05;

    public static string ReasonText(byte r) => r switch
    {
        0x00 => "нет", 0x01 => "номер станции не совпадает", 0x02 => "пистолет уже заряжает",
        0x03 => "неисправность станции", 0x04 => "станция не в сети", 0x05 => "пистолет не вставлен в машину",
        _ => $"код 0x{r:X2}",
    };
}

public sealed record YkcRemoteStopReply(string PileCode, int Gun, bool Success, byte Reason)
{
    public static YkcRemoteStopReply Parse(byte[] b) =>
        new(BcdToString(b.AsSpan(0, 7)), BcdToInt(b[7]), b[8] == 1, b[9]);
    public const int MinLength = 10;

    public const byte ReasonNotCharging = 0x02;
}

/// <summary>0x3B. Итоговая запись станции о зарядке.</summary>
public sealed record YkcTransactionRecord(
    string TradeSeq, string PileCode, int Gun, DateTime? StartLocal, DateTime? EndLocal,
    decimal MeterStartKwh, decimal MeterStopKwh, decimal TotalEnergyKwh, decimal AmountYuan,
    byte TradeType, byte StopReason, string Card)
{
    public static YkcTransactionRecord Parse(byte[] b) => new(
        TradeSeq: BcdToString(b.AsSpan(0, 16)),
        PileCode: BcdToString(b.AsSpan(16, 7)),
        Gun: BcdToInt(b[23]),
        StartLocal: FromCp56(b.AsSpan(24, 7)),
        EndLocal: FromCp56(b.AsSpan(31, 7)),
        MeterStartKwh: ULittle(b.AsSpan(102, 5)) / 10000m,
        MeterStopKwh: ULittle(b.AsSpan(107, 5)) / 10000m,
        TotalEnergyKwh: U32(b.AsSpan(112)) / 10000m,
        AmountYuan: U32(b.AsSpan(120)) / 10000m,
        TradeType: b[141],
        StopReason: b[149],
        Card: Convert.ToHexString(b.AsSpan(150, 8)));

    public const int MinLength = 158;
}

/// <summary>Сборка тел исходящих кадров (без заголовка и CRC).</summary>
public static class YkcBodies
{
    /// <summary>Код тарифной модели, который выдаёт наша платформа (по спецификации фиксированный «01 00»).</summary>
    public static readonly byte[] ModelCode = [0x01, 0x00];

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static byte[] LoginReply(byte[] pile, bool ok) => Concat(pile, [ok ? (byte)0 : (byte)1]);

    public static byte[] HeartbeatReply(byte[] pile, int gun) => Concat(pile, [IntToBcd(gun), 0]);

    public static byte[] BillingCheckReply(byte[] pile, byte[] modelCode, bool match) =>
        Concat(pile, modelCode, [match ? (byte)0 : (byte)1]);

    /// <summary>Одна цена на все 48 получасов (тариф «ровный»), цены с точностью 0.00001.</summary>
    public static byte[] BillingModel(byte[] pile, decimal pricePerKwh, decimal serviceFeePerKwh)
    {
        var price = U32Bytes((uint)Math.Round(pricePerKwh * 100000m));
        var fee = U32Bytes((uint)Math.Round(serviceFeePerKwh * 100000m));
        var rates = Concat(price, fee, price, fee, price, fee, price, fee);
        var periods = Enumerable.Repeat((byte)0x02, 48).ToArray();
        return Concat(pile, ModelCode, rates, [0x00], periods);
    }

    public static byte[] RealtimeRequest(byte[] pile, int gun) => Concat(pile, [IntToBcd(gun)]);

    /// <summary>
    /// Номер транзакции: номер станции (7) + пистолет (1) + ггММддЧЧммсс (6) + счётчик (2), всё в BCD.
    /// </summary>
    public static byte[] NewTradeSeq(byte[] pile, int gun, DateTime local, int counter) => Concat(
        pile, [IntToBcd(gun)], StringToBcd(local.ToString("yyMMddHHmmss"), 6), StringToBcd((counter % 10000).ToString(), 2));

    /// <summary>0x34. Баланс сообщаем станции с запасом: лимиты суммы считает платформа.</summary>
    public static byte[] RemoteStart(byte[] tradeSeq, byte[] pile, int gun, decimal balance) => Concat(
        tradeSeq, pile, [IntToBcd(gun)], new byte[8], new byte[8], U32Bytes((uint)Math.Round(balance * 100m)));

    public static byte[] RemoteStop(byte[] pile, int gun) => Concat(pile, [IntToBcd(gun)]);

    public static byte[] TransactionRecordAck(byte[] tradeSeq, bool ok) => Concat(tradeSeq, [ok ? (byte)0 : (byte)1]);

    /// <summary>0x32, отказ в старте с карты: 0x0B «станция не поддерживает карты».</summary>
    public static byte[] CardStartRejected(byte[] pile, int gun) =>
        Concat(new byte[16], pile, [IntToBcd(gun)], new byte[8], new byte[4], [0x00, 0x0B]);

    public static byte[] TimeSync(byte[] pile, DateTime local) => Concat(pile, ToCp56(local));

    /// <summary>0x52: разрешить или запретить работу станции, мощность в процентах (30–100).</summary>
    public static byte[] WorkParams(byte[] pile, bool allowWork, int maxPowerPercent) =>
        Concat(pile, [allowWork ? (byte)0 : (byte)1, (byte)Math.Clamp(maxPowerPercent, 30, 100)]);

    /// <summary>0x92: 0x01 — сразу, 0x02 — когда станция свободна.</summary>
    public static byte[] Reboot(byte[] pile, bool immediately) => Concat(pile, [immediately ? (byte)1 : (byte)2]);
}
