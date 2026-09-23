using WwanMessenger.Core.Channels;
using WwanMessenger.Core.Modem;
using WwanMessenger.Core.Sms;
using WwanMessenger.Core.Storage;

namespace WwanMessenger.Smoke;

internal static class Program
{
    private static int _failures;

    private static void Check(string name, bool pass, string detail = "")
    {
        if (pass) Console.WriteLine($"  PASS  {name}");
        else { Console.WriteLine($"  FAIL  {name}  {detail}"); _failures++; }
    }

    private static int Main()
    {
        Console.WriteLine("wwan-messenger smoke \u2014 core protocol & sim tests\n");

        CodecTests();
        PduTests();
        SegmenterTests();
        ServiceTests().GetAwaiter().GetResult();

        Console.WriteLine($"\n{(_failures == 0 ? "ALL GREEN" : $"{_failures} FAILURE(S)")}");
        return _failures == 0 ? 0 : 1;
    }

    // ---- GSM-7 codec -------------------------------------------------------

    private static void CodecTests()
    {
        Console.WriteLine("[codecs]");

        // Classic 3GPP TS 23.038 example: "hello" packs to E8 32 9B FD 06 (5 septets - 5 octets)
        var septets = SmsCodecs.ToSeptets("hello");
        var packed = SmsCodecs.PackSeptets(septets);
        Check("gsm7 'hello' vector", SmsPdu.BytesToHex(packed) == "E8329BFD06", SmsPdu.BytesToHex(packed));
        // TS 23.038's canonical 10-septet example: "hellohello" - E8 32 9B FD 46 97 D9 EC 37
        var packed10 = SmsCodecs.PackSeptets(SmsCodecs.ToSeptets("hellohello"));
        Check("gsm7 'hellohello' vector", SmsPdu.BytesToHex(packed10) == "E8329BFD4697D9EC37", SmsPdu.BytesToHex(packed10));

        var round = SmsCodecs.FromSeptets(SmsCodecs.UnpackSeptets(packed, 0, 5));
        Check("gsm7 roundtrip", round == "hello", round);

        // Full basic alphabet + extension chars roundtrip (canonical TS 23.038).
        var exotic = "\u0040\u00A3\u0024\u00A5\u00E8\u00E9\u00F9\u00EC\u00F2\u00C7\n\u00D8\u00F8\r\u00C5\u00E5\u0394_\u03A6\u0393\u039B\u03A9\u03A0\u03A8\u03A3\u0398\u039E\u00C6\u00E6\u00DF\u00C9 !\"#\u00A4%&'()*+,-./0123456789:;<=>?" +
                     "\u00A1ABCDEFGHIJKLMNOPQRSTUVWXYZ\u00C4\u00D6\u00D1\u00DC\u00A7\u00BFabcdefghijklmnopqrstuvwxyz\u00E4\u00F6\u00F1\u00FC\u00E0" +
                     "^{}\\[~]|\u20AC";
        var exSept = SmsCodecs.ToSeptets(exotic);
        var exBack = SmsCodecs.FromSeptets(SmsCodecs.UnpackSeptets(
            SmsCodecs.PackSeptets(exSept), 0, exSept.Length));
        if (exBack != exotic)
        {
            int k = 0;
            while (k < Math.Min(exBack.Length, exotic.Length) && exBack[k] == exotic[k]) k++;
            Console.WriteLine($"        alphabet mismatch @ {k}: got U+{(int)exBack[k]:X4} want U+{(int)exotic[k]:X4}");
        }
        Check("gsm7 full alphabet roundtrip", exBack == exotic);

        // Canonical spec vectors (TS 23.038 §6.1.1): row 0x1B is the ESCAPE
        // code point; 0x1C=AE, 0x1D=ae, 0x1E=ss, 0x1F=acute-E; the extension
        // table is {=ESC 0x28, }=ESC 0x29, [=ESC 0x3C, ]=ESC 0x3E, ~=ESC 0x3D,
        // ^=ESC 0x14, |=ESC 0x40, \=ESC 0x2F.
        var aeSept = SmsCodecs.ToSeptets("\u00C6");
        Check("gsm7 AE encodes as 0x1C", aeSept.SequenceEqual(new byte[] { 0x1C }),
            string.Join(' ', aeSept.Select(b => b.ToString("X2"))));
        var eAcuteSept = SmsCodecs.ToSeptets("\u00C9");
        Check("gsm7 E-acute encodes as 0x1F", eAcuteSept.SequenceEqual(new byte[] { 0x1F }),
            string.Join(' ', eAcuteSept.Select(b => b.ToString("X2"))));
        var braceSept = SmsCodecs.ToSeptets("{");
        Check("gsm7 '{' encodes as ESC 0x28", braceSept.SequenceEqual(new byte[] { 0x1B, 0x28 }),
            string.Join(' ', braceSept.Select(b => b.ToString("X2"))));
        Check("gsm7 inbound ESC 0x28 decodes to '{'",
            SmsCodecs.FromSeptets(new byte[] { 0x1B, 0x28 }) == "{",
            SmsCodecs.FromSeptets(new byte[] { 0x1B, 0x28 }));
        Check("gsm7 inbound 0x1F decodes to E-acute",
            SmsCodecs.FromSeptets(new byte[] { 0x1F }) == "\u00C9",
            SmsCodecs.FromSeptets(new byte[] { 0x1F }));
        Check("gsm7 reserved ESC+unknown renders '?'",
            SmsCodecs.FromSeptets(new byte[] { 0x1B, 0x1C }) == "?",
            SmsCodecs.FromSeptets(new byte[] { 0x1B, 0x1C }));

        Check("gsm7 detect ucs2", !SmsCodecs.IsGsm7Representable("h\u00E9llo \u2713"));
        Check("semi-octet 12345", SmsPdu.BytesToHex(SmsCodecs.EncodeSemiOctets("12345")) == "2143F5",
            SmsPdu.BytesToHex(SmsCodecs.EncodeSemiOctets("12345")));
        Check("semi-octet decode", SmsCodecs.DecodeSemiOctets(new byte[] { 0x21, 0x43, 0xF5 }) == "12345");

        var ts = new DateTimeOffset(2026, 9, 22, 14, 5, 9, TimeSpan.FromHours(1));
        var enc = SmsCodecs.EncodeTimestamp(ts);
        var dec = SmsCodecs.DecodeTimestamp(enc);
        Check("scts roundtrip", dec.Year == 2026 && dec.Month == 9 && dec.Day == 22 &&
                                dec.Hour == 14 && dec.Minute == 5 && dec.Second == 9 &&
                                dec.Offset == TimeSpan.FromHours(1),
            $"{dec:yyyy-MM-dd HH:mm:ss zzz}");

        // ---- '?' regression (live over-the-air defect, 2026-09-22) ----------------
        // The basic table used to carry '?' at the RESERVED septet 0x1F, shadowing
        // the real '?' at 0x3F in the first-wins lookup, so every outgoing '?'
        // went on the air as 0x1F. Recipients' decoders rendered it as '-'
        // ("E with a line at the top") - reproduced live over the air on Orange PL:
        // "hello?" was delivered as "hello-" (Windows' own chat-store decoder), as
        // were "why not-", "ok 123 - yes" and a 160-char sentence ending "right-".
        var qSep = SmsCodecs.ToSeptets("hello?");
        Check("gsm7 '?' encodes as 0x3F, never reserved 0x1F",
            qSep.SequenceEqual(new byte[] { 0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x3F }),
            string.Join(' ', qSep.Select(b => b.ToString("X2"))));
        // 6 septets = 42 bits = 6 octets; the defective build ended FE 00 (0x1F).
        Check("gsm7 'hello?' packed vector", SmsPdu.BytesToHex(SmsCodecs.PackSeptets(qSep)) == "E8329BFDFE01",
            SmsPdu.BytesToHex(SmsCodecs.PackSeptets(qSep)));
        // no text composed of ordinary characters may ever emit the ESC septet
        // (0x1B is the extension escape; 0x1F is E-acute in the canonical table)
        var noReserved = SmsCodecs.ToSeptets("hello? why not? ok 123 ? yes? really?");
        Check("gsm7 no escape septets in output", !noReserved.Contains((byte)0x1B));
    }

    // ---- PDU ---------------------------------------------------------------

    private static void PduTests()
    {
        Console.WriteLine("[pdu]");

        var (hex, octets) = SmsPdu.EncodeDeliver("+447700900123", "Signal check", new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero));
        var dec = SmsPdu.DecodeDeliver(hex);
        Check("deliver decode sender", dec.Sender == "+447700900123", dec.Sender);
        Check("deliver decode text", dec.Text == "Signal check", dec.Text);
        Check("deliver decode ts", dec.Timestamp.Year == 2026 && dec.Timestamp.Month == 9 && dec.Timestamp.Day == 22);

        var sub = SmsPdu.EncodeSubmit("+447700900123", "hello hello", 0x2A, 1, 1);
        var decS = SmsPdu.DecodeSubmit(sub.Hex);
        Check("submit decode dest", decS.Destination == "+447700900123", decS.Destination);
        Check("submit decode text", decS.Text == "hello hello", decS.Text);
        Check("submit tpdu octets", sub.TpduOctets == sub.Hex.Length / 2 - 1);

        // multipart submit - parts reassemble
        string longText = string.Concat(Enumerable.Repeat("part-fragment-0123456789abcdef", 24)); // 720 chars - 5 parts
        var chunks = Segmenter.Plan(longText).Parts;
        Check("long text is multipart", chunks.Count >= 5, $"parts={chunks.Count}");
        var parts = new List<string>();
        for (int i = 1; i <= 5; i++)
        {
            var seg = chunks[i - 1];
            var p = SmsPdu.EncodeSubmit("+447700900999", seg, 0x77, i, 5);
            var d = SmsPdu.DecodeSubmit(p.Hex);
            Check($"multipart submit part{i} meta", d.ConcatRef == 0x77 && d.ConcatTotal == 5 && d.ConcatSeq == i,
                $"ref={d.ConcatRef} total={d.ConcatTotal} seq={d.ConcatSeq}");
            parts.Add(d.Text);
        }
        Check("multipart reassembly", string.Concat(parts) == longText);

        // multipart deliver (sim bot path) - parts reassemble incl. UDH fill bits
        var deliverParts = new List<string>();
        for (int i = 1; i <= 3; i++)
        {
            var seg = chunks[i - 1];
            var (h, _) = SmsPdu.EncodeDeliver("+447700900123", seg, DateTimeOffset.Now, 0x42, 3, i);
            var d = SmsPdu.DecodeDeliver(h);
            Check($"multipart deliver part{i} meta", d.ConcatTotal == 3 && d.ConcatSeq == i,
                $"total={d.ConcatTotal} seq={d.ConcatSeq}");
            deliverParts.Add(d.Text);
        }
        Check("multipart deliver reassembly", string.Concat(deliverParts) == string.Concat(chunks.Take(3)));

        // UCS-2 path
        var (uh, _) = SmsPdu.EncodeDeliver("+447700900123", "h\u00E9llo w\u00F6rld \u2713", DateTimeOffset.Now);
        var ud = SmsPdu.DecodeDeliver(uh);
        Check("ucs2 deliver", ud.Text == "h\u00E9llo w\u00F6rld \u2713", ud.Text);

        var us = SmsPdu.EncodeSubmit("+447700900123", "\u043F\u0440\u0438\u0432\u0435\u0442", 1, 1, 1);
        var usd = SmsPdu.DecodeSubmit(us.Hex);
        Check("ucs2 submit", usd.Text == "\u043F\u0440\u0438\u0432\u0435\u0442", usd.Text);
        // emoji (non-BMP, surrogate pair) rides UCS-2 untouched end-to-end
        var emoji = "ok \U0001F600 fire \U0001F525";
        var es = SmsPdu.EncodeSubmit("+447700900123", emoji, 1, 1, 1);
        var esd = SmsPdu.DecodeSubmit(es.Hex);
        Check("ucs2 emoji roundtrip", esd.Text == emoji, esd.Text);
        var eplan = Segmenter.Plan(emoji);
        Check("emoji uses UCS2", eplan.UseUcs2, eplan.UseUcs2.ToString());

        // ---- '?' PDU regression vectors (2026-09-22 over-the-air defect) ---------
        // Exact deterministic submit PDU for "hello?", hand-decoded bit-by-bit
        // against GSM 03.40: TPDU 01 00 0B 91 8421436587F9 00 00 06 with UD
        // E8 32 9B FD FE 01, which unpacks LSB-first to 68 65 6C 6C 6F 3F =
        // h e l l o ?. The defective build emitted UD ...FE 00 (septet 0x1F,
        // reserved) and real handsets displayed "hello-" - live-verified.
        var q = SmsPdu.EncodeSubmit("+48123456789", "hello?", 0x2A, 1, 1);
        Check("submit 'hello?' exact PDU hex",
            q.Hex == "0001000B918421436587F9000006E8329BFDFE01", q.Hex);
        Check("submit 'hello?' tpdu octets", q.TpduOctets == 19, q.TpduOctets.ToString());
        var qd = SmsPdu.DecodeSubmit(q.Hex);
        Check("submit 'hello?' decodes back", qd.Text == "hello?" && qd.Destination == "+48123456789", qd.Text);

        // mid-message '?': live matrix sent "ok 123 ? yes", delivered "ok 123 - yes"
        var mid = SmsPdu.EncodeSubmit("+48123456789", "ok 123 ? yes", 1, 1, 1);
        var midD = SmsPdu.DecodeSubmit(mid.Hex);
        Check("submit 'ok 123 ? yes' roundtrip", midD.Text == "ok 123 ? yes", midD.Text);
        var wn = SmsPdu.EncodeSubmit("+48123456789", "why not?", 1, 1, 1);
        Check("submit 'why not?' roundtrip", SmsPdu.DecodeSubmit(wn.Hex).Text == "why not?",
            SmsPdu.DecodeSubmit(wn.Hex).Text);

        // message ending in '?' exactly at a multipart part boundary: 183 septets
        // split 153+30, so part 1 is a full 153-septet part whose LAST septet is
        // '?'. UDL must be 0xA0 (7 UDH septets + 153) and the final UD octet 0x7F
        // (fill bit + 'a'.b6 in bit 0, '?' = 0x3F in bits 1..7 - after the 6-octet
        // concat UDH the septet stream is offset by one fill bit). The defective
        // build produced tail 0x3F here (1 | 0x1F<<1): the trailing '?' silently
        // became the reserved code point at the segment boundary.
        string boundaryText = new string('a', 152) + "?" + new string('b', 30);
        var bplan = Segmenter.Plan(boundaryText);
        Check("boundary text splits 2 parts, part1 ends '?' at septet 153",
            bplan.Total == 2 && SmsCodecs.SeptetCount(bplan.Parts[0]) == 153 && bplan.Parts[0][^1] == '?',
            $"parts={bplan.Total}");
        var bp1 = SmsPdu.EncodeSubmit("+48123456789", bplan.Parts[0], 0x77, 1, 2);
        Check("boundary part1 exact PDU hex",
            bp1.Hex == "0041000B918421436587F90000A0050003770201C2" +
                       string.Concat(Enumerable.Repeat("E170381C0E87C3", 18)) + "E170381C0E877F",
            bp1.Hex);
        var bd1 = SmsPdu.DecodeSubmit(bp1.Hex);
        Check("boundary part1 roundtrip",
            bd1.Text == bplan.Parts[0] && bd1.ConcatSeq == 1 && bd1.ConcatTotal == 2 && bd1.ConcatRef == 0x77,
            $"len={bd1.Text.Length} last='{bd1.Text[^1]}' seq={bd1.ConcatSeq}/{bd1.ConcatTotal}");
        var bd2 = SmsPdu.DecodeSubmit(SmsPdu.EncodeSubmit("+48123456789", bplan.Parts[1], 0x77, 2, 2).Hex);
        Check("boundary reassembly", bd1.Text + bd2.Text == boundaryText);

        // single-segment 160-septet sentence ending in '?': 140 packed octets, and
        // the final octet must be 0x7F ('t'.b6 | 0x3F<<1). The defective build
        // packed 0x3F ('t'.b6 | 0x1F<<1) - live-delivered as "-right-".
        string s160 = "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs " +
                      "0123456789. How vexingly quick daft zebras jump over this modem link right?";
        var s160Packed = SmsCodecs.PackSeptets(SmsCodecs.ToSeptets(s160));
        Check("160-septet sentence trailing '?' tail octet 0x7F",
            s160.Length == 160 && s160Packed.Length == 140 && s160Packed[^1] == 0x7F,
            $"{s160.Length} chars, {s160Packed.Length} octets, tail {SmsPdu.BytesToHex(s160Packed[^2..])}");
    }

    private static List<string> SegmenterChunks(string text) => Segmenter.Plan(text).Parts;

    // ---- segmenter ---------------------------------------------------------

    private static void SegmenterTests()
    {
        Console.WriteLine("[segmenter]");

        Check("single gsm7", Segmenter.Plan("short").Total == 1);
        Check("160 gsm7 = 1 seg", Segmenter.Plan(new string('a', 160)).Total == 1);
        Check("161 gsm7 = 2 seg", Segmenter.Plan(new string('a', 161)).Total == 2);
        Check("ucs2 single", Segmenter.Plan("h\u00E9llo \u2713").UseUcs2 && Segmenter.Plan("h\u00E9llo \u2713").Total == 1);
        var u70 = Segmenter.Plan(new string('\u2713', 70));
        var u71 = Segmenter.Plan(new string('\u2713', 71));
        Check("ucs2 70 = 1 seg", u70.UseUcs2 && u70.Total == 1);
        Check("ucs2 71 = 2 seg", u71.UseUcs2 && u71.Total == 2);
        // ext chars cost 2 septets: 80 braces must not fit one 153-septet part per-part
        var braces = new string('{', 100); // 200 septets - 2 parts
        var bp = Segmenter.Plan(braces);
        Check("ext-char septet budget", bp.Total == 2 && bp.Parts.All(p => SmsCodecs.SeptetCount(p) <= 153),
            $"total={bp.Total}");
    }

    // ---- end-to-end over the simulated modem --------------------------------

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    private static async Task ServiceTests()
    {
        Console.WriteLine("[service over simulated modem]");

        var sim = new SimulatedModemChannel { Scenario = SimScenario.SimReady };
        using var service = new SmsService(sim);

        var received = new System.Collections.Concurrent.ConcurrentQueue<SmsIncomingMessage>();
        service.MessageReceived += received.Enqueue;

        var connected = await service.ConnectAsync();
        Check("connect", connected);
        Check("sim ready", service.Status.Sim == SimState.Ready, service.Status.Sim.ToString());
        Check("net home", service.Status.Net == NetState.Home, service.Status.Net.ToString());
        Check("rssi known", service.Status.Rssi is >= 0 and <= 31, service.Status.Rssi.ToString());
        Check("operator read", service.Status.OperatorName == "SimNet", service.Status.OperatorName);

        // No-SIM scenario matches the machine's current real state
        var simNo = new SimulatedModemChannel { Scenario = SimScenario.NoSim, AutoReply = false };
        using var svcNo = new SmsService(simNo);
        await svcNo.ConnectAsync();
        Check("no-sim detected", svcNo.Status.Sim == SimState.Absent, svcNo.Status.Sim.ToString());
        var refused = await svcNo.SendAsync("+447700900000", "should fail");
        Check("send refused without sim", !refused.Success && refused.Detail.Contains("CMS ERROR: 3"), refused.Detail);

        // Roundtrip: send - bot reply; every 3rd bot reply is long/multipart.
        var send1 = await service.SendAsync("+447700900123", "Smoke test message one");
        Check("send ok", send1.Success, send1.Detail);

        var gotFirst = await WaitFor(() => !received.IsEmpty, 15000);
        SmsIncomingMessage? first = received.ToArray().FirstOrDefault();
        Check("bot reply received", gotFirst && first is not null);
        if (first is not null)
        {
            Check("bot reply sender", first.Sender == "+447700900123", first.Sender);
            Check("bot reply text sane", first.Text.Length > 0, first.Text);
        }

        // Drive more traffic until the simulator's multipart LONG-FRAME reply lands.
        await service.SendAsync("+447700900456", "drive 1");
        await service.SendAsync("+447700900456", "drive 2");
        var gotLong = await WaitFor(
            () => received.Any(m => m.Text.Contains("LONG-FRAME TEST", StringComparison.Ordinal)), 20000);
        SmsIncomingMessage? longMsg = received.FirstOrDefault(m => m.Text.Contains("LONG-FRAME TEST", StringComparison.Ordinal));
        Check("multipart inbound reassembled", gotLong && longMsg is not null && longMsg.Text.Length > 160,
            longMsg is null ? "(timeout)" : $"{longMsg.Text.Length} chars");

        // Direct injection path (used by tests/demos without a send)
        sim.InjectInbound("+447700999000", "direct inject");
        var gotInj = await WaitFor(() => received.Any(m => m.Sender == "+447700999000"), 10000);
        SmsIncomingMessage? inj = received.FirstOrDefault(m => m.Sender == "+447700999000");
        Check("injected inbound", gotInj && inj is not null && inj.Text == "direct inject",
            inj is null ? "(timeout)" : $"{inj.Sender}: {inj.Text}");

        // Store behaves and persists fields used by the UI
        var store = new MessageStore(Path.Combine(Path.GetTempPath(), $"lw-smoke-{Guid.NewGuid():N}.json"));
        var om = store.AddOutbound("+447700900123", null, "Outbound probe");
        store.UpdateMessage(om, MessageStatus.Sent, 42);
        store.AddInbound("+447700900123", "Network Bot", "Inbound probe", DateTime.UtcNow);
        store.MarkRead(store.Conversations.First(c => c.Number == "+447700900123"));
        var conv = store.Conversations.Single(c => c.Number == "+447700900123");
        Check("store statuses", conv.Messages[0].Status == MessageStatus.Sent && conv.Messages[0].ModemRef == 42 &&
                               conv.Messages[1].Status == MessageStatus.Received && conv.UnreadCount == 0);

        // duplicate handling lives in the backends (exact message IDs), so the
        // store itself must record repeated identical texts faithfully - the
        // old 5-minute text heuristic ate legitimate rapid-fire repeats
        var dup1 = store.AddInbound("+447700900787", null, "ok", DateTime.UtcNow);
        var dup2 = store.AddInbound("+447700900787", null, "ok", DateTime.UtcNow.AddSeconds(1));
        Check("store keeps identical rapid repeats", dup1 is not null && dup2 is not null);
        store.Dispose();
    }
}
