namespace YKC.Gateway.Protocol;

/// <summary>
/// Кадр протокола YKC (云快充): 0x68 | длина | номер (2, LE) | шифрование | тип | тело | CRC (2).
/// Длина = номер + шифрование + тип + тело. CRC считается от номера до конца тела.
/// </summary>
public sealed record YkcFrame(ushort Seq, bool Encrypted, byte Type, byte[] Body)
{
    public const byte StartByte = 0x68;
    private const int HeaderAfterLength = 4; // номер (2) + шифрование (1) + тип (1)

    /// <summary>Порядок байтов CRC. По спецификации младший байт первым.</summary>
    public byte[] Encode(bool crcBigEndian = false)
    {
        var length = HeaderAfterLength + Body.Length;
        if (length > byte.MaxValue)
            throw new InvalidOperationException($"Тело кадра 0x{Type:X2} слишком длинное: {Body.Length} байт");

        var buf = new byte[2 + length + 2];
        buf[0] = StartByte;
        buf[1] = (byte)length;
        buf[2] = (byte)Seq;
        buf[3] = (byte)(Seq >> 8);
        buf[4] = Encrypted ? (byte)1 : (byte)0;
        buf[5] = Type;
        Body.CopyTo(buf, 6);

        var crc = YkcCrc.Compute(buf.AsSpan(2, length));
        buf[2 + length] = crcBigEndian ? (byte)(crc >> 8) : (byte)crc;
        buf[3 + length] = crcBigEndian ? (byte)crc : (byte)(crc >> 8);
        return buf;
    }
}

/// <summary>CRC-16/MODBUS (полином 0xA001 в отражённом виде, начальное значение 0xFFFF).</summary>
public static class YkcCrc
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }
}

/// <summary>
/// Собирает кадры из TCP-потока: кадр может прийти по частям или несколько в одном пакете.
/// Мусор до стартового байта и кадры с неверной CRC отбрасываются с описанием проблемы.
/// </summary>
public sealed class YkcFrameReader
{
    private readonly List<byte> _buffer = new();

    /// <summary>true, если CRC в принятых кадрах шла старшим байтом вперёд (вопреки спецификации).</summary>
    public bool? CrcBigEndian { get; private set; }

    public IReadOnlyList<YkcFrame> Feed(ReadOnlySpan<byte> data, Action<string>? onProblem = null)
    {
        _buffer.AddRange(data.ToArray());
        var frames = new List<YkcFrame>();

        while (_buffer.Count > 0)
        {
            var start = _buffer.IndexOf(YkcFrame.StartByte);
            if (start != 0)
            {
                var junk = start < 0 ? _buffer.Count : start;
                onProblem?.Invoke($"пропущено {junk} байт до начала кадра: {Convert.ToHexString(_buffer.GetRange(0, Math.Min(junk, 32)).ToArray())}");
                _buffer.RemoveRange(0, junk);
                if (start < 0) break;
            }

            if (_buffer.Count < 2) break;
            var length = _buffer[1];
            var total = 2 + length + 2;
            if (length < 4)
            {
                onProblem?.Invoke($"слишком короткий кадр (длина {length}), пропускаю стартовый байт");
                _buffer.RemoveAt(0);
                continue;
            }
            if (_buffer.Count < total) break;

            var raw = _buffer.GetRange(0, total).ToArray();
            var crc = YkcCrc.Compute(raw.AsSpan(2, length));
            var lo = raw[2 + length];
            var hi = raw[3 + length];
            bool? bigEndian = lo == (byte)crc && hi == (byte)(crc >> 8) ? false
                : lo == (byte)(crc >> 8) && hi == (byte)crc ? true
                : null;

            if (bigEndian is null)
            {
                onProblem?.Invoke($"неверная CRC (ожидалась {crc:X4}), кадр отброшен: {Convert.ToHexString(raw)}");
                // Сдвигаемся на один байт: «0x68» мог оказаться внутри мусора.
                _buffer.RemoveAt(0);
                continue;
            }

            CrcBigEndian ??= bigEndian;
            _buffer.RemoveRange(0, total);
            frames.Add(new YkcFrame(
                Seq: (ushort)(raw[2] | raw[3] << 8),
                Encrypted: raw[4] != 0,
                Type: raw[5],
                Body: raw.AsSpan(6, length - 4).ToArray()));
        }

        return frames;
    }
}
