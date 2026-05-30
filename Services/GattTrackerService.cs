using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BluetoothTracker.Services;

public enum TrackingStatus { Idle, Connecting, InRange, OutOfRange }

public record TrackingEvent(DateTime Time, bool InRange);

public class GattTrackerService : IDisposable
{
    private CancellationTokenSource? _cts;

    public TrackingStatus Status { get; private set; } = TrackingStatus.Idle;
    public ulong TrackedAddress { get; private set; }
    public BluetoothAddressType AddressType { get; private set; } = BluetoothAddressType.Random;
    public DateTime? LastSeen { get; private set; }
    public int? BatteryLevel { get; private set; }
    public string? LastError { get; private set; }
    public bool IsTracking => _cts is not null && !_cts.IsCancellationRequested;

    // Rolling 60-entry history (one per poll)
    private readonly List<TrackingEvent> _history = new();
    public IReadOnlyList<TrackingEvent> History { get { lock (_history) return _history.ToList(); } }

    public event Action? StatusChanged;

    // How often to poll. 15s is a reasonable balance — quick enough to detect leaving, gentle on battery.
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    public void StartTracking(ulong address, BluetoothAddressType addrType = BluetoothAddressType.Random)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        TrackedAddress = address;
        AddressType = addrType;
        Status = TrackingStatus.Connecting;
        LastError = null;
        lock (_history) _history.Clear();

        _ = TrackLoop(_cts.Token);
    }

    public void StopTracking()
    {
        _cts?.Cancel();
        Status = TrackingStatus.Idle;
        StatusChanged?.Invoke();
    }

    private async Task TrackLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Status = TrackingStatus.Connecting;
            StatusChanged?.Invoke();

            var (inRange, battery, error) = await TryConnectAsync(TrackedAddress, AddressType);

            if (ct.IsCancellationRequested) break;

            Status = inRange ? TrackingStatus.InRange : TrackingStatus.OutOfRange;
            LastError = error;
            if (inRange)
            {
                LastSeen = DateTime.UtcNow;
                BatteryLevel = battery;
            }

            lock (_history)
            {
                _history.Add(new TrackingEvent(DateTime.UtcNow, inRange));
                // Keep last 60 events
                if (_history.Count > 60)
                    _history.RemoveAt(0);
            }

            StatusChanged?.Invoke();

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task<(bool inRange, int? battery, string? error)> TryConnectAsync(
        ulong address, BluetoothAddressType addrType)
    {
        BluetoothLEDevice? device = null;
        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addrType);
            if (device is null)
                return (false, null, "Device not found at this address");

            var svcResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (svcResult.Status != GattCommunicationStatus.Success)
            {
                return (false, null, $"GATT failed: {svcResult.Status} — press button 5× to activate");
            }

            int? battery = await TryReadBatteryAsync(svcResult.Services);

            foreach (var s in svcResult.Services) s.Dispose();
            return (true, battery, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static async Task<int?> TryReadBatteryAsync(
        IReadOnlyList<GattDeviceService> services)
    {
        var svc = services.FirstOrDefault(s => Short16(s.Uuid) == 0x180F);
        if (svc is null) return null;

        try
        {
            var charResult = await svc.GetCharacteristicsForUuidAsync(
                Guid.Parse("00002a19-0000-1000-8000-00805f9b34fb"),
                BluetoothCacheMode.Uncached);

            if (charResult.Status != GattCommunicationStatus.Success) return null;
            var ch = charResult.Characteristics.FirstOrDefault();
            if (ch is null) return null;

            var read = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (read.Status != GattCommunicationStatus.Success) return null;

            var reader = DataReader.FromBuffer(read.Value);
            return reader.ReadByte();
        }
        catch { return null; }
    }

    private static ushort Short16(Guid uuid)
    {
        var b = uuid.ToByteArray();
        return (ushort)((b[1] << 8) | b[0]);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
