using System.Text;

namespace WwanMessenger.Core.Sms;

public sealed class SubmitPdu
{
    public string Hex { get; init; } = "";
    /// <summary>Octets of the TPDU excluding the SMSC length byte (what AT+CMGS wants).</summary>
    public int TpduOctets { get; init; }
}

public sealed class DeliverSms
{
    public string Sender { get; init; } = "";
    public string Text { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
    public int ConcatRef { get; init; } = -1;
    public int ConcatTotal { get; init; } = 1;
    public int ConcatSeq { get; init; } = 1;
    public bool IsMultipart => ConcatTotal > 1;
}

public sealed class SubmitSms
{
    public string Destination { get; init; } = "";
    public string Text { get; init; } = "";
    public int ConcatRef { get; init; } = -1;
    public int ConcatTotal { get; init; } = 1;
    public int ConcatSeq { get; init; } = 1;
}

/// <summary>SMS PDU mode encoder/decoder (GSM 03.40). Primary protocol path for WwanMessenger:
/// charset-independent, works on every conformant modem, and gives us full control of
/// multipart segmentation.</summary>
public static class SmsPdu
{
    public static SubmitPdu EncodeSubmit(string destination, string text, int concatRef, int part, int total)
    {
        bool international = destination.StartsWith('+');
        string digits = new string(destination.Where(char.IsDigit).ToArray());

        var tpdu = new List<byte>();

        // TP-MTI=01 (SMS-SUBMIT), TP-RD=0, TP-VPF=none, TP-SRR=0, RP=0; UDHI when concatenated
        byte first = 0x01;
        if (total > 1) first |= 0x40; // TP-UDHI
        tpdu.Add(first);

        tpdu.Add(0x00); // TP-MR

        // TP-DA
        tpdu.Add((byte)digits.Length);
        tpdu.Add(international ? (byte)0x91 : (byte)0x81);
        tpdu.AddRange(SmsCodecs.EncodeSemiOctets(digits));

        tpdu.Add(0x00); // TP-PID

        bool ucs2 = !SmsCodecs.IsGsm7Representable(text);
        tpdu.Add(ucs2 ? (byte)0x08 : (byte)0x00); // TP-DCS

        // (no TP-VP: VPF=none)

        // TP-UD with optional UDH
        var ud = new List<byte>();
        if (total > 1)
        {
            ud.Add(0x05);            // UDH length (excl. this byte)
            ud.Add(0x00);            // IEI: concatenated SMS, 8-bit reference
            ud.Add(0x03);            // IE length
            ud.Add((byte)(concatRef & 0xFF));
            ud.Add((byte)(total & 0xFF));
            ud.Add((byte)(part & 0xFF));
        }

        if (ucs2)
        {
            ud.AddRange(SmsCodecs.EncodeUcs2(text));
            tpdu.Add((byte)ud.Count); // UDL in octets
            tpdu.AddRange(ud);
        }
        else
        {
            var septets = SmsCodecs.ToSeptets(text);
            int udhSeptets = (ud.Count * 8 + 6) / 7; // UDH octets rounded up to septet boundary (fill bits)
            tpdu.Add((byte)(udhSeptets + septets.Length)); // UDL in septets (incl. UDH septets)
            if (ud.Count > 0) tpdu.AddRange(ud);
            tpdu.AddRange(SmsCodecs.PackSeptets(septets, FillBits(ud.Count)));
        }

        var full = new List<byte>(tpdu.Count + 1) { 0x00 }; // SMSC: none (len 0)
        full.AddRange(tpdu);

        return new SubmitPdu
        {
            Hex = BytesToHex(full.ToArray()),
            TpduOctets = tpdu.Count,
        };
    }

    private static int FillBits(int udhOctets)
    {
        // bits of padding needed so the message septets start on a septet boundary after the UDH
        int rem = (udhOctets * 8) % 7;
        return rem == 0 ? 0 : 7 - rem;
    }

    public static DeliverSms DecodeDeliver(string hex) => DecodeDeliver(HexToBytes(hex));

    public static DeliverSms DecodeDeliver(byte[] pdu)
    {
        int i = 0;
        int smscLen = pdu[i++];
        i += smscLen; // skip SMSC

        byte first = pdu[i++];
        if ((first & 0x03) != 0x00)
            throw new ArgumentException($"PDU is not an SMS-DELIVER (first octet 0x{first:X2}).");
        bool udhi = (first & 0x40) != 0;

        int oaLen = pdu[i++];               // number of useful semi-octets (digits)
        byte tonNpi = pdu[i++];
        int oaOctets = (oaLen + 1) / 2;
        byte[] oa = pdu[i..(i + oaOctets)];
        i += oaOctets;
        string sender = SmsCodecs.DecodeSemiOctets(oa);
        if (sender.EndsWith("F")) sender = sender[..^1];
        if (((tonNpi >> 4) & 0x7) == 0x1) sender = "+" + sender; // international

        i++; // PID
        byte dcs = pdu[i++];
        byte[] scts = pdu[i..(i + 7)];
        i += 7;
        var ts = SmsCodecs.DecodeTimestamp(scts);

        int udl = pdu[i++];
        int udBitsTotal = udl * (IsGsm7Dcs(dcs) ? 7 : 8);
        int udAvailable = (pdu.Length - i) * 8;
        int usableBits = Math.Min(udBitsTotal, udAvailable); // tolerate short buffers
        byte[] ud = pdu[i..];

        int refId = -1, total = 1, seq = 1;
        int startBit = 0;
        if (udhi && ud.Length > 0)
        {
            int udhLen = ud[0]; // excluding the length byte itself
            int p = 1;
            int end = 1 + udhLen;
            while (p + 2 <= end)
            {
                byte iei = ud[p];
                int iel = ud[p + 1];
                if (iei == 0x00 && iel == 3 && p + 2 + 3 <= ud.Length)
                {
                    refId = ud[p + 2]; total = ud[p + 3]; seq = ud[p + 4];
                }
                else if (iei == 0x08 && iel == 4 && p + 2 + 4 <= ud.Length)
                {
                    refId = (ud[p + 2] << 8) | ud[p + 3]; total = ud[p + 4]; seq = ud[p + 5];
                }
                p += 2 + iel;
            }
            int consumedBits = 8 * (1 + udhLen);
            if (IsGsm7Dcs(dcs))
            {
                int rem = consumedBits % 7;
                startBit = rem == 0 ? 0 : consumedBits + (7 - rem); // skip fill bits
            }
            else startBit = consumedBits;
        }

        string text;
        if (IsGsm7Dcs(dcs))
        {
            int payloadBits = usableBits - startBit;
            int septets = Math.Max(0, payloadBits / 7);
            var unpacked = SmsCodecs.UnpackSeptets(ud, startBit, septets);
            text = SmsCodecs.FromSeptets(unpacked);
        }
        else if (((dcs & 0x0C) >> 2) == 0x02) // UCS-2
        {
            int bytesAvail = Math.Min((usableBits - startBit) / 8, ud.Length - startBit / 8);
            text = SmsCodecs.DecodeUcs2(ud, startBit / 8, Math.Max(0, bytesAvail));
        }
        else
        {
            text = BytesToHex(ud); // 8-bit binary — surface as hex for now
        }

        return new DeliverSms
        {
            Sender = sender,
            Text = text,
            Timestamp = ts,
            ConcatRef = refId,
            ConcatTotal = total,
            ConcatSeq = seq,
        };
    }

    public static SubmitSms DecodeSubmit(string hex)
    {
        var pdu = HexToBytes(hex);
        int i = 0;
        int smscLen = pdu[i++];
        i += smscLen;

        byte first = pdu[i++];
        if ((first & 0x03) != 0x01)
            throw new ArgumentException($"PDU is not an SMS-SUBMIT (first octet 0x{first:X2}).");
        bool udhi = (first & 0x40) != 0;
        bool hasVp = ((first >> 3) & 0x3) == 0x2;

        i++; // MR

        int daLen = pdu[i++];
        byte tonNpi = pdu[i++];
        int daOctets = (daLen + 1) / 2;
        var da = pdu[i..(i + daOctets)];
        i += daOctets;
        string dest = SmsCodecs.DecodeSemiOctets(da);
        if (dest.EndsWith("F")) dest = dest[..^1];
        if (((tonNpi >> 4) & 0x7) == 0x1) dest = "+" + dest;

        i++; // PID
        byte dcs = pdu[i++];
        if (hasVp) i++; // VP

        int udl = pdu[i++];
        byte[] ud = pdu[i..];

        int refId = -1, total = 1, seq = 1;
        int startBit = 0;
        if (udhi && ud.Length > 0)
        {
            int udhLen = ud[0];
            int p = 1;
            while (p + 2 <= 1 + udhLen)
            {
                byte iei = ud[p];
                int iel = ud[p + 1];
                if (iei == 0x00 && iel == 3) { refId = ud[p + 2]; total = ud[p + 3]; seq = ud[p + 4]; }
                p += 2 + iel;
            }
            int consumedBits = 8 * (1 + udhLen);
            if (IsGsm7Dcs(dcs))
            {
                int rem = consumedBits % 7;
                startBit = rem == 0 ? 0 : consumedBits + (7 - rem);
            }
            else startBit = consumedBits;
        }

        string text;
        if (IsGsm7Dcs(dcs))
        {
            int payloadBits = Math.Min(udl * 7, (pdu.Length - i) * 8) - startBit;
            var septets = SmsCodecs.UnpackSeptets(ud, startBit, Math.Max(0, payloadBits / 7));
            text = SmsCodecs.FromSeptets(septets);
        }
        else
        {
            int off = startBit / 8;
            text = SmsCodecs.DecodeUcs2(ud, off, Math.Max(0, ud.Length - off));
        }

        return new SubmitSms
        {
            Destination = dest,
            Text = text,
            ConcatRef = refId,
            ConcatTotal = total,
            ConcatSeq = seq,
        };
    }

    /// <summary>Build an SMS-DELIVER PDU — used by the simulated network to inject inbound traffic.
    /// Supports optional concatenation UDH so multipart inbound can be exercised end-to-end.</summary>
    public static (string Hex, int TpduOctets) EncodeDeliver(string sender, string text, DateTimeOffset scts,
        int concatRef = -1, int total = 1, int seq = 1)
    {
        bool international = sender.StartsWith('+');
        string digits = new string(sender.Where(char.IsDigit).ToArray());

        var tpdu = new List<byte> { 0x00 }; // TP-MTI=00 (DELIVER), no flags
        if (total > 1) tpdu[0] |= 0x40;     // TP-UDHI

        int daLen = digits.Length;
        tpdu.Add((byte)daLen);
        tpdu.Add(international ? (byte)0x91 : (byte)0x81);
        tpdu.AddRange(SmsCodecs.EncodeSemiOctets(digits));

        tpdu.Add(0x00); // PID

        bool ucs2 = !SmsCodecs.IsGsm7Representable(text);
        tpdu.Add(ucs2 ? (byte)0x08 : (byte)0x00); // DCS

        tpdu.AddRange(SmsCodecs.EncodeTimestamp(scts));

        var ud = new List<byte>();
        if (total > 1)
        {
            ud.Add(0x05); ud.Add(0x00); ud.Add(0x03);
            ud.Add((byte)(concatRef & 0xFF)); ud.Add((byte)total); ud.Add((byte)seq);
        }

        if (ucs2)
        {
            ud.AddRange(SmsCodecs.EncodeUcs2(text));
            tpdu.Add((byte)ud.Count);
            tpdu.AddRange(ud);
        }
        else
        {
            var septets = SmsCodecs.ToSeptets(text);
            int udhSeptets = (ud.Count * 8 + 6) / 7;
            tpdu.Add((byte)(udhSeptets + septets.Length));
            if (ud.Count > 0) tpdu.AddRange(ud);
            tpdu.AddRange(SmsCodecs.PackSeptets(septets, FillBits(ud.Count)));
        }

        var full = new List<byte>(tpdu.Count + 1) { 0x00 };
        full.AddRange(tpdu);
        return (BytesToHex(full.ToArray()), tpdu.Count);
    }

    public static bool IsGsm7Dcs(byte dcs) => (dcs & 0x0C) == 0x00;

    public static byte[] HexToBytes(string hex)
    {
        hex = new string(hex.Where(char.IsLetterOrDigit).ToArray());
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    public static string BytesToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }
}
