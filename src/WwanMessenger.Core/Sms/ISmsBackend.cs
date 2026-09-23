using WwanMessenger.Core.At;
using WwanMessenger.Core.Modem;

namespace WwanMessenger.Core.Sms;

/// <summary>
/// Transport-agnostic messaging backend: AT-over-serial (SmsService) or
/// Windows MBIM/WwanSvc (MbnSmsService). The UI binds to this.
/// </summary>
public interface ISmsBackend : IDisposable
{
    ModemStatus Status { get; }

    event Action<SmsIncomingMessage>? MessageReceived;
    event Action? StatusChanged;
    event Action<AtLineKind, string>? Logged;

    Task<bool> ConnectAsync();
    void Disconnect();
    Task<SendResult> SendAsync(string destination, string text);
}
