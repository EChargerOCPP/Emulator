using System.Buffers.Binary;

namespace YKC.Gateway.Protocol;

/// <summary>Кодирование полей YKC: BCD, числа little-endian, время CP56Time2a.</summary>
public static class YkcCodec
{
    /// <summary>BCD-байты как строка цифр: 32 01 02 → "320102".</summary>
    public static string BcdToString(ReadOnlySpan<byte> bcd) => Convert.ToHexString(bcd);

    /// <summary>Строка цифр в BCD фиксированной длины, недостающее дополняется нулями слева.</summary>
    public static byte[] StringToBcd(string digits, int length)
    {
        if (digits.Length > length * 2 || !digits.All(char.IsAsciiDigit))
            throw new ArgumentException($"Ожидалось не больше {length * 2} цифр: '{digits}'", nameof(digits));
        return Convert.FromHexString(digits.PadLeft(length * 2, '0'));
    }

    /// <summary>Номер пистолета хранится одним BCD-байтом: 0x01 → 1, 0x12 → 12.</summary>
    public static int BcdToInt(byte b) => (b >> 4) * 10 + (b & 0x0F);

    public static byte IntToBcd(int value)
    {
        if (value is < 0 or > 99) throw new ArgumentOutOfRangeException(nameof(value));
        return (byte)((value / 10 << 4) | value % 10);
    }

    public static ushort U16(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt16LittleEndian(s);
    public static uint U32(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt32LittleEndian(s);

    /// <summary>Беззнаковое little-endian число произвольной длины (у показаний счётчика — 5 байт).</summary>
    public static ulong ULittle(ReadOnlySpan<byte> s)
    {
        ulong v = 0;
        for (var i = s.Length - 1; i >= 0; i--) v = v << 8 | s[i];
        return v;
    }

    public static byte[] U32Bytes(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }

    /// <summary>
    /// CP56Time2a: мс (2, LE, включая секунды), минуты, часы, день месяца (+ день недели в старших битах),
    /// месяц, год от 2000. Время — местное время станции.
    /// </summary>
    public static byte[] ToCp56(DateTime local)
    {
        var ms = (ushort)(local.Second * 1000 + local.Millisecond);
        var dayOfWeek = local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek;
        return
        [
            (byte)ms, (byte)(ms >> 8),
            (byte)(local.Minute & 0x3F),
            (byte)(local.Hour & 0x1F),
            (byte)(dayOfWeek << 5 | local.Day & 0x1F),
            (byte)(local.Month & 0x0F),
            (byte)((local.Year - 2000) & 0x7F),
        ];
    }

    /// <summary>Разбор CP56Time2a. Невозможная дата (нули, мусор) даёт null.</summary>
    public static DateTime? FromCp56(ReadOnlySpan<byte> b)
    {
        var ms = U16(b);
        try
        {
            return new DateTime(
                2000 + (b[6] & 0x7F), b[5] & 0x0F, b[4] & 0x1F,
                b[3] & 0x1F, b[2] & 0x3F, ms / 1000, ms % 1000, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
