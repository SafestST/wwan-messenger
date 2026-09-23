using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using WwanMessenger.Core.Mbn;
using WwanMessenger.Core.Sms;

// ---------------------------------------------------------------------------
// mbncli — standalone Mobile Broadband SMS bridge (diagnostics / scripting).
// The same functionality now lives in the WwanMessenger GUI via MbnSmsService;
// this CLI remains for bring-up work.
//
//   mbncli                        status dump
//   mbncli send <number> <text>   send an SMS (PDU mode)
//   mbncli read                   dump the message store (raw PDU + decoded)
//   mbncli signal [seconds]       count driver-pushed signal events vs polls
//   mbncli watch [seconds]        live watch for inbound SMS
//   mbncli monitor [seconds]      single-flight live monitor
//   mbncli probe                  read-combination probe
// ---------------------------------------------------------------------------

internal static class Program
{
    private static readonly Guid IidImbnSmsEvents = new("DCBBBAB6-2016-4BBB-AAEE-338E368AF6FA");
    private const int SmsFormatPdu = 1;

    [MTAThread]
    private static int Main(string[] args)
    {
        Console.WriteLine("wwan-messenger mbncli — Windows Mobile Broadband SMS bridge\n");

        IMbnInterface iface;
        IMbnInterfaceManager mgr;
        try
        {
            mgr = (IMbnInterfaceManager)new MbnInterfaceManagerCoClass();
            mgr.GetInterfaces(out var ifaces);
            if (ifaces is null || ifaces.Length == 0)
            {
                Console.WriteLine("no mobile broadband interfaces found");
                return 1;
            }
            Console.WriteLine($"interfaces: {ifaces.Length}");
            iface = ifaces[0];
        }
        catch (COMException ex)
        {
            Console.WriteLine($"failed to reach the Mobile Broadband service: 0x{ex.HResult:X8} — {ex.Message}");
            return 1;
        }

        var sms = (IMbnSms)iface;
        var signal = (IMbnSignal)iface;

        string? smsc = null;
        try
        {
            iface.get_InterfaceID(out var id);
            Console.WriteLine($"interface id    : {id}");
        }
        catch (COMException ex) { Console.WriteLine($"interface id    : error 0x{ex.HResult:X8}"); }

        try
        {
            iface.GetInterfaceCapability(out var caps);
            Console.WriteLine($"modem           : {caps.Manufacturer} {caps.Model} (fw {caps.FirmwareInfo})");
            Console.WriteLine($"SMS caps        : 0x{caps.SmsCaps:X} (PDU send: {(caps.SmsCaps & 2) != 0}, PDU receive: {(caps.SmsCaps & 1) != 0})");
        }
        catch (COMException ex) { Console.WriteLine($"capability      : error 0x{ex.HResult:X8} — {ex.Message}"); }

        try
        {
            iface.GetSubscriberInformation(out var sub);
            sub.get_SubscriberID(out var imsi);
            sub.get_SimIccID(out var iccid);
            sub.get_TelephoneNumbers(out var numbers);
            Console.WriteLine($"IMSI            : {imsi}");
            Console.WriteLine($"ICCID           : {iccid}");
            Console.WriteLine($"MSISDN (SIM no) : {(numbers is { Length: > 0 } ? string.Join(", ", numbers) : "(empty)")}");
        }
        catch (COMException ex) { Console.WriteLine($"subscriber info : error 0x{ex.HResult:X8} — {ex.Message}"); }

        try
        {
            signal.GetSignalStrength(out var rssi);
            Console.WriteLine($"signal strength : {rssi} (0..31, 99 = unknown)");
        }
        catch (COMException) { Console.WriteLine("signal           : unavailable"); }

        try
        {
            sms.GetSmsConfiguration(out var cfg);
            cfg.get_ServiceCenterAddress(out smsc);
            cfg.get_MaxMessageIndex(out var maxIdx);
            cfg.get_SmsFormat(out var fmt);
            Console.WriteLine($"SMSC            : {smsc}");
            Console.WriteLine($"SMS format      : {fmt} (1 = PDU)");
            Console.WriteLine($"message slots   : {maxIdx}");
            sms.GetSmsStatus(out var st);
            Console.WriteLine($"SMS status flags: {st.Flag}, last used index {st.Index}");
        }
        catch (COMException ex)
        {
            Console.WriteLine($"SMS configuration unavailable: 0x{ex.HResult:X8} — {ex.Message}");
        }

        if (args.Length == 0)
            return 0;

        if (args[0] == "signal")
            return SignalProbe(mgr, iface, signal, args.Length > 1 && int.TryParse(args[1], out var sigSec) ? sigSec : 60);
        if (args[0] == "chat")
            return ChatDump(args.Length > 1 && int.TryParse(args[1], out var chatN) ? chatN : 25);

        var sink = new SmsEventsSink();
        sink.EventLogged += line => Console.WriteLine($"    [{DateTime.Now:HH:mm:ss.fff}] {line}");
        var cpc = (IConnectionPointContainer)mgr;
        var eventsIid = IidImbnSmsEvents;
        cpc.FindConnectionPoint(ref eventsIid, out var cp);
        if (cp is null) { Console.WriteLine("connection point unavailable"); return 1; }
        cp.Advise(sink, out var cookie);

        try
        {
            if (args[0] == "monitor") return Monitor(sink, sms, args.Length > 1 && int.TryParse(args[1], out var sec) ? sec : 300);
            if (args[0] == "probe") return ProbeReads(sink, sms);
            if (args[0] == "read") return ReadStore(sink, sms);
            if (args[0] == "watch") return Watch(sink, sms, args.Length > 1 && int.TryParse(args[1], out var s) ? s : 180);
            if (args[0] != "send") return 0;
            return Send(sink, sms, smsc, args);
        }
        finally
        {
            cp.Unadvise(cookie);
        }
    }

    private static int Send(SmsEventsSink sink, IMbnSms sms, string? smsc, string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("usage: mbncli send <number> <message>");
            return 2;
        }
        string number = args[1];
        string text = string.Join(' ', args.Skip(2));

        var submit = SmsPdu.EncodeSubmit(number, text, Random.Shared.Next(256), 1, 1);

        string fullPdu;
        if (!string.IsNullOrEmpty(smsc) && smsc.StartsWith('+'))
        {
            var digits = new string(smsc.Where(char.IsDigit).ToArray());
            var semi = SmsPdu.BytesToHex(SmsCodecs.EncodeSemiOctets(digits));
            fullPdu = (1 + semi.Length / 2).ToString("X2") + "91" + semi + submit.Hex[2..];
        }
        else
        {
            fullPdu = submit.Hex;
        }

        Console.WriteLine($"\ndestination : {number}");
        Console.WriteLine($"pdu         : {fullPdu}");
        Console.WriteLine($"tpdu octets : {submit.TpduOctets}");

        // readiness kick, then send with retries
        if (!sink.ConfigurationEvent.WaitOne(TimeSpan.FromSeconds(8)))
        {
            try
            {
                sms.GetSmsConfiguration(out var cfg);
                cfg.get_ServiceCenterAddress(out var sc);
                cfg.put_ServiceCenterAddress(sc);
                sms.SetSmsConfiguration(cfg, out _);
            }
            catch (COMException) { }
        }

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            Console.WriteLine($"\nsend attempt {attempt}/2");
            sink.SendCompleted.Reset();
            sink.SendStatus = int.MinValue;
            sink.SendInFlight = true;
            uint requestID;
            try
            {
                sms.SmsSendPdu(fullPdu, (byte)submit.TpduOctets, out requestID);
                sink.ExpectedRequestId = requestID;
            }
            catch (COMException ex)
            {
                sink.SendInFlight = false;
                Console.WriteLine($"SmsSendPdu rejected: 0x{ex.HResult:X8} — {ex.Message}");
                return 3;
            }
            Console.WriteLine($"request {requestID} accepted, waiting on network—");

            if (!sink.SendCompleted.WaitOne(TimeSpan.FromSeconds(90)))
            {
                sink.SendInFlight = false;
                Console.WriteLine("OUTCOME UNKNOWN — no completion within 90 s; NOT resending (message may have been delivered).");
                return 4;
            }
            sink.SendInFlight = false;
            if (sink.SendStatus == 0)
            {
                Console.WriteLine("\nSEND COMPLETE — the modem accepted the message for delivery (S_OK).");
                return 0;
            }
            Console.WriteLine($"attempt failed — status 0x{sink.SendStatus:X8}");
            if (attempt < 2) Thread.Sleep(5000);
        }
        return 3;
    }

    private static int ReadStore(SmsEventsSink sink, IMbnSms sms)
    {
        int found;
        sink.ReadCompleted.Reset();
        var filter = new MbnSmsFilter { Flag = 0, Index = 0 };
        try { sms.SmsRead(ref filter, SmsFormatPdu, out _); }
        catch (COMException ex)
        {
            Console.WriteLine($"SmsRead rejected: 0x{ex.HResult:X8}");
            return 1;
        }
        if (!sink.ReadCompleted.WaitOne(TimeSpan.FromSeconds(75)))
        {
            Console.WriteLine("SmsRead timed out");
            return 1;
        }
        if (sink.ReadStatus != 0)
        {
            Console.WriteLine($"SmsRead status 0x{sink.ReadStatus:X8} (empty store reports an error on this firmware)");
            return 0;
        }
        found = Dump(sink.ReadMessages, "store");
        if (found == 0) Console.WriteLine("\nno messages found");
        return 0;
    }

    private static int Watch(SmsEventsSink sink, IMbnSms sms, int seconds)
    {
        Console.WriteLine($"\nwatching for inbound SMS for {seconds}s — text the SIM now—");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (!sink.StoreChangedEvent.WaitOne(TimeSpan.FromSeconds(2))) continue;
            sink.StoreChanged = false;
            sink.StoreChangedEvent.Reset();
            Thread.Sleep(500);
            sink.ReadCompleted.Reset();
            var filter = new MbnSmsFilter { Flag = 0, Index = 0 };
            try { sms.SmsRead(ref filter, SmsFormatPdu, out _); }
            catch (COMException ex) { Console.WriteLine($"    SmsRead rejected: 0x{ex.HResult:X8}"); continue; }
            if (!sink.ReadCompleted.WaitOne(TimeSpan.FromSeconds(50))) continue;
            if (sink.ReadStatus != 0)
            {
                Console.WriteLine("    read: empty");
                continue;
            }
            Dump(sink.ReadMessages, "live");
        }
        Console.WriteLine("\nwatch window elapsed");
        return 0;
    }

    /// <summary>
    /// Single-flight live monitor: ONE client, ONE read in flight at a time,
    /// completions waited out to 120 s (this firmware answers slowly and its
    /// completion IDs are unreliable — stale completions must never be
    /// mistaken for fresh ones). Store status snapshot every event and every
    /// 30 s so index movement is visible even without reads succeeding.
    /// </summary>
    private static int Monitor(SmsEventsSink sink, IMbnSms sms, int seconds)
    {
        Console.WriteLine($"\nMONITOR for {seconds}s — TEXT THE SIM NOW (+48123456789). One read in flight at a time.");
        var swTotal = Stopwatch.StartNew();
        uint lastIndex = uint.MaxValue;

        void Snapshot(string why)
        {
            try
            {
                sms.GetSmsStatus(out var st);
                string moved = lastIndex != uint.MaxValue && st.Index != lastIndex ? $"  <<< INDEX MOVED {lastIndex} -> {st.Index}" : "";
                Console.WriteLine($"    [{DateTime.Now:HH:mm:ss}] store({why}): flag={st.Flag} lastUsedIndex={st.Index}{moved}");
                lastIndex = st.Index;
            }
            catch (COMException ex) { Console.WriteLine($"    store status error 0x{ex.HResult:X8}"); }
        }

        bool SingleRead(int msgStatus, string label)
        {
            sink.ReadCompleted.Reset();
            sink.ReadStatus = int.MinValue;
            sink.ReadMessages = null;
            var filter = new MbnSmsFilter { Flag = 0, Index = 0 };
            try { sms.SmsRead(ref filter, 1 /*PDU*/, out _); }
            catch (COMException ex) { Console.WriteLine($"    SmsRead({label}) rejected 0x{ex.HResult:X8}"); return false; }

            var sw = Stopwatch.StartNew();
            while (!sink.ReadCompleted.WaitOne(500))
            {
                if (sw.ElapsedMilliseconds < 120_000) continue;
                Console.WriteLine($"    SmsRead({label}) NO completion in 120 s");
                return false;
            }
            Console.WriteLine($"    SmsRead({label}) completed in {sw.ElapsedMilliseconds / 1000.0:0.0}s — status 0x{sink.ReadStatus:X8}, count={sink.ReadMessages?.Length ?? 0}");
            // drain any straggler completion from a previous request
            sink.ReadCompleted.Reset();
            sink.ReadCompleted.WaitOne(1500);
            if ((sink.ReadMessages?.Length ?? 0) > 0) Dump(sink.ReadMessages, label);
            return (sink.ReadMessages?.Length ?? 0) > 0;
        }

        var nextSnap = TimeSpan.FromSeconds(0);
        while (swTotal.Elapsed.TotalSeconds < seconds)
        {
            if (sink.StoreChangedEvent.WaitOne(500))
            {
                sink.StoreChanged = false;
                sink.StoreChangedEvent.Reset();
                Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] STORE CHANGE — reading—");
                Thread.Sleep(1500);               // let the store settle
                Snapshot("on-change");
                if (SingleRead(0, "NEW")) continue;
                SingleRead(1, "OLD");
                Snapshot("post-read");
                nextSnap = TimeSpan.FromSeconds(swTotal.Elapsed.TotalSeconds + 30);
            }
            else if (swTotal.Elapsed >= nextSnap)
            {
                Snapshot("periodic");
                nextSnap = TimeSpan.FromSeconds(swTotal.Elapsed.TotalSeconds + 30);
            }
        }
        Console.WriteLine("\nmonitor window elapsed");
        return 0;
    }

    private static int ProbeReads(SmsEventsSink sink, IMbnSms sms)
    {
        Console.WriteLine("\nprobing read combinations—");
        try
        {
            sms.GetSmsStatus(out var st);
            Console.WriteLine($"store status: flag={st.Flag} lastUsedIndex={st.Index}");
        }
        catch (COMException ex) { Console.WriteLine($"store status: 0x{ex.HResult:X8}"); }

        int hits = 0;

        bool TryRead(int msgStatus, uint index, int format, string label)
        {
            sink.ReadCompleted.Reset();
            var filter = new MbnSmsFilter { Flag = msgStatus, Index = index };
            try { sms.SmsRead(ref filter, format, out _); }
            catch (COMException ex)
            {
                Console.WriteLine($"  {label,-28} rejected 0x{ex.HResult:X8}");
                return false;
            }
            if (!sink.ReadCompleted.WaitOne(TimeSpan.FromSeconds(55)))
            {
                Console.WriteLine($"  {label,-28} TIMEOUT");
                return false;
            }
            if (sink.ReadStatus != 0)
            {
                Console.WriteLine($"  {label,-28} status 0x{sink.ReadStatus:X8}");
                return false;
            }
            var n = sink.ReadMessages?.Length ?? 0;
            Console.WriteLine($"  {label,-28} OK — {n} message(s)");
            if (n > 0) Dump(sink.ReadMessages, label);
            return n > 0;
        }

        string[] names = { "NEW", "OLD", "DRAFT", "SENT" };
        for (int s = 0; s < 4; s++)
        {
            if (TryRead(s, 0, 1, $"PDU {names[s]} (no index)")) hits++;
        }
        for (int s = 0; s < 4; s++)
        {
            if (TryRead(s, 1, 1, $"PDU {names[s]} index=1")) hits++;
        }
        // sample a spread of indexes on NEW and OLD
        foreach (uint idx in new uint[] { 2, 5, 10, 15, 20, 25 })
        {
            if (TryRead(0, idx, 1, $"PDU NEW index={idx}")) hits++;
        }
        // text-format reads (driver claims CDMA class oddly — try it)
        for (int s = 0; s < 4; s++)
        {
            if (TryRead(s, 0, 2, $"TEXT {names[s]}")) hits++;
        }

        try
        {
            sms.GetSmsStatus(out var st2);
            Console.WriteLine($"store status after: flag={st2.Flag} lastUsedIndex={st2.Index}");
        }
        catch { }

        Console.WriteLine($"\nprobe complete — {hits} combination(s) returned messages");
        return 0;
    }

    /// <summary>
    /// Dump the last N messages from the Windows Chat store — the receive
    /// channel WwanSvc routes inbound MBIM SMS into. The body text is decoded
    /// by WINDOWS' OWN GSM-7 decoder, i.e. an independent renderer's view of
    /// what actually went over the air (ground truth for codec audits).
    /// </summary>
    private static int ChatDump(int count)
    {
        var store = Windows.ApplicationModel.Chat.ChatMessageManager
            .RequestStoreAsync().GetAwaiter().GetResult();
        var reader = store.GetMessageReader();
        var all = new List<Windows.ApplicationModel.Chat.ChatMessage>();
        for (int page = 0; page < 20; page++)
        {
            var batch = reader.ReadBatchAsync().GetAwaiter().GetResult();
            if (batch.Count == 0) break;
            all.AddRange(batch);
            if (all.Count >= 1000) break;
        }
        var recent = all.Where(m => !string.IsNullOrEmpty(m.From))
                        .OrderByDescending(m => m.LocalTimestamp)
                        .Take(count)
                        .ToList();
        Console.WriteLine($"\nchat store — {recent.Count} most recent message(s) (decoded by Windows' own SMS stack):");
        foreach (var m in recent)
        {
            string dir = m.IsIncoming ? "IN " : "OUT";
            Console.WriteLine($"  [{m.LocalTimestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}] {dir} {m.From}: {m.Body}");
        }
        if (recent.Count == 0) Console.WriteLine("  (empty)");
        return 0;
    }

    /// <summary>
    /// Signal-event probe: advises IMbnSignalEvents on BOTH the interface-manager
    /// connection point (what MbnSmsService uses) and the interface object's own
    /// connection point, then counts OnSignalStateChange pushes against periodic
    /// GetSignalStrength polls. Answers whether the driver pushes signal events
    /// on this hardware or the meter can only be poll-driven.
    /// </summary>
    private static int SignalProbe(IMbnInterfaceManager mgr, IMbnInterface iface, IMbnSignal signal, int seconds)
    {
        Console.WriteLine($"\nSIGNAL PROBE {seconds}s — counting driver-pushed OnSignalStateChange events vs polled RSSI—");
        var sigIid = new Guid("DCBBBAB6-2004-4BBB-AAEE-338E368AF6FA");

        int mgrPushes = 0, ifPushes = 0;
        var mgrSink = new SignalEventsSink();
        var ifSink = new SignalEventsSink();
        mgrSink.SignalChanged += () =>
        {
            int n = Interlocked.Increment(ref mgrPushes);
            uint r = 99;
            try { signal.GetSignalStrength(out r); } catch { }
            Console.WriteLine($"    [{DateTime.Now:HH:mm:ss.fff}] manager-CP push #{n} (rssi={r})");
        };
        ifSink.SignalChanged += () =>
        {
            int n = Interlocked.Increment(ref ifPushes);
            uint r = 99;
            try { signal.GetSignalStrength(out r); } catch { }
            Console.WriteLine($"    [{DateTime.Now:HH:mm:ss.fff}] interface-CP push #{n} (rssi={r})");
        };

        IConnectionPoint? mgrCp = null, ifCp = null;
        int mgrCookie = 0, ifCookie = 0;
        try
        {
            var cpc = (IConnectionPointContainer)mgr;
            cpc.FindConnectionPoint(ref sigIid, out mgrCp);
            if (mgrCp is not null) mgrCp.Advise(mgrSink, out mgrCookie);
        }
        catch (Exception ex) { Console.WriteLine($"manager connection point unavailable: {ex.Message}"); }
        try
        {
            var cpc = (IConnectionPointContainer)iface;
            cpc.FindConnectionPoint(ref sigIid, out ifCp);
            if (ifCp is not null) ifCp.Advise(ifSink, out ifCookie);
        }
        catch (Exception ex) { Console.WriteLine($"interface connection point unavailable: {ex.Message}"); }
        Console.WriteLine($"advised — manager-CP: {(mgrCp is not null ? "yes" : "no")}, interface-CP: {(ifCp is not null ? "yes" : "no")}");

        var sw = Stopwatch.StartNew();
        int polls = 0;
        uint last = uint.MaxValue;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(5000);
            try
            {
                signal.GetSignalStrength(out var r);
                polls++;
                string moved = r != last ? "  <<< CHANGED" : " (unchanged)";
                Console.WriteLine($"    [{DateTime.Now:HH:mm:ss}] poll #{polls}: rssi={r}{(last == uint.MaxValue ? "" : moved)}");
                last = r;
            }
            catch (COMException ex) { Console.WriteLine($"    [{DateTime.Now:HH:mm:ss}] poll failed 0x{ex.HResult:X8}"); }
        }

        try { if (ifCp is not null && ifCookie != 0) ifCp.Unadvise(ifCookie); } catch { }
        try { if (mgrCp is not null && mgrCookie != 0) mgrCp.Unadvise(mgrCookie); } catch { }

        Console.WriteLine($"\nRESULT: {mgrPushes} manager-CP push(es), {ifPushes} interface-CP push(es) in {seconds}s vs {polls} polls.");
        if (mgrPushes + ifPushes > 0)
            Console.WriteLine("VERDICT: the driver DOES push signal-state events on this hardware (event-driven meter is live).");
        else
            Console.WriteLine("VERDICT: the driver pushes NO signal events — the meter is poll-only on this hardware.");
        return 0;
    }

    private static int Dump(Array? messages, string label)
    {
        if (messages is not { Length: > 0 }) return 0;
        int n = 0;
        foreach (var element in messages)
        {
            try
            {
                var msg = (IMbnSmsReadMsgPdu)element;
                msg.get_Index(out var idx);
                msg.get_PduData(out var pduHex);
                Console.WriteLine($"\n--- slot {idx} [{label}] ---");
                Console.WriteLine($"pdu  : {pduHex}"); // RAW store contents — ground truth, pre-decode
                var deliver = SmsPdu.DecodeDeliver(pduHex);
                Console.WriteLine($"from : {deliver.Sender}");
                Console.WriteLine($"time : {deliver.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"text : {deliver.Text}");
                n++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    decode failed: {ex.Message}");
            }
        }
        return n;
    }
}
