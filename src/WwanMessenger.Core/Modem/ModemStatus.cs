namespace WwanMessenger.Core.Modem;

public enum LinkState { Offline, Connecting, Online }
public enum SimState { Unknown, Absent, PinRequired, Ready }
public enum NetState { Unknown, Searching, Home, Roaming, Denied }

public sealed class ModemStatus
{
    public LinkState Link { get; set; } = LinkState.Offline;
    public SimState Sim { get; set; } = SimState.Unknown;
    public NetState Net { get; set; } = NetState.Unknown;
    /// <summary>Raw RSSI 0–31, or 99 = unknown/no signal.</summary>
    public int Rssi { get; set; } = 99;
    public string OperatorName { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string? ModemIdentity { get; set; }

    /// <summary>Active radio technology from the registration context —
    /// "LTE", "HSPA+", "3G"… — flips live as the modem changes RAT.</summary>
    public string RadioTech { get; set; } = "";

    /// <summary>Numeric network identity (MCC+MNC), e.g. 26003 = Orange PL.</summary>
    public string ProviderId { get; set; } = "";

    /// <summary>Carrier roaming text, when registered to a partner network.</summary>
    public string RoamingText { get; set; } = "";

    /// <summary>Local time of the last successful status poll — the mobility
    /// heartbeat shown next to the LINK lamp.</summary>
    public DateTime? LastPollLocal { get; set; }

    /// <summary>0–5 bars for the signal meter (99 → 0).</summary>
    public int Bars => Rssi switch
    {
        >= 28 => 5,
        >= 20 => 4,
        >= 13 => 3,
        >= 7 => 2,
        >= 1 => 1,
        _ => 0,
    };

    public string RssiDbm => Rssi is >= 0 and <= 31 ? $"{-113 + Rssi * 2} dBm" : "—";
}

public static class ModemStatusExtensions
{
    public static string Describe(this SimState s) => s switch
    {
        SimState.Ready => "DETECTED",
        SimState.Absent => "NO SIM",
        SimState.PinRequired => "PIN",
        _ => "SIM ?",
    };

    public static string Describe(this NetState s) => s switch
    {
        NetState.Home => "REGISTERED",
        NetState.Roaming => "ROAMING",
        NetState.Searching => "SEARCHING",
        NetState.Denied => "DENIED",
        _ => "NET ?",
    };
}
