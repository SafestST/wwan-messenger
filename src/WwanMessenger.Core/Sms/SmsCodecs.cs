using System.Text;

namespace WwanMessenger.Core.Sms;

/// <summary>GSM 03.38 default alphabet + extension table, and septet packing.</summary>
public static class SmsCodecs
{
    public const char Esc = (char)0x1B;

    /// <summary>Canonical TS 23.038 §6.1.1 row 0x1B-0x1F note: 0x1B is the
    /// EXTENSION ESCAPE code point (no printable glyph lives there — the
    /// extension table values are sent after it), 0x1C=Æ, 0x1D=æ, 0x1E=ß,
    /// 0x1F=É. HISTORY: this table once carried '?' at the reserved slot of a
    /// shifted rebuild, so every outgoing '?' went on the air as a code point
    /// recipients rendered as an accented E — reproduced live over the air on
    /// Orange PL and fixed by rebuilding both rows against the spec (exact-PDU
    /// vectors in the smoke suite now pin this).</summary>
    public const byte EscSeptet = 0x1B;

    private static readonly char[] Basic =
    {
        '@','\u00A3','\u0024','\u00A5','\u00E8','\u00E9','\u00F9','\u00EC','\u00F2','\u00C7','\n','\u00D8','\u00F8','\r','\u00C5','\u00E5',
        '\u0394','_','\u03A6','\u0393','\u039B','\u03A9','\u03A0','\u03A8','\u03A3','\u0398','\u039E','\u001B','\u00C6','\u00E6','\u00DF','\u00C9', // 0x1B = ESC (extension escape)
        ' ','!','"','#','\u00A4','%','&','\'','(',')','*','+',',','-','.','/',
        '0','1','2','3','4','5','6','7','8','9',':',';','<','=','>','?',
        '\u00A1','A','B','C','D','E','F','G','H','I','J','K','L','M','N','O',
        'P','Q','R','S','T','U','V','W','X','Y','Z','\u00C4','\u00D6','\u00D1','\u00DC','\u00A7',
        '\u00BF','a','b','c','d','e','f','g','h','i','j','k','l','m','n','o',
        'p','q','r','s','t','u','v','w','x','y','z','\u00E4','\u00F6','\u00F1','\u00FC','\u00E0',
    };

    // Canonical TS 23.038 extension table: septet value (sent after ESC) -> char.
    private static readonly (char Ch, byte Septet)[] ExtTable =
    {
        ('\f', 0x0A),
        ('^', 0x14),
        ('{', 0x28),
        ('}', 0x29),
        ('\\', 0x2F),
        ('[', 0x3C),
        ('~', 0x3D),
        (']', 0x3E),
        ('|', 0x40),
        ('\u20AC', 0x65),
    };

    private static readonly Dictionary<char, byte> ExtEncode = ExtTable.ToDictionary(t => t.Ch, t => t.Septet);
    private static readonly Dictionary<byte, char> ExtDecode = ExtTable.ToDictionary(t => t.Septet, t => t.Ch);

    private static readonly Dictionary<char, byte> BasicLookup = BuildBasicLookup();

    private static Dictionary<char, byte> BuildBasicLookup()
    {
        var d = new Dictionary<char, byte>(128);
        for (byte i = 0; i < Basic.Length; i++)
        {
            if (i == EscSeptet) continue; // 0x1B is the extension escape — never an encoding target
            if (!d.ContainsKey(Basic[i])) d[Basic[i]] = i;
        }
        return d;
    }

    public static bool IsGsm7Representable(string text)
        => text.All(c => BasicLookup.ContainsKey(c) || ExtEncode.ContainsKey(c));

    public static int SeptetCount(string text)
    {
        int n = 0;
        foreach (var c in text)
        {
            if (BasicLookup.ContainsKey(c)) n++;
            else if (ExtEncode.ContainsKey(c)) n += 2;
            else return -1;
        }
        return n;
    }

    /// <summary>Septets consumed by one character (1 basic, 2 extension, -1 not representable).</summary>
    public static int SeptetCost(char c)
        => BasicLookup.ContainsKey(c) ? 1 : (ExtEncode.ContainsKey(c) ? 2 : -1);

    /// <summary>Text -> septet values (ESC-prefixed for extension chars).</summary>
    public static byte[] ToSeptets(string text)
    {
        var list = new List<byte>(text.Length + 8);
        foreach (var c in text)
        {
            if (BasicLookup.TryGetValue(c, out var b)) list.Add(b);
            else if (ExtEncode.TryGetValue(c, out var ext)) { list.Add(0x1B); list.Add(ext); }
            else throw new ArgumentException($"Character '{c}' (U+{(int)c:X4}) is not in the GSM alphabet.");
        }
        return list.ToArray();
    }

    /// <summary>Glyph for one decoded septet: canonical TS 23.038 basic char,
    /// '?' for out-of-range values.</summary>
    private static char CharForSeptet(byte s)
        => s < Basic.Length ? Basic[s] : '?';

    public static string FromSeptets(IEnumerable<byte> septets)
    {
        var sb = new StringBuilder();
        bool esc = false;
        foreach (var s in septets)
        {
            if (esc)
            {
                esc = false;
                // ESC + known extension value -> extension char; ESC + anything
                // else is a reserved combination — render '?' (spec-undefined).
                sb.Append(ExtDecode.TryGetValue(s, out var ch) ? ch : '?');
                continue;
            }
            if (s == EscSeptet) { esc = true; continue; }
            sb.Append(CharForSeptet(s));
        }
        return sb.ToString();
    }

    /// <summary>Pack septets into octets, LSB-first, starting at bit offset <paramref name="startBit"/>.</summary>
    public static byte[] PackSeptets(byte[] septets, int startBit = 0)
    {
        int totalBits = startBit + septets.Length * 7;
        var bytes = new byte[(totalBits + 7) / 8];
        for (int i = 0; i < septets.Length; i++)
        {
            int bitPos = startBit + i * 7;
            for (int b = 0; b < 7; b++)
            {
                if ((septets[i] & (1 << b)) != 0)
                {
                    int abs = bitPos + b;
                    bytes[abs / 8] |= (byte)(1 << (abs % 8));
                }
            }
        }
        return bytes;
    }

    /// <summary>Unpack <paramref name="septetCount"/> septets from a packed buffer starting at bit offset.</summary>
    public static byte[] UnpackSeptets(byte[] data, int startBit, int septetCount)
    {
        var result = new byte[septetCount];
        for (int i = 0; i < septetCount; i++)
        {
            int bitPos = startBit + i * 7;
            byte v = 0;
            for (int b = 0; b < 7; b++)
            {
                int abs = bitPos + b;
                if (abs / 8 >= data.Length) return result; // short buffer; return what we have
                if ((data[abs / 8] & (1 << (abs % 8))) != 0) v |= (byte)(1 << b);
            }
            result[i] = v;
        }
        return result;
    }

    public static byte[] EncodeUcs2(string text) => Encoding.BigEndianUnicode.GetBytes(text);

    public static string DecodeUcs2(byte[] data, int offset, int count)
        => Encoding.BigEndianUnicode.GetString(data, offset, count - (count % 2));

    /// <summary>Semi-octet BCD encoding used for phone numbers (swap nibbles, pad with 0xF).</summary>
    public static byte[] EncodeSemiOctets(string digits)
    {
        var bytes = new byte[(digits.Length + 1) / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            int lo = digits[2 * i] & 0xF;
            int hi = 2 * i + 1 < digits.Length ? digits[2 * i + 1] & 0xF : 0xF;
            bytes[i] = (byte)((hi << 4) | lo);
        }
        return bytes;
    }

    public static string DecodeSemiOctets(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 2);
        foreach (var b in data)
        {
            int lo = b & 0xF, hi = (b >> 4) & 0xF;
            if (lo < 10) sb.Append((char)('0' + lo));
            if (hi < 10) sb.Append((char)('0' + hi));
        }
        return sb.ToString();
    }

    /// <summary>Semi-octet timestamp (SCTS) decode â†' DateTimeOffset.
    /// Sign convention (matches <see cref="EncodeTimestamp"/>): timezone octet low nibble
    /// = units digit with bit 3 as negative flag, high nibble = tens digit.</summary>
    public static DateTimeOffset DecodeTimestamp(byte[] scts)
    {
        if (scts.Length < 7) return DateTimeOffset.UtcNow;
        string s = DecodeSemiOctets(scts[..6]); // yy MM dd hh mm ss
        if (s.Length < 12) return DateTimeOffset.UtcNow;
        int yy = int.Parse(s[..2]), mo = int.Parse(s.Substring(2, 2)), dd = int.Parse(s.Substring(4, 2));
        int hh = int.Parse(s.Substring(6, 2)), mi = int.Parse(s.Substring(8, 2)), ss = int.Parse(s.Substring(10, 2));

        int lo = scts[6] & 0xF;
        int hi = (scts[6] >> 4) & 0xF;
        int quarters = (hi * 10) + (lo & 0x7);
        bool negative = (lo & 0x8) != 0;
        var offset = TimeSpan.FromMinutes(15 * (negative ? -quarters : quarters));

        var year = yy + (yy < 90 ? 2000 : 1900);
        try { return new DateTimeOffset(year, mo, dd, hh, mi, ss, offset); }
        catch (ArgumentOutOfRangeException) { return DateTimeOffset.UtcNow; }
    }

    public static byte[] EncodeTimestamp(DateTimeOffset t)
    {
        static byte Swap(int v) => (byte)(((v % 10) << 4) | ((v / 10) % 10));
        var local = t.DateTime; // SCTS fields are local wall-clock time; the tz octet carries the offset
        var tzQuarter = (int)Math.Round(t.Offset.TotalMinutes / 15.0, MidpointRounding.AwayFromZero);
        bool negative = tzQuarter < 0;
        int tzAbs = Math.Abs(tzQuarter);
        return new[]
        {
            Swap(local.Year % 100), Swap(local.Month), Swap(local.Day),
            Swap(local.Hour), Swap(local.Minute), Swap(local.Second),
            (byte)((negative ? 0x08 : 0x00) | (((tzAbs / 10) % 10) << 4) | (tzAbs % 10)),
        };
    }
}
