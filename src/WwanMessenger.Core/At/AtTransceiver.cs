using System.Text.RegularExpressions;
using WwanMessenger.Core.Channels;

namespace WwanMessenger.Core.At;

/// <summary>
/// Serialised AT command engine over an <see cref="IModemChannel"/>.
/// One command in flight at a time; unsolicited result codes (URCs) are routed to
/// <see cref="UrcReceived"/> even while a command is pending. Supports the modem's
/// "> " payload prompt (needed by AT+CMGS).
/// </summary>
public sealed class AtTransceiver : IDisposable
{
    // Lines that are ALWAYS unsolicited, never a command response we wait for.
    private static readonly Regex AlwaysUrc = new(
        @"^(\+CMT:|\+CMTI:|\+CDS:|\+CBM:|\+CGEV:|\+CIEV:|RING|\^)",
        RegexOptions.Compiled);

    private sealed class Pending
    {
        public TaskCompletionSource<AtResponse> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Lines = new();
        public Action? PromptAction;
    }

    private readonly IModemChannel _channel;
    private readonly SemaphoreSlim _cmdLock = new(1, 1);
    private readonly object _stateLock = new();
    private Pending? _pending;
    private bool _drainingOrphans; // after a timeout, swallow lines until the next final result

    public event Action<AtLineKind, string>? Logged;
    public event Action<string>? UrcReceived;

    /// <summary>Allows wrapping services to feed informational lines into the log stream.</summary>
    public void RaiseLog(AtLineKind kind, string text) => Logged?.Invoke(kind, text);

    public AtTransceiver(IModemChannel channel)
    {
        _channel = channel;
        _channel.LineReceived += OnLine;
        _channel.PromptReceived += OnPrompt;
    }

    public IModemChannel Channel => _channel;

    public AtResponse Execute(string command, int timeoutMs = 5000)
        => ExecuteAsync(command, null, timeoutMs).GetAwaiter().GetResult();

    public AtResponse ExecuteWithPrompt(string command, Action writePayload, int timeoutMs = 30000)
        => ExecuteAsync(command, writePayload, timeoutMs).GetAwaiter().GetResult();

    public Task<AtResponse> ExecuteWithPromptAsync(string command, Action writePayload, int timeoutMs = 30000)
        => ExecuteAsync(command, writePayload, timeoutMs);

    public async Task<AtResponse> ExecuteAsync(string command, Action? promptAction = null, int timeoutMs = 5000)
    {
        await _cmdLock.WaitAsync();
        try
        {
            var pending = new Pending { PromptAction = promptAction };
            lock (_stateLock)
            {
                _pending = pending;
                _drainingOrphans = false;
            }

            Logged?.Invoke(AtLineKind.Tx, command);

            try
            {
                _channel.WriteLine(command);
            }
            catch (Exception ex)
            {
                Logged?.Invoke(AtLineKind.Info, $"channel write failed: {ex.Message}");
                lock (_stateLock) _pending = null;
                return new AtResponse { Success = false, Final = $"CHANNEL ERROR: {ex.Message}" };
            }

            using var cts = new CancellationTokenSource(timeoutMs);
            using var reg = cts.Token.Register(() =>
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_pending, pending))
                    {
                        _pending = null;
                        _drainingOrphans = true;
                    }
                }
                pending.Tcs.TrySetResult(new AtResponse { Success = false, Final = "TIMEOUT" });
            });

            var resp = await pending.Tcs.Task.ConfigureAwait(false);
            Logged?.Invoke(AtLineKind.Rx, resp.Success ? "OK" : resp.Final);
            return resp;
        }
        finally
        {
            _cmdLock.Release();
        }
    }

    private void OnPrompt()
    {
        Pending? p;
        lock (_stateLock) p = _pending;
        if (p?.PromptAction == null) return;
        try { p.PromptAction(); }
        catch (Exception ex) { Logged?.Invoke(AtLineKind.Info, $"payload write failed: {ex.Message}"); }
    }

    private void OnLine(string line)
    {
        Pending? pending;
        lock (_stateLock) pending = _pending;

        // URCs are routed regardless of command state.
        if (AlwaysUrc.IsMatch(line))
        {
            Logged?.Invoke(AtLineKind.Urc, line);
            UrcReceived?.Invoke(line);
            return;
        }

        if (pending is null)
        {
            if (_drainingOrphans)
            {
                if (IsFinal(line, out _)) _drainingOrphans = false; // resynchronised
                return;
            }
            // Unsolicited info line while idle (e.g. modem boot banner).
            Logged?.Invoke(AtLineKind.Urc, line);
            UrcReceived?.Invoke(line);
            return;
        }

        if (IsFinal(line, out bool ok))
        {
            lock (_stateLock) _pending = null;
            pending.Tcs.TrySetResult(new AtResponse
            {
                Success = ok,
                Final = line,
                Lines = pending.Lines.ToList(),
            });
            return;
        }

        pending.Lines.Add(line);
    }

    private static bool IsFinal(string line, out bool ok)
    {
        switch (line)
        {
            case "OK": ok = true; return true;
            case "ERROR":
            case "NO ANSWER":
            case "NO DIALTONE":
            case "BUSY": ok = false; return true;
            default:
                if (line.StartsWith("+CMS ERROR:", StringComparison.Ordinal) ||
                    line.StartsWith("+CME ERROR:", StringComparison.Ordinal))
                { ok = false; return true; }
                ok = false;
                return false;
        }
    }

    public void Dispose()
    {
        _channel.LineReceived -= OnLine;
        _channel.PromptReceived -= OnPrompt;
        _cmdLock.Dispose();
    }
}
