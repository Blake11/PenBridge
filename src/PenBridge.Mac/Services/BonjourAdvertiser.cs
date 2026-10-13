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
/// <para>Uses the DNS-SD C API: <c>DNSServiceRegister</c> (libSystem) with its replies delivered on the main dispatch
/// queue by <c>DNSServiceSetDispatchQueue</c>, so nothing polls. <c>NSNetService</c> is obsoleted since macOS 12
/// (CA1422 under warnings-as-errors), and Network.framework can only advertise a listener it owns, while the sockets
/// belong to Core's server. The registration lives in mDNSResponder until its ref is deallocated (<see cref="Stop"/>,
/// re-register) or the process exits.</para>
/// <para>The instance name is a DNS label: at most <see cref="MaxInstanceNameBytes"/> UTF-8 bytes, so it is
/// truncated on a code-point boundary; the TXT <c>name</c> keeps up to <see cref="BonjourTxtRecord.MaxNameBytes"/>.
/// On a conflict mDNSResponder renames the instance ("Name (2)") and reports the final name in the reply.</para>
/// <para>A reply error (e.g. <c>kDNSServiceErr_ServiceNotRunning</c> after an mDNSResponder restart) or a failed
/// registration deallocates the ref and re-registers after a one-shot main-queue delay of 1 s, doubling to 30 s; a
/// successful reply resets the delay.</para>
/// </remarks>
public sealed unsafe partial class BonjourAdvertiser : IDisposable
{
    /// <summary>The DNS-SD service type (also listed under <c>NSBonjourServices</c> in Info.plist).</summary>
    public const string ServiceType = "_penbridge._tcp";

    /// <summary>Longest DNS-SD instance name in UTF-8 bytes (one DNS label).</summary>
    public const int MaxInstanceNameBytes = 63;

    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger _log;
    private readonly GCHandle _self;
    private nint _serviceRef;
    private bool _wanted;
    private bool _disposed;
    private int _generation;
    private TimeSpan _retryDelay = InitialRetryDelay;
    private string? _name;
    private int _port;
    private byte[]? _fingerprint;

    public BonjourAdvertiser(ILogger<BonjourAdvertiser> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _self = GCHandle.Alloc(this, GCHandleType.Weak);
    }

    /// <summary>True while a registration ref is live (between a successful register and <see cref="Stop"/>).</summary>
    public bool IsPublished => _serviceRef != 0;

    /// <summary>
    /// Publishes, or re-publishes when the name, port or fingerprint changed. With <paramref name="force"/> it
    /// re-registers even if nothing changed (after a network change).
    /// </summary>
    /// <param name="name">The Mac's name: TXT <c>name</c> (≤ <see cref="BonjourTxtRecord.MaxNameBytes"/> bytes) and instance name (≤ <see cref="MaxInstanceNameBytes"/> bytes).</param>
    public void Publish(string name, int port, ReadOnlySpan<byte> fingerprint, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        ObjectDisposedException.ThrowIf(_disposed, this);
        name = Utf8Text.Truncate(name, BonjourTxtRecord.MaxNameBytes);
        if (!force && _wanted && _serviceRef != 0 && _port == port && _name == name && fingerprint.SequenceEqual(_fingerprint))
        {
            return;
        }

        _name = name;
        _port = port;
        _fingerprint = fingerprint.ToArray();
        _wanted = true;
        _retryDelay = InitialRetryDelay;
        _generation++;
        Register();
    }

    /// <summary>Withdraws the advert (on quit, sleep, or before re-publishing) and cancels a pending retry.</summary>
    public void Stop()
    {
        _wanted = false;
        _generation++;
        if (Deallocate())
        {
            Log.Withdrawn(_log, _port);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        _self.Free();
    }

    /// <summary>The TXT rdata: each entry as a length byte followed by <c>key=value</c> in UTF-8 (RFC 6763 §6).</summary>
    internal static byte[] EncodeTxt(BonjourTxtRecord record)
    {
        using var buffer = new MemoryStream();
        foreach (KeyValuePair<string, string> entry in record.ToEntries())
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

    private void Register()
    {
        Deallocate();
        byte[] txt = EncodeTxt(new BonjourTxtRecord(ProtocolVersion.Current, _fingerprint, _name!));
        string instance = Utf8Text.Truncate(_name!, MaxInstanceNameBytes);
        ushort port = (ushort)_port;
        nint serviceRef;
        int error;
        fixed (byte* txtPtr = txt)
        {
            error = DNSServiceRegister(
                &serviceRef,
                flags: 0,
                interfaceIndex: 0,
                instance,
                ServiceType,
                domain: null,
                host: null,
                BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(port) : port,
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
            Log.PublishFailed(_log, error);
            ScheduleRetry();
            return;
        }

        _serviceRef = serviceRef;
    }

    /// <summary>Deallocates the live ref (on the main queue it was scheduled on, so no reply runs afterwards).</summary>
    private bool Deallocate()
    {
        if (_serviceRef == 0)
        {
            return false;
        }

        DNSServiceRefDeallocate(_serviceRef);
        _serviceRef = 0;
        return true;
    }

    private void ScheduleRetry()
    {
        int generation = ++_generation;
        TimeSpan delay = _retryDelay;
        _retryDelay = TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
        Log.RetryScheduled(_log, delay.TotalSeconds);
        DispatchQueue.MainQueue.DispatchAfter(new DispatchTime(DispatchTime.Now, delay), () =>
        {
            if (!_disposed && _wanted && generation == _generation)
            {
                Register();
            }
        });
    }

    private void OnReply(nint serviceRef, int errorCode, string name, string domain)
    {
        if (_disposed || serviceRef != _serviceRef)
        {
            return;
        }

        if (errorCode == 0)
        {
            _retryDelay = InitialRetryDelay;
            Log.Published(_log, ServiceType, name, domain, _port);
            return;
        }

        Log.PublishFailed(_log, errorCode);

        // Leave the reply callback before deallocating its ref.
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (!_disposed && serviceRef == _serviceRef)
            {
                Deallocate();
                ScheduleRetry();
            }
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRegisterReply(nint serviceRef, uint flags, int errorCode, byte* name, byte* regType, byte* domain, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is BonjourAdvertiser self)
            {
                self.OnReply(serviceRef, errorCode, Marshal.PtrToStringUTF8((nint)name) ?? string.Empty, Marshal.PtrToStringUTF8((nint)domain) ?? string.Empty);
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

        [LoggerMessage(4, LogLevel.Information, "Bonjour re-registration in {Seconds} s")]
        public static partial void RetryScheduled(ILogger logger, double seconds);
    }
}
