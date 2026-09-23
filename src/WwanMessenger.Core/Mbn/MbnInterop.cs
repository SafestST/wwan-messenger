using System.Runtime.InteropServices;

namespace WwanMessenger.Core.Mbn;

// ---------------------------------------------------------------------------
// Hand-transcribed interop for the Windows Mobile Broadband COM API (mbnapi),
// transcribed verbatim from the Windows SDK header (tools/mbnapi.h) and the
// CLSID registration on the target machine (wwanapi.dll).
// Interfaces are declared only as deep as the methods actually called —
// vtable truncation is safe for that purpose.
// ---------------------------------------------------------------------------

/// <summary>Coclass "Mbn Interface Manager" — InProcServer32 = wwanapi.dll.</summary>
[ComImport]
[Guid("BDFEE05B-4418-11DD-90ED-001C257CCFF1")]
public class MbnInterfaceManagerCoClass
{
}

[ComImport]
[Guid("DCBBBAB6-201B-4BBB-AAEE-338E368AF6FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMbnInterfaceManager
{
    void GetInterface(
        [MarshalAs(UnmanagedType.LPWStr)] string interfaceID,
        out IMbnInterface mbnInterface);

    void GetInterfaces(
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_UNKNOWN)]
        out IMbnInterface[] mbnInterfaces);
}

/// <summary>First three vtable slots only — identity/caps/subscriber.</summary>
[ComImport]
[Guid("DCBBBAB6-2001-4BBB-AAEE-338E368AF6FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMbnInterface
{
    void get_InterfaceID([MarshalAs(UnmanagedType.BStr)] out string interfaceID);
    void GetInterfaceCapability(out MbnInterfaceCaps interfaceCaps);
    void GetSubscriberInformation(out IMbnSubscriberInformation subscriberInformation);
}

[ComImport]
[Guid("DCBBBAB6-2003-4BBB-AAEE-338E368AF6FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMbnSignal
{
    void GetSignalStrength(out uint signalStrength);
    void GetSignalError(out uint signalError);
}

/// <summary>Seven vtable slots in header order: state, mode, provider id/name,
/// roaming text, available + current data classes.</summary>
[ComImport]
[Guid("DCBBBAB6-2009-4BBB-AAEE-338E368AF6FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMbnRegistration
{
    void GetRegisterState(out int registerState);   // 0 none 1 dereg 2 searching 3 home 4 roaming 5 partner 6 denied
    void GetRegisterMode(out int registerMode);
    void GetProviderID([MarshalAs(UnmanagedType.BStr)] out string providerID);
    void GetProviderName([MarshalAs(UnmanagedType.BStr)] out string providerName);
    void GetRoamingText([MarshalAs(UnmanagedType.BStr)] out string roamingText);
    void GetAvailableDataClasses(out uint availableDataClasses);
    void GetCurrentDataClass(out uint currentDataClass);
}

[ComImport]
[Guid("459ECC43-BCF5-11DC-A8A8-001321F1405F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMbnSubscriberInformation
{
    void get_SubscriberID([MarshalAs(UnmanagedType.BStr)] out string subscriberID);
    void get_SimIccID([MarshalAs(UnmanagedType.BStr)] out string simIccID);
    void get_TelephoneNumbers(
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)]
        out string[] telephoneNumbers);
}

/// <summary>MBN_INTERFACE_CAPS — enums/ULONGs + BSTRs, marshaled natively.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MbnInterfaceCaps
{
    public int CellularClass;
    public int VoiceClass;
    public uint DataClass;
    [MarshalAs(UnmanagedType.BStr)] public string? CustomDataClass;
    public uint GsmBandClass;
    public uint CdmaBandClass;
    [MarshalAs(UnmanagedType.BStr)] public string? CustomBandClass;
    public uint SmsCaps;        // 1=PDU_RECV 2=PDU_SEND 4=TEXT_RECV 8=TEXT_SEND
    public uint ControlCaps;
    [MarshalAs(UnmanagedType.BStr)] public string? DeviceID;
    [MarshalAs(UnmanagedType.BStr)] public string? Manufacturer;
    [MarshalAs(UnmanagedType.BStr)] public string? Model;
    [MarshalAs(UnmanagedType.BStr)] public string? FirmwareInfo;
}

[ComImport]
[Guid("DCBBBAB6-2012-4BBB-AAEE-338E368AF6FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMbnSmsConfiguration
{
    void get_ServiceCenterAddress([MarshalAs(UnmanagedType.BStr)] out string scAddress);
    void put_ServiceCenterAddress([MarshalAs(UnmanagedType.LPWStr)] string scAddress);
    void get_MaxMessageIndex(out uint index);
    void get_CdmaShortMsgSize(out uint shortMsgSize);
    void get_SmsFormat(out int smsFormat);
    void put_SmsFormat(int smsFormat);
}

[ComImport]
[Guid("DCBBBAB6-2013-4BBB-AAEE-338E368AF6FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMbnSmsReadMsgPdu
{
    void get_Index(out uint index);
    void get_Status(out int status);          // 0 NEW 1 OLD 2 DRAFT 3 SENT
    void get_PduData([MarshalAs(UnmanagedType.BStr)] out string pduData);
    void get_Message([MarshalAs(UnmanagedType.SafeArray)] out Array message);
}

[ComImport]
[Guid("DCBBBAB6-2015-4BBB-AAEE-338E368AF6FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMbnSms
{
    void GetSmsConfiguration(out IMbnSmsConfiguration smsConfiguration);
    void SetSmsConfiguration(IMbnSmsConfiguration smsConfiguration, out uint requestID);
    void SmsSendPdu([MarshalAs(UnmanagedType.LPWStr)] string pduData, byte size, out uint requestID);
    void SmsSendCdma([MarshalAs(UnmanagedType.LPWStr)] string address, int encoding, int language,
        uint sizeInCharacters, [MarshalAs(UnmanagedType.SafeArray)] Array message, out uint requestID);
    void SmsSendCdmaPdu([MarshalAs(UnmanagedType.SafeArray)] Array message, out uint requestID);
    void SmsRead(ref MbnSmsFilter smsFilter, int smsFormat, out uint requestID);
    void SmsDelete(ref MbnSmsFilter smsFilter, out uint requestID);
    void GetSmsStatus(out MbnSmsStatusInfo smsStatusInfo);
}

/// <summary>MBN_SMS_FILTER — native layout is exactly two fields:
/// { MBN_SMS_FLAG flag; ULONG messageIndex; } (flag: 0 ALL, 1 INDEX,
/// 2 NEW, 3 OLD). No sender-address field exists in the native struct.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MbnSmsFilter
{
    public int Flag;
    public uint Index;
}

/// <summary>MBN_SMS_STATUS_INFO — flag + message index.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MbnSmsStatusInfo
{
    public int Flag;
    public uint Index;
}

/// <summary>IMbnSmsEvents — exact vtable order from mbnapi.h.</summary>
[ComVisible(true)]
[Guid("DCBBBAB6-2016-4BBB-AAEE-338E368AF6FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMbnSmsEventsSink
{
    void OnSmsConfigurationChange([MarshalAs(UnmanagedType.Interface)] object sms);
    void OnSetSmsConfigurationComplete([MarshalAs(UnmanagedType.Interface)] object sms, uint requestID, int status);
    void OnSmsSendComplete([MarshalAs(UnmanagedType.Interface)] object sms, uint requestID, int status);
    void OnSmsReadComplete([MarshalAs(UnmanagedType.Interface)] object sms, int smsFormat,
        [MarshalAs(UnmanagedType.SafeArray)] Array readMsgs,
        [MarshalAs(UnmanagedType.VariantBool)] bool moreMsgs, uint requestID, int status);
    void OnSmsNewClass0Message([MarshalAs(UnmanagedType.Interface)] object sms, int smsFormat,
        [MarshalAs(UnmanagedType.SafeArray)] Array readMsgs);
    void OnSmsDeleteComplete([MarshalAs(UnmanagedType.Interface)] object sms, uint requestID, int status);
    void OnSmsStatusChange([MarshalAs(UnmanagedType.Interface)] object sms);
}

/// <summary>IMbnSignalEvents — pushed by the driver whenever signal quality
/// changes (typically every few seconds while connected), so the meter is
/// live rather than poll-paced.</summary>
[ComVisible(true)]
[Guid("DCBBBAB6-2004-4BBB-AAEE-338E368AF6FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMbnSignalEventsSink
{
    void OnSignalStateChange([MarshalAs(UnmanagedType.Interface)] object signal);
}

public sealed class SignalEventsSink : IMbnSignalEventsSink
{
    /// <summary>Raised on every signal-state push from the driver.</summary>
    public event Action? SignalChanged;
    void IMbnSignalEventsSink.OnSignalStateChange(object signal) => SignalChanged?.Invoke();
}

public sealed class SmsEventsSink : IMbnSmsEventsSink
{
    public uint ExpectedRequestId;   // logging only — NEVER used for matching
    /// <summary>Single-flight flag: exactly one send is in flight at a time
    /// (the service's COM queue serialises sends), so ANY send completion
    /// observed while this is set belongs to that send. Matching by requestID
    /// is a race: completions can arrive before the caller stores the ID.</summary>
    public volatile bool SendInFlight;
    public readonly ManualResetEvent SendCompleted = new(false);
    public int SendStatus = int.MinValue;

    public volatile bool ConfigurationSignalled;
    public readonly ManualResetEvent ConfigurationEvent = new(false);

    public readonly ManualResetEvent ReadCompleted = new(false);
    public int ReadStatus = int.MinValue;
    public Array? ReadMessages;

    public volatile bool StoreChanged;
    public readonly ManualResetEvent StoreChangedEvent = new(false);

    /// <summary>Optional verbose log hook (console drawer).</summary>
    public event Action<string>? EventLogged;

    private void Log(string line) => EventLogged?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {line}");

    void IMbnSmsEventsSink.OnSmsConfigurationChange(object sms)
    {
        ConfigurationSignalled = true;
        ConfigurationEvent.Set();
        Log("OnSmsConfigurationChange — SMS configuration available");
    }

    void IMbnSmsEventsSink.OnSetSmsConfigurationComplete(object sms, uint requestID, int status)
    {
        ConfigurationSignalled = true;
        ConfigurationEvent.Set();
        Log($"OnSetSmsConfigurationComplete status=0x{status:X8}");
    }

    void IMbnSmsEventsSink.OnSmsSendComplete(object sms, uint requestID, int status)
    {
        Log($"OnSmsSendComplete request={requestID} status=0x{status:X8}");
        if (SendInFlight)
        {
            SendStatus = status;
            SendCompleted.Set();
        }
    }

    void IMbnSmsEventsSink.OnSmsReadComplete(object sms, int smsFormat, Array readMsgs, bool moreMsgs, uint requestID, int status)
    {
        // This firmware's completion requestIDs do not always match the
        // SmsRead out-param — accept any read completion.
        Log($"OnSmsReadComplete count={(readMsgs?.Length ?? 0)} more={moreMsgs} status=0x{status:X8}");
        ReadMessages = readMsgs;
        ReadStatus = status;
        ReadCompleted.Set();
    }

    void IMbnSmsEventsSink.OnSmsNewClass0Message(object sms, int smsFormat, Array readMsgs)
    {
        Log($"OnSmsNewClass0Message count={(readMsgs?.Length ?? 0)}");
        Class0Messages?.Invoke(readMsgs!);
    }

    void IMbnSmsEventsSink.OnSmsDeleteComplete(object sms, uint requestID, int status)
        => Log($"OnSmsDeleteComplete status=0x{status:X8}");

    void IMbnSmsEventsSink.OnSmsStatusChange(object sms)
    {
        Log("OnSmsStatusChange — message store changed");
        StoreChanged = true;
        StoreChangedEvent.Set();
    }

    /// <summary>Class-0 (flash) messages pushed directly by the network.</summary>
    public event Action<Array>? Class0Messages;
}
