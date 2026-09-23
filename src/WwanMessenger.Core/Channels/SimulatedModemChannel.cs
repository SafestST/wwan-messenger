using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using WwanMessenger.Core.Sms;

namespace WwanMessenger.Core.Channels;

public enum SimScenario
{
    /// <summary>SIM present, PIN ok, registered, auto-replying "network bot".</summary>
    SimReady,
    /// <summary>No SIM inserted — mirrors the machine's current real state.</summary>
    NoSim,
    /// <summary>SIM present but requires PIN.</summary>
    SimPin,
    /// <summary>SIM present, no network registration (searching).</summary>
    NoNetwork,
}

/// <summary>
/// In-process fake WWAN modem speaking the same AT subset we use on real hardware.
/// Lets the entire client (UI, AT engine, PDU codecs, store) be exercised with no
/// SIM and no WWAN card. Inbound traffic is generated as real PDU-mode messages,
/// decoded by the same codec used in production.
/// </summary>
public sealed class SimulatedModemChannel : IModemChannel
{
    private readonly ConcurrentDictionary<string, Regex> _regexes = new();
    private readonly Random _rng = new();
    private readonly object _stateLock = new();
    private readonly List<SimStoredSms> _messages = new();
    private readonly SemaphoreSlim _handlerLock = new(1, 1);   // preserve command/response ordering
    private readonly System.Timers.Timer _signalTimer;

    private bool _open;
    private int _cmgf;                  // 0 = PDU (our default), 1 = text
    private string _cscs = "GSM";
    private int _nextIdx = 1;
    private int _nextMr = 11;
    private int _replyCount;
    private string? _payloadDest;       // text-mode CMGS destination awaiting payload
    private int? _payloadTpduLen;       // PDU-mode CMGS octet count awaiting payload
    private readonly StringBuilder _payload = new();

    public SimScenario Scenario { get; set; } = SimScenario.SimReady;
    public bool AutoReply { get; set; } = true;
    public string OperatorName { get; set; } = "SimNet";
    public string BotNumber { get; set; } = "+447700900123";

    public int Rssi { get; private set; } = 22;

    public string Name => "SIMULATOR";
    public bool IsOpen { get { lock (_stateLock) return _open; } }

    public event Action<string>? LineReceived;
    public event Action? PromptReceived;

    public SimulatedModemChannel()
    {
        _signalTimer = new System.Timers.Timer(3000) { AutoReset = true };
        _signalTimer.Elapsed += (_, _) =>
        {
            lock (_stateLock) { Rssi = Math.Clamp(Rssi + _rng.Next(-3, 4), 8, 30); }
        };
    }

    public void Open() { lock (_stateLock) { _open = true; _signalTimer.Start(); } }
    public void Close() { lock (_stateLock) { _open = false; _signalTimer.Stop(); } }

    public void WriteLine(string line) => Task.Run(() => Handle(line.Trim()));

    public void WriteRaw(byte[] data) => Task.Run(() =>
    {
        foreach (var b in data)
        {
            if (b == 0x1A) { EmitPayload(); return; }             // Ctrl-Z: message complete
            if (b == 0x1B)                                         // ESC: abort
            {
                lock (_stateLock) { _payload.Clear(); _payloadDest = null; _payloadTpduLen = null; }
                Respond("ERROR");
                return;
            }
            _payload.Append((char)b);
        }
    });

    // ---- modem brain -------------------------------------------------------

    private void Handle(string cmd)
    {
        _handlerLock.Wait();
        try
        {
            Thread.Sleep(_rng.Next(15, 40)); // a little latency feels like real hardware

            if (_payloadDest is not null || _payloadTpduLen is not null)
            {
                // payload should arrive as raw bytes; tolerate line input defensively
                _payload.Append(cmd);
                if (_payload.Length > 0 && _payload[^1] == '\x1A')
                {
                    _payload.Remove(_payload.Length - 1, 1);
                    EmitPayload();
                }
                return;
            }

            switch (cmd)
            {
                case "AT": case "ATE0": case "ATE1": case "AT&F": case "ATZ":
                    Respond("OK"); return;
            }

            if (Match(cmd, @"^ATI\d*$"))
            {
                Respond("WWAN Messenger Simulator");
                Respond("WWAN bench modem, FW 0.9.0");
                Respond("OK");
                return;
            }
            if (Match(cmd, @"^AT\+CMEE=\d$")) { Respond("OK"); return; }
            if (cmd == "AT+CPIN?")
            {
                Respond(Scenario switch
                {
                    SimScenario.SimReady or SimScenario.NoNetwork => "+CPIN: READY",
                    SimScenario.SimPin => "+CPIN: SIM PIN",
                    _ => "",
                }, Scenario == SimScenario.NoSim ? "+CME ERROR: sim not inserted" : "OK");
                return;
            }
            if (cmd == "AT+CSQ")
            {
                Respond(Scenario == SimScenario.NoSim ? "+CSQ: 99,99" : $"+CSQ: {Rssi},99", "OK");
                return;
            }
            if (cmd == "AT+COPS?")
            {
                Respond(Scenario == SimScenario.SimReady ? $"+COPS: 0,0,\"{OperatorName}\"" : "+COPS: 0", "OK");
                return;
            }
            if (cmd == "AT+CIMI" || cmd == "AT+CCID")
            {
                if (Scenario is SimScenario.NoSim or SimScenario.SimPin)
                    Respond("+CME ERROR: sim not inserted");
                else
                    Respond(cmd == "AT+CIMI" ? "234150009988776" : "+CCID: 8944110099887766554", "OK");
                return;
            }
            if (cmd == "AT+CEREG?")
            {
                var stat = Scenario switch
                {
                    SimScenario.SimReady => "1",
                    SimScenario.NoNetwork => "2",
                    SimScenario.NoSim => "3",
                    _ => "0",
                };
                Respond($"+CEREG: 2,{stat}", "OK");
                return;
            }
            if (cmd == "AT+CGATT?") { Respond($"+CGATT: {(Scenario == SimScenario.SimReady ? 1 : 0)}", "OK"); return; }
            if (cmd == "AT+CSCA?") { Respond("+CSCA: \"+447700900000\",145", "OK"); return; }
            if (Match(cmd, @"^AT\+CMGF=(\d)$")) { _cmgf = int.Parse(Group(1)); Respond("OK"); return; }
            if (Match(cmd, @"^AT\+CSCS=""(\w+)""$")) { _cscs = Group(1); Respond("OK"); return; }
            if (Match(cmd, @"^AT\+CNMI=\d,\d,\d,\d(,\d)?$")) { Respond("OK"); return; }
            if (Match(cmd, @"^AT\+(CSMP=.+|COPS=.+|CFUN=\d|CMMS=\d|CNMA|CSQ=0)$")) { Respond("OK"); return; }

            if (Match(cmd, @"^AT\+CMGS=""(\+?\d*)""$"))
            {
                if (!SimUsable()) { Respond(CmsSimError()); return; }
                lock (_stateLock) { _payloadDest = Group(1); _payload.Clear(); }
                Task.Run(async () => { await Task.Delay(25); PromptReceived?.Invoke(); });
                return;
            }
            if (Match(cmd, @"^AT\+CMGS=(\d+)$"))
            {
                if (!SimUsable()) { Respond(CmsSimError()); return; }
                lock (_stateLock) { _payloadTpduLen = int.Parse(Group(1)); _payload.Clear(); }
                Task.Run(async () => { await Task.Delay(25); PromptReceived?.Invoke(); });
                return;
            }
            if (Match(cmd, @"^AT\+CMGR=(\d+)$")) { HandleCmgr(int.Parse(Group(1))); return; }
            if (Match(cmd, @"^AT\+CMGL=(\d+|""[A-Z ]+"")$")) { HandleCmgl(); return; }
            if (Match(cmd, @"^AT\+CMGD=(\d+)$"))
            {
                int idx = int.Parse(Group(1));
                lock (_stateLock) _messages.RemoveAll(m => m.Index == idx);
                Respond("OK");
                return;
            }

            Respond("ERROR");
        }
        finally
        {
            _handlerLock.Release();
        }
    }

    private string CmsSimError()
        => Scenario == SimScenario.SimPin ? "+CMS ERROR: 311" : "+CMS ERROR: 310";
    private bool SimUsable() => Scenario is SimScenario.SimReady or SimScenario.NoNetwork;

    private bool Match(string cmd, string pattern)
    {
        var re = _regexes.GetOrAdd(pattern, p => new Regex(p, RegexOptions.Compiled));
        _lastMatch = re.Match(cmd);
        return _lastMatch.Success;
    }

    [ThreadStatic] private static Match? _lastMatchThread;
    private Match? _lastMatch { get => _lastMatchThread; set => _lastMatchThread = value; }
    private string Group(int n) => _lastMatch?.Groups[n].Value ?? "";

    private void EmitPayload()
    {
        string payload;
        string? dest;
        try
        {
            lock (_stateLock)
            {
                payload = _payload.ToString().TrimEnd('\r', '\n');
                dest = _payloadDest;
                _payload.Clear();
                _payloadDest = null;
                _payloadTpduLen = null;
            }
        }
        catch { return; }

        if (payload.Length == 0) { Respond("ERROR"); return; }

        if (dest is not null)
        {
            string text = _cscs == "UCS2"
                ? Encoding.BigEndianUnicode.GetString(SmsPdu.HexToBytes(payload))
                : payload;
            int mr;
            lock (_stateLock) mr = _nextMr++;
            Respond($"+CMGS: {mr}", "OK");
            if (AutoReply && Scenario == SimScenario.SimReady)
                ScheduleBotReply(dest, text);
            return;
        }

        try
        {
            var submit = SmsPdu.DecodeSubmit(payload);
            int mr;
            lock (_stateLock) mr = _nextMr++;
            Respond($"+CMGS: {mr}", "OK");
            if (AutoReply && Scenario == SimScenario.SimReady)
                ScheduleBotReply(submit.Destination, submit.Text);
        }
        catch
        {
            Respond("+CMS ERROR: 500"); // invalid PDU
        }
    }

    private void ScheduleBotReply(string dest, string receivedText)
    {
        int n = Interlocked.Increment(ref _replyCount);
        Task.Run(async () =>
        {
            await Task.Delay(_rng.Next(1500, 4000));
            var text = ComposeReply(n, receivedText);
            var plan = Segmenter.Plan(text); // chunk with the production segmenter: honours GSM7/UCS2 limits
            int refId = _rng.Next(256);
            for (int seq = 1; seq <= plan.Total; seq++)
            {
                var (hex, octets) = SmsPdu.EncodeDeliver(dest, plan.Parts[seq - 1], DateTimeOffset.Now, refId, plan.Total, seq);
                DeliverToStorage(hex, octets);
                if (plan.Total > 1) await Task.Delay(120);
            }
        });
    }

    private static string ComposeReply(int n, string receivedText)
    {
        string[] replies =
        {
            $"Copy that — received loud and clear at signal 22/31.",
            "RX confirmed, both ends of the link are green.",
            $"Roger. Your {receivedText.Length} characters arrived intact.",
            $"Message logged {DateTime.Now:HH:mm}. Standing by on this channel.",
        };
        var text = replies[n % replies.Length];
        if (n % 3 == 0)
        {
            // long reply exercises the multipart reassembly path
            text += " LONG-FRAME TEST: " + string.Concat(Enumerable.Repeat(
                "The quick brown fox jumps over the lazy dog 0123456789. ", 4));
        }
        return text;
    }

    private void DeliverToStorage(string hex, int tpduOctets)
    {
        int idx;
        lock (_stateLock)
        {
            idx = _nextIdx++;
            _messages.Add(new SimStoredSms(idx, hex));
        }
        Task.Run(async () => { await Task.Delay(60); Emit($"+CMTI: \"ME\",{idx}"); });
    }

    private void HandleCmgr(int idx)
    {
        SimStoredSms? msg;
        lock (_stateLock) msg = _messages.FirstOrDefault(m => m.Index == idx);
        if (msg is null) { Respond("+CMS ERROR: 321"); return; }
        Respond($"+CMGR: 0,,{msg.TpduOctets}", msg.PduHex, "OK");
    }

    private void HandleCmgl()
    {
        List<SimStoredSms> snapshot;
        lock (_stateLock) snapshot = _messages.ToList();
        foreach (var m in snapshot)
            Respond($"+CMGL: {m.Index},0,,{m.TpduOctets}", m.PduHex);
        Respond("OK");
    }

    /// <summary>Inject an inbound message from outside (tests, UI demos).</summary>
    public void InjectInbound(string fromNumber, string text)
    {
        var (hex, octets) = SmsPdu.EncodeDeliver(fromNumber, text, DateTimeOffset.Now);
        DeliverToStorage(hex, octets);
    }

    // ---- plumbing ----------------------------------------------------------

    private void Respond(params string[] lines)
    {
        foreach (var line in lines)
            if (line.Length > 0) Emit(line);
    }

    private void Emit(string line) => LineReceived?.Invoke(line);

    public void Dispose()
    {
        Close();
        _signalTimer.Dispose();
        _handlerLock.Dispose();
    }

    private sealed record SimStoredSms(int Index, string PduHex)
    {
        public int TpduOctets => PduHex.Length / 2 - 1; // minus the SMSC length byte
    }
}
