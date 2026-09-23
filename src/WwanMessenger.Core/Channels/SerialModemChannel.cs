using System.IO.Ports;
using System.Text;

namespace WwanMessenger.Core.Channels;

/// <summary>
/// Real modem over a serial COM port (USB CDC-ACM AT port exposed by the WWAN module).
/// </summary>
public sealed class SerialModemChannel : IModemChannel
{
    private readonly SerialPort _port;
    private readonly object _rxLock = new();
    private readonly StringBuilder _rxBuffer = new();

    public string Name => _port.PortName;
    public bool IsOpen => _port.IsOpen;

    public event Action<string>? LineReceived;
    public event Action? PromptReceived;

    public SerialModemChannel(string portName, int baud = 115200)
    {
        _port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 5000,
            DtrEnable = true,   // many USB modems stay silent without DTR asserted
            RtsEnable = true,
            Handshake = Handshake.None,
            NewLine = "\r\n",
        };
        _port.DataReceived += (_, _) => Drain();
    }

    public void Open()
    {
        if (!_port.IsOpen)
        {
            _port.Open();
            _port.DiscardInBuffer();
            lock (_rxLock) _rxBuffer.Clear();
        }
    }

    public void Close()
    {
        if (_port.IsOpen) _port.Close();
        lock (_rxLock) _rxBuffer.Clear();
    }

    public void WriteLine(string line)
    {
        if (!_port.IsOpen) throw new InvalidOperationException($"Port {_port.PortName} is not open.");
        _port.Write(line);
        _port.Write("\r");
    }

    public void WriteRaw(byte[] data)
    {
        if (!_port.IsOpen) throw new InvalidOperationException($"Port {_port.PortName} is not open.");
        _port.Write(data, 0, data.Length);
    }

    private void Drain()
    {
        lock (_rxLock)
        {
            try
            {
                while (_port.BytesToRead > 0)
                {
                    int b = _port.ReadByte();
                    if (b < 0) break;
                    _rxBuffer.Append((char)b);

                    // Complete line?
                    int nl;
                    while ((nl = IndexOfLineBreak(_rxBuffer)) >= 0)
                    {
                        var line = _rxBuffer.ToString(0, nl).Trim('\r', '\n');
                        // swallow the terminator (handles \r\n and lone \n)
                        int termLen = (_rxBuffer[nl] == '\r' && nl + 1 < _rxBuffer.Length && _rxBuffer[nl + 1] == '\n') ? 2 : 1;
                        _rxBuffer.Remove(0, nl + termLen);
                        if (line.Length > 0) LineReceived?.Invoke(line);
                    }

                    // SMS payload prompt: "\r\n> " with no terminator — flush it eagerly.
                    if (_rxBuffer.Length > 0 && _rxBuffer.ToString(_rxBuffer.Length - 1, 1) == " ")
                    {
                        var tail = _rxBuffer.ToString();
                        if (tail.EndsWith("> ", StringComparison.Ordinal))
                        {
                            _rxBuffer.Clear();
                            PromptReceived?.Invoke();
                        }
                    }
                }
            }
            catch (TimeoutException) { /* drained */ }
            catch (IOException) { /* port vanished; upper layers observe via failed commands */ }
            catch (ObjectDisposedException) { /* closing */ }
        }
    }

    private static int IndexOfLineBreak(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
            if (sb[i] == '\r' || sb[i] == '\n') return i;
        return -1;
    }

    public void Dispose()
    {
        Close();
        _port.Dispose();
    }
}
