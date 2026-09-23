namespace WwanMessenger.Core.Channels;

/// <summary>
/// A raw byte/line pipe to a modem's AT interpreter.
/// Implementations: <see cref="SerialModemChannel"/> (real hardware) and
/// <see cref="SimulatedModemChannel"/> (in-process fake modem for hardwareless development).
/// </summary>
public interface IModemChannel : IDisposable
{
    string Name { get; }
    bool IsOpen { get; }

    void Open();
    void Close();

    /// <summary>Writes a command line (newline is appended).</summary>
    void WriteLine(string line);

    /// <summary>Writes raw bytes (SMS payload + SUBMIT/ESC control characters).</summary>
    void WriteRaw(byte[] data);

    /// <summary>A complete modem response/URC line with CRLF already stripped.</summary>
    event Action<string>? LineReceived;

    /// <summary>The modem's "> " prompt — it wants the SMS payload now.</summary>
    event Action? PromptReceived;
}
