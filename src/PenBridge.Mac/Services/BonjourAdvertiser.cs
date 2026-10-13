using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CoreFoundation;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Protocol;

namespace PenBridge.Mac.Services;

/// <summary>
/// Advertises the server as <c>_penbridge._tcp</c> with the TXT record from Core (<see cref="BonjourTxtRecord"/>:
/// <c>v</c>, <c>fp</c>, <c>name</c>). Main thread only.
/// </summary>
/// <remarks>
/// <para>Uses the DNS-SD C API (<c>DNSServiceRegister</c> in libSystem) with its replies on the main dispatch queue.
/// <c>NSNetService</c> is obsoleted since macOS 12 (CA1422 under warnings-as-errors), and Network.framework can only
/// advertise a listener it owns, while the sockets belong to Core's server. The registration lives in
/// mDNSResponder until <see cref="Stop"/> deallocates the service ref (or the process exits).</para>
/// <para>On a name conflict mDNSResponder renames the instance ("Name (2)") and reports the final name in the reply.</para>
/// </remarks>
public sealed unsafe partial class BonjourAdvertiser : IDisposable
{
    /// <summary>The DNS-SD service type (also listed under <c>NSBonjourServices</c> in Info.plist).</summary>
    public const string ServiceType = "_penbridge._tcp";

    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    private readonly ILogger _log;
    private nint _serviceRef;
    private GCHandle _self;
    private string? _name;
    private int _port;
    private byte[]? _fingerprint;

    public BonjourAdvertiser(ILogger<BonjourAdvertiser> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>True between a successful <see cref="Publish"/> and <see cref="Stop"/>.</summary>
    public bool IsPublished => _serviceRef != 0;

    /// <summary>
    /// Publishes, or re-publishes when the name, port or fingerprint changed. With <paramref name="force"/> it
    /// re-registers even if nothing changed (after a network change).
    /// </summary>
    /// <param name="name">Service instance name and TXT <c>name</c>; truncated to <see cref="BonjourTxtRecord.MaxNameBytes"/> UTF-8 bytes.</param>
    public void Publish(string name, int port, ReadOnlySpan<byte> fingerprint, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        name = TruncateUtf8(name, BonjourTxtRecord.MaxNameBytes);
        if (!force && _serviceRef != 0 && _port == port && _name == name && fingerprint.SequenceEqual(_fingerprint))
        {
            return;
        }

        Stop();
        byte[] txt = EncodeTxt(new BonjourTxtRecord(ProtocolVersion.Current, fingerprint.ToArray(), name));
        _self = GCHandle.Alloc(this);
        nint serviceRef;
        int error;
        fixed (byte* txtPtr = txt)
        {
            error = DNSServiceRegister(
                &serviceRef,
                flags: 0,
                interfaceIndex: 0,
                name,
                ServiceType,
                domain: null,
                host: null,
                BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness((ushort)port) : (ushort)port,
                (ushort)txt.Length,
                txtPtr,
                &OnRegisterReply,
                GCHandle.ToIntPtr(_self));
        }

        if (error == 0)
        {
            error = DNSServiceSetDispatchQueue(serviceRef, DispatchQueue.MainQueue.Handle);
            if (error != 0)
            {
                DNSServiceRefDeallocate(serviceRef);
            }
        }

        if (error != 0)
        {
            _self.Free();
            Log.PublishFailed(_log, error);
            return;
        }

        _serviceRef = serviceRef;
        _name = name;
        _port = port;
        _fingerprint = fingerprint.ToArray();
    }

    /// <summary>Withdraws the advert (on quit, sleep, or before re-publishing).</summary>
    public void Stop()
    {
        if (_serviceRef == 0)
        {
            return;
        }

        // On the queue the ref was scheduled on (main), so no reply can run after this.
        DNSServiceRefDeallocate(_serviceRef);
        _serviceRef = 0;
        _self.Free();
        Log.Withdrawn(_log, _port);
    }

    public void Dispose() => Stop();

    /// <summary>The TXT rdata: each entry as a length byte followed by <c>key=value</c> in UTF-8 (RFC 6763 §6).</summary>
    internal static byte[] EncodeTxt(BonjourTxtRecord record)
    {
        IReadOnlyList<KeyValuePair<string, string>> entries = record.ToEntries();
        using var buffer = new MemoryStream();
        foreach (KeyValuePair<string, string> entry in entries)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(entry.Key + "=" + entry.Value);
            if (bytes.Length > byte.MaxValue)
            {
                throw new ArgumentException("TXT entry exceeds 255 bytes.", nameof(record));
            }

            buffer.WriteByte((byte)bytes.Length);
            buffer.Write(bytes);
        }

        return buffer.ToArray();
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        int length = value.Length;
        while (length > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) > maxBytes)
        {
            length--;
            if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            {
                length--;
            }
        }

        return length == value.Length ? value : value[..length];
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRegisterReply(nint serviceRef, uint flags, int errorCode, byte* name, byte* regType, byte* domain, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not BonjourAdvertiser self || self._serviceRef != serviceRef)
            {
                return;
            }

            if (errorCode == 0)
            {
                Log.Published(self._log, ServiceType, Marshal.PtrToStringUTF8((nint)name) ?? string.Empty, Marshal.PtrToStringUTF8((nint)domain) ?? string.Empty, self._port);
            }
            else
            {
                Log.PublishFailed(self._log, errorCode);
            }
        }
#pragma warning disable CA1031 // An exception must not unwind into native code.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int DNSServiceRegister(
        nint* serviceRef,
        uint flags,
        uint interfaceIndex,
        string? name,
        string regType,
        string? domain,
        string? host,
        ushort portNetworkOrder,
        ushort txtLength,
        byte* txtRecord,
        delegate* unmanaged[Cdecl]<nint, uint, int, byte*, byte*, byte*, nint, void> callback,
        nint context);

    [LibraryImport(LibSystem)]
    private static partial int DNSServiceSetDispatchQueue(nint serviceRef, nint queue);

    [LibraryImport(LibSystem)]
    private static partial void DNSServiceRefDeallocate(nint serviceRef);

    private static partial class Log
    {
        [LoggerMessage(1, LogLevel.Information, "Bonjour advert published: {Type} \"{Name}\" in {Domain} on port {Port}")]
        public static partial void Published(ILogger logger, string type, string name, string domain, int port);

        [LoggerMessage(2, LogLevel.Error, "Bonjour advert failed (DNSServiceErrorType {Code})")]
        public static partial void PublishFailed(ILogger logger, int code);

        [LoggerMessage(3, LogLevel.Information, "Bonjour advert withdrawn (port {Port})")]
        public static partial void Withdrawn(ILogger logger, int port);
    }
}
