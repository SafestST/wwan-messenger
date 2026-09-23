using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using WwanMessenger.Core.At;
using WwanMessenger.Core.Modem;
using WwanMessenger.Core.Sms;

namespace WwanMessenger.Core.Mbn;

/// <summary>
/// Real-modem backend over the Windows Mobile Broadband COM API (WwanSvc/MBIM).
/// All COM work runs on a dedicated MTA thread with a serialised work queue;
/// events are raised on that thread (or RPC threads for callbacks) — the UI
/// marshals. Quirks handled per live testing on the Quectel EM120R-GL:
/// reads take ~30–45 s, empty stores report 0x80548222 as an error, and
/// completion requestIDs for reads don't always match the out-param.
/// </summary>
public sealed class MbnSmsService : ISmsBackend
{
    private static readonly Guid IidImbnSmsEvents = new("DCBBBAB6-2016-4BBB-AAEE-338E368AF6FA");
    private const int SmsFormatPdu = 1;

    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private Thread? _comThread;
    private readonly System.Threading.Timer _pollTimer;
    private readonly MultipartReassembler _reassembler = new();
    private readonly HashSet<string> _seenPdus = new();
    private readonly object _seenLock = new();

    private IMbnInterfaceManager? _mgr;
    private IMbnSms? _sms;
    private IMbnSignal? _signal;
    private IMbnRegistration? _registration;
    private IConnectionPoint? _cp;
    private int _cookie;
    private string? _smsc;
    private volatile bool _connected;
    private volatile bool _sweepQueued;
    /// <summary>Consecutive poll failures — 3+ means the COM objects are dead
    /// (WwanSvc bounce / device re-enumeration) and the supervisor must
    /// recreate the whole backend rather than keep retrying a corpse.</summary>
    private int _pollFailures;
    /// <summary>Sends waiting in the queue — sweeps poll this and yield early
    /// so a send never sits behind a 60-second store read.</summary>
    private volatile int _sendsPending;

    public ModemStatus Status { get; } = new();

    public event Action<SmsIncomingMessage>? MessageReceived;
    public event Action? StatusChanged;
    public event Action<AtLineKind, string>? Logged;

    /// <summary>Receive channel: WwanSvc routes inbound SMS into the Windows
    /// Chat store on most modern stacks — the modem-store sweep then sees an
    /// always-empty store and stays disabled. On stacks where the Chat store
    /// is unavailable (some vendor firmware routes SMS differently), the
    /// sweep becomes the receive channel automatically.</summary>
    private ChatStoreReceiver? _chatReceiver;
    private volatile bool _modemStoreSweepEnabled;

    public MbnSmsService()
    {
        _pollTimer = new System.Threading.Timer(_ => _queue.Add(PollStatus), null, 10000, 10000);
    }

    /// <summary>Quick availability probe (any mobile broadband interface present?).</summary>
    public static bool IsAvailable()
    {
        try
        {
            var mgr = (IMbnInterfaceManager)new MbnInterfaceManagerCoClass();
            mgr.GetInterfaces(out var ifaces);
            return ifaces is { Length: > 0 };
        }
        catch { return false; }
    }

    public async Task<bool> ConnectAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_comThread is { IsAlive: true })
        {
            _queue.Add(() => tcs.TrySetResult(_connected));
            return await tcs.Task.ConfigureAwait(false);
        }

        // receive channel: Windows chat store when the platform routes SMS
        // there; modem-store sweep as the automatic fallback otherwise
        _chatReceiver = new ChatStoreReceiver();
        _chatReceiver.MessageReceived += m => MessageReceived?.Invoke(m);
        _chatReceiver.Logged += line => Log(AtLineKind.Rx, line);
        var chatOk = await _chatReceiver.StartAsync().ConfigureAwait(false);
        _modemStoreSweepEnabled = !chatOk;
        Log(AtLineKind.Info, chatOk
            ? "receive mode: Windows chat store"
            : "receive mode: modem-store sweep (chat store unavailable)");

        _comThread = new Thread(() => ComMain(tcs)) { IsBackground = true, Name = "mbn-com" };
        _comThread.Start();
        return await tcs.Task.ConfigureAwait(false);
    }

    private void ComMain(TaskCompletionSource<bool> tcs)
    {
        try
        {
            Log(AtLineKind.Info, "opening Mobile Broadband service");
            _mgr = (IMbnInterfaceManager)new MbnInterfaceManagerCoClass();

            var cpc = (IConnectionPointContainer)_mgr;
            var iid = IidImbnSmsEvents;
            cpc.FindConnectionPoint(ref iid, out _cp);
            if (_cp is null) throw new InvalidOperationException("IMbnSmsEvents connection point unavailable");
            _cp.Advise(_sink, out _cookie);

            // Event-driven signal: MEASURED LIVE 2026-09-22 (mbncli signal 60s,
            // Quectel EM120R-GL / WwanSvc): the manager connection point advises
            // successfully but OnSignalStateChange NEVER fires (0 pushes in 60 s
            // vs 12 polls, while RSSI moved 5->7 — only polls saw it), and the
            // interface object exposes no connection point container at all.
            // The driver does NOT push signal events on this stack; the meter is
            // poll-driven. The advise stays wired so pushes are counted and
            // logged (first 3 + cumulative in the poll line) should a firmware
            // update ever start pushing them.
            try
            {
                var sigIid = new Guid("DCBBBAB6-2004-4BBB-AAEE-338E368AF6FA");
                cpc.FindConnectionPoint(ref sigIid, out _signalCp);
                if (_signalCp is not null)
                {
                    _signalCp.Advise(_signalSink, out _signalCookie);
                    _signalSink.SignalChanged += OnSignalPush;
                }
            }
            catch (Exception ex)
            {
                Log(AtLineKind.Info, $"signal events unavailable ({ex.Message}) — poll-only mode");
            }
            _sink.EventLogged += line => Log(AtLineKind.Urc, line);
            _sink.StoreChangedEvent.WaitOne(0); // clear

            _sink.StoreChangedEvent.Reset();
            _sink.ConfigurationEvent.Reset();
            _sink.ReadCompleted.Reset();
            _sink.SendCompleted.Reset();

            _mgr.GetInterfaces(out var ifaces);
            if (ifaces is not { Length: > 0 })
            {
                Log(AtLineKind.Info, "no mobile broadband interface present");
                tcs.TrySetResult(false);
                return;
            }

            var iface = ifaces[0];
            _sms = (IMbnSms)iface;
            _signal = (IMbnSignal)iface;
            _registration = (IMbnRegistration)iface;

            Status.ChannelName = "MBIM / WwanSvc";

            try
            {
                iface.GetInterfaceCapability(out var caps);
                Status.ModemIdentity = $"{caps.Manufacturer} {caps.Model} (fw {caps.FirmwareInfo})";
                Log(AtLineKind.Info, $"modem: {Status.ModemIdentity}, SMS caps 0x{caps.SmsCaps:X}");
            }
            catch (COMException ex) { Log(AtLineKind.Info, $"caps unavailable: 0x{ex.HResult:X8}"); }

            try
            {
                iface.GetSubscriberInformation(out var sub);
                sub.get_SubscriberID(out var imsi);
                sub.get_SimIccID(out var iccid);
                sub.get_TelephoneNumbers(out var numbers);
                var msisdn = numbers is { Length: > 0 } ? numbers[0] : "";
                Status.Sim = SimState.Ready;
                Log(AtLineKind.Info, $"SIM: IMSI {imsi}, ICCID {iccid}, MSISDN {msisdn}");
            }
            catch (COMException)
            {
                Status.Sim = SimState.Absent;
                Log(AtLineKind.Info, "subscriber information unavailable — SIM absent?");
            }

            _sink.ConfigurationEvent.WaitOne(TimeSpan.FromSeconds(5));
            LoadSmsConfiguration();
            PollStatus();

            _connected = true;
            Status.Link = LinkState.Online;
            Status.LastPollLocal = DateTime.Now;
            StatusChanged?.Invoke();
            Log(AtLineKind.Info, "channel up — MBIM SMS bridge online");
            tcs.TrySetResult(true);

            // messages that arrived while we were closed: the chat store
            // handles this when it's the receive channel; on sweep-mode stacks
            // the modem store still holds them
            if (_modemStoreSweepEnabled) _queue.Add(Sweep);

            // store-change driven sweeps + class-0 messages (sweep mode only;
            // chat-store stacks intercept SMS before the modem store fills)
            if (_modemStoreSweepEnabled)
            {
                ThreadPool.RegisterWaitForSingleObject(_sink.StoreChangedEvent,
                    (_, _) =>
                    {
                        if (!_sink.StoreChanged) return;
                        _sink.StoreChanged = false;
                        _sink.StoreChangedEvent.Reset();
                        QueueSweep();
                    }, null, Timeout.Infinite, executeOnlyOnce: false);
            }
            _sink.Class0Messages += arr => DeliverArray(arr, "class0");

            foreach (var work in _queue.GetConsumingEnumerable())
                SafeRun(work);
        }
        catch (Exception ex)
        {
            Log(AtLineKind.Info, $"connect failed: {ex.Message}");
            Status.Link = LinkState.Offline;
            StatusChanged?.Invoke();
            tcs.TrySetResult(false);
        }
        finally
        {
            TeardownCom();
        }
    }

    private void SafeRun(Action work)
    {
        try { work(); }
        catch (Exception ex) { Log(AtLineKind.Info, $"worker error: {ex.Message}"); }
    }

    private void TeardownCom()
    {
        try { if (_cp is not null && _cookie != 0) _cp.Unadvise(_cookie); } catch { }
        try { if (_signalCp is not null && _signalCookie != 0) _signalCp.Unadvise(_signalCookie); } catch { }
        _connected = false;
    }

    private readonly SmsEventsSink _sink = new();
    private readonly SignalEventsSink _signalSink = new();
    private IConnectionPoint? _signalCp;
    private int _signalCookie;

    private void LoadSmsConfiguration()
    {
        try
        {
            _sms!.GetSmsConfiguration(out var cfg);
            cfg.get_ServiceCenterAddress(out _smsc);
            Log(AtLineKind.Info, $"SMSC {_smsc}");
        }
        catch (COMException ex)
        {
            Log(AtLineKind.Info, $"SMS config unavailable (0x{ex.HResult:X8}) — kicking");
            try
            {
                _sms!.GetSmsConfiguration(out var cfg);
                cfg.get_ServiceCenterAddress(out _smsc);
                cfg.put_ServiceCenterAddress(_smsc!);
                _sms.SetSmsConfiguration(cfg, out _);
                Log(AtLineKind.Info, "configuration kick issued");
            }
            catch (COMException ex2) { Log(AtLineKind.Info, $"kick failed: 0x{ex2.HResult:X8}"); }
        }
    }

    // ---- status ------------------------------------------------------------

    /// <summary>Total OnSignalStateChange pushes since connect. Live-measured
    /// 2026-09-22: stays 0 on the EM120R-GL/WwanSvc stack (60 s probe, 0 pushes
    /// vs 12 polls while RSSI moved 5->7) — the driver pushes none, so the
    /// meter is poll-driven on this hardware.</summary>
    private int _signalPushCount;

    /// <summary>Driver-pushed signal change: refresh quality immediately.
    /// First 3 pushes are logged individually; every push bumps the cumulative
    /// count shown on the periodic poll line.</summary>
    private void OnSignalPush()
    {
        int n = Interlocked.Increment(ref _signalPushCount);
        try
        {
            _signal!.GetSignalStrength(out var rssi);
            Status.Rssi = (int)rssi;
            if (n <= 3)
                Log(AtLineKind.Info, $"signal push #{n}: rssi={rssi} (driver-pushed OnSignalStateChange)");
            StatusChanged?.Invoke();
        }
        catch
        {
            if (n <= 3)
                Log(AtLineKind.Info, $"signal push #{n}: strength unreadable (driver-pushed OnSignalStateChange)");
            /* the poll will catch up */
        }
    }

    private void PollStatus()
    {
        if (!_connected) return;
        int rssiRaw = -1, regRaw = -1;
        bool signalOk = false, regOk = false;
        try
        {
            _signal!.GetSignalStrength(out var rssi);
            rssiRaw = (int)rssi;
            Status.Rssi = (int)rssi;
            signalOk = true;
        }
        catch (COMException ex) { Log(AtLineKind.Info, $"signal poll failed: 0x{ex.HResult:X8}"); }
        try
        {
            _registration!.GetRegisterState(out var reg);
            regRaw = reg;
            Status.Net = reg switch
            {
                3 => NetState.Home,
                4 or 5 => NetState.Roaming,
                2 => NetState.Searching,
                6 => NetState.Denied,
                _ => NetState.Unknown,
            };
            if (Status.Net is NetState.Home or NetState.Roaming)
            {
                _registration.GetProviderName(out var name);
                if (!string.IsNullOrEmpty(name)) Status.OperatorName = name;

                // numeric network identity (MCC+MNC) + live radio technology
                try
                {
                    _registration.GetProviderID(out var pid);
                    if (!string.IsNullOrEmpty(pid)) Status.ProviderId = pid;
                    _registration.GetCurrentDataClass(out uint dc);
                    Status.RadioTech = DescribeDataClass(dc);
                }
                catch { /* older stacks may not implement these */ }
                try
                {
                    _registration.GetRoamingText(out var roam);
                    Status.RoamingText = roam ?? "";
                }
                catch { Status.RoamingText = ""; }
            }
            regOk = true;
        }
        catch (COMException ex) { Log(AtLineKind.Info, $"registration poll failed: 0x{ex.HResult:X8}"); }

        // Mobility supervision: transient radio gaps (tower handovers, tunnels)
        // show up as reg=Searching on SUCCESSFUL polls — the NET lamp handles
        // that. Dead COM objects (WwanSvc bounce) show up as repeated poll
        // FAILURES — after 3, declare link lost so the UI supervisor recreates
        // the backend with fresh objects.
        bool pollOk = signalOk && regOk;
        if (pollOk)
        {
            _pollFailures = 0;
            Status.LastPollLocal = DateTime.Now;
            Status.Link = LinkState.Online;
        }
        else
        {
            _pollFailures++;
            if (_pollFailures >= 3)
            {
                Log(AtLineKind.Info, $"LINK LOST — {_pollFailures} consecutive poll failures; supervisor should reconnect");
                Status.Link = LinkState.Offline;
            }
            else
            {
                Status.Link = LinkState.Connecting;
            }
        }

        // receive-channel health: a dead chat store is equally fatal
        if (_chatReceiver is { ConsecutiveFailures: >= 5 })
        {
            Log(AtLineKind.Info, "LINK LOST — chat store receiver persistently failing");
            Status.Link = LinkState.Offline;
        }

        Log(AtLineKind.Info, $"poll: rssi={rssiRaw} reg={regRaw} net={Status.Net} rat={Status.RadioTech} ok={pollOk} link={Status.Link} sigEvents={Volatile.Read(ref _signalPushCount)}");
        StatusChanged?.Invoke();
    }

    /// <summary>MBN_DATA_CLASS bits → human RAT name (best match wins).</summary>
    private static string DescribeDataClass(uint dc) => dc switch
    {
        0 => "",
        _ when (dc & 0x20) != 0 => "LTE",
        _ when (dc & 0x18) == 0x18 => "HSPA+",
        _ when (dc & 0x8) != 0 => "HSDPA",
        _ when (dc & 0x10) != 0 => "HSUPA",
        _ when (dc & 0x4) != 0 => "3G",
        _ when (dc & 0x2) != 0 => "EDGE",
        _ when (dc & 0x1) != 0 => "GPRS",
        // note: DC is a 32-bit mask, so 5G-NR (newer stacks, bit 32) cannot
        // appear here — extend DescribeDataClass if that ever changes.
        _ => $"0x{dc:X}",
    };

    // ---- send --------------------------------------------------------------

    public Task<SendResult> SendAsync(string destination, string text)
    {
        var tcs = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref _sendsPending);
        _queue.Add(() =>
        {
            try
            {
                if (!_connected || _sms is null)
                {
                    tcs.TrySetResult(new SendResult { Success = false, Detail = "backend not connected" });
                    return;
                }

                var plan = Segmenter.Plan(text);
                if (plan.Total == 0)
                {
                    tcs.TrySetResult(new SendResult { Success = false, Detail = "empty message" });
                    return;
                }

                int concatRef = Random.Shared.Next(256);
                int? lastRef = null;
                for (int i = 0; i < plan.Total; i++)
                {
                    var pdu = SmsPdu.EncodeSubmit(destination, plan.Parts[i], concatRef, i + 1, plan.Total);
                    string fullPdu = BuildFullPdu(pdu.Hex);
                    Log(AtLineKind.Tx, $"CMGS-equivalent → {destination} ({pdu.TpduOctets} octets, part {i + 1}/{plan.Total})");

                    var result = SendOnePdu(fullPdu, pdu.TpduOctets);
                    if (!result.Success)
                    {
                        tcs.TrySetResult(result);
                        return;
                    }
                    lastRef = result.ModemRef;
                }
                tcs.TrySetResult(new SendResult { Success = true, ModemRef = lastRef });
            }
            finally
            {
                Interlocked.Decrement(ref _sendsPending);
            }
        });
        return tcs.Task;
    }

    /// <summary>
    /// DATA-SAFETY contract: a retry is issued ONLY after an explicit failure
    /// completion (status != 0) from the network. If the modem accepted the
    /// request but no completion arrives, the outcome is UNKNOWN — the message
    /// may already have been delivered — so we report it and DO NOT resend.
    /// Max one retry, explicit failures only, no exceptions.
    /// </summary>
    private SendResult SendOnePdu(string fullPdu, int tpduOctets)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            _sink.SendCompleted.Reset();
            _sink.SendStatus = int.MinValue;
            _sink.SendInFlight = true;
            try
            {
                _sms!.SmsSendPdu(fullPdu, (byte)tpduOctets, out var requestID);
                _sink.ExpectedRequestId = requestID; // logging only
                Log(AtLineKind.Info, $"send request {requestID} accepted, waiting on network…");
            }
            catch (COMException ex)
            {
                _sink.SendInFlight = false;
                Log(AtLineKind.Info, $"SmsSendPdu rejected outright: 0x{ex.HResult:X8} — nothing was sent");
                return new SendResult { Success = false, Detail = $"modem rejected the request (0x{ex.HResult:X8}); nothing was sent" };
            }

            var sw = Stopwatch.StartNew();
            while (!_sink.SendCompleted.WaitOne(250))
            {
                if (sw.ElapsedMilliseconds <= 90_000) continue;
                _sink.SendInFlight = false;
                Log(AtLineKind.Info, "send outcome UNKNOWN — no completion within 90 s; NOT resending (message may have been delivered)");
                return new SendResult
                {
                    Success = false,
                    Detail = "UNCONFIRMED: no completion within 90 s — the message may still have been delivered; not resent for safety",
                };
            }
            _sink.SendInFlight = false;

            if (_sink.SendStatus == 0)
            {
                Log(AtLineKind.Rx, "SEND COMPLETE (S_OK)");
                return new SendResult { Success = true };
            }

            // Explicit failure — the network told us it did NOT deliver.
            if (attempt == 1)
            {
                Log(AtLineKind.Info, $"explicit failure 0x{_sink.SendStatus:X8} — one retry permitted");
                Thread.Sleep(3000);
                continue;
            }
            return new SendResult { Success = false, Detail = $"network refused: 0x{_sink.SendStatus:X8}" };
        }
        return new SendResult { Success = false, Detail = "network refused" };
    }

    /// <summary>Wait for a read completion in slices, aborting early if a send
    /// is queued behind us (sends must never wait behind slow store reads).</summary>
    private bool WaitForReadInterruptible(int totalMs)
    {
        var sw = Stopwatch.StartNew();
        while (!_sink.ReadCompleted.WaitOne(250))
        {
            if (_sendsPending > 0)
            {
                Log(AtLineKind.Info, "read wait yielded — send queued");
                return false;
            }
            if (sw.ElapsedMilliseconds >= totalMs) return false;
        }
        return true;
    }

    private string BuildFullPdu(string tpduHex)
    {
        // Prefix the SMSC (from the modem's own configuration) to the TPDU.
        if (!string.IsNullOrEmpty(_smsc) && _smsc.StartsWith('+'))
        {
            var digits = new string(_smsc.Where(char.IsDigit).ToArray());
            var semi = SmsPdu.BytesToHex(SmsCodecs.EncodeSemiOctets(digits));
            int smscOctets = 1 + semi.Length / 2;
            return smscOctets.ToString("X2") + "91" + semi + tpduHex[2..];
        }
        return tpduHex; // keeps our '00' placeholder — modem uses SIM default
    }

    // ---- receive -----------------------------------------------------------

    private void QueueSweep()
    {
        if (_sweepQueued) return;
        _sweepQueued = true;
        _queue.Add(() =>
        {
            try { Sweep(); }
            finally { _sweepQueued = false; }
        });
    }

    private void Sweep()
    {
        if (!_connected || _sms is null) return;
        if (_sendsPending > 0)
        {
            Log(AtLineKind.Info, "sweep deferred — sends queued");
            return;
        }

        // FLAG_ALL (0) covers the whole store in one read — no per-status loops.
        _sink.ReadCompleted.Reset();
        var filter = new MbnSmsFilter { Flag = 0, Index = 0 };
        try
        {
            _sms.SmsRead(ref filter, SmsFormatPdu, out _);
        }
        catch (COMException ex)
        {
            Log(AtLineKind.Info, $"SmsRead rejected: 0x{ex.HResult:X8}");
            return;
        }

        if (!WaitForReadInterruptible(75_000))
        {
            Log(AtLineKind.Info, "SmsRead wait yielded/timed out");
            return;
        }
        if (_sink.ReadStatus != 0)
        {
            // Empty filter slot reports 0x80548222 on this firmware.
            return;
        }
        DeliverArray(_sink.ReadMessages, "store");

        foreach (var (sender, text) in _reassembler.FlushStale())
            MessageReceived?.Invoke(new SmsIncomingMessage(sender, text, DateTime.Now));
    }

    private void DeliverArray(Array? messages, string source)
    {
        if (messages is not { Length: > 0 }) return;
        foreach (var element in messages)
        {
            string pduHex;
            uint index = uint.MaxValue;
            try
            {
                var msg = (IMbnSmsReadMsgPdu)element;
                msg.get_Index(out index);
                msg.get_PduData(out pduHex);
            }
            catch (Exception ex)
            {
                Log(AtLineKind.Info, $"store element unreadable: {ex.Message}");
                continue;
            }

            lock (_seenLock)
            {
                if (!_seenPdus.Add(pduHex)) continue; // already imported
            }

            try
            {
                var deliver = SmsPdu.DecodeDeliver(pduHex);
                Log(AtLineKind.Rx, $"inbound from {deliver.Sender} (slot {index}, {source})");
                var full = _reassembler.Add(deliver);
                if (full is not null)
                    MessageReceived?.Invoke(new SmsIncomingMessage(deliver.Sender, full, deliver.Timestamp.LocalDateTime));
            }
            catch (ArgumentException)
            {
                // SMS-STATUS-REPORT (delivery report) or non-DELIVER PDU — noted, not shown.
                Log(AtLineKind.Info, $"slot {index}: non-deliver PDU skipped (status report?)");
            }
            catch (Exception ex)
            {
                Log(AtLineKind.Info, $"slot {index}: decode failed — {ex.Message}");
            }
        }
    }

    // ---- lifecycle ---------------------------------------------------------

    public void Disconnect()
    {
        _queue.CompleteAdding();
        try { _comThread?.Join(3000); } catch { }
        _connected = false;
        _chatReceiver?.Dispose();
        _chatReceiver = null;
        Status.Link = LinkState.Offline;
        Status.Sim = SimState.Unknown;
        Status.Net = NetState.Unknown;
        Status.OperatorName = "";
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _pollTimer.Dispose();
        Disconnect();
        _queue.Dispose();
    }

    private void Log(AtLineKind kind, string text) => Logged?.Invoke(kind, text);
}
