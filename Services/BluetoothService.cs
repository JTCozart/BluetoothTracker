using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BluetoothTracker.Services;

public enum ScanMode
{
    Passive,    // Listen only — best for trackers that broadcast continuously
    Active,     // Send scan requests — surfaces device names and extra data
    Extended    // BLE 5.0 extended advertisements — for newer trackers
}

public record AdapterInfo(
    bool Found,
    string Address,
    bool IsLeSupported,
    bool IsCentralRoleSupported,
    bool IsExtendedAdvertisingSupported,
    bool IsAdvertisementOffloadSupported
)
{
    public static readonly AdapterInfo None = new(false, "", false, false, false, false);
}

public class BleDevice
{
    public ulong Address { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Rssi { get; set; }
    public DateTime LastSeen { get; set; }
    public BluetoothLEAdvertisementType AdvertisementType { get; set; }
    public List<string> ServiceUuids { get; set; } = new();
    public byte[] ManufacturerBytes { get; set; } = Array.Empty<byte>();
    public ushort ManufacturerId { get; set; }

    public string ManufacturerHex => ManufacturerBytes.Length > 0
        ? BitConverter.ToString(ManufacturerBytes).Replace("-", " ")
        : string.Empty;

    public string DecodedManufacturerInfo =>
        BleDecoder.DecodeManufacturerData(ManufacturerId, ManufacturerBytes);

    public string AddressString => string.Join(":",
        BitConverter.GetBytes(Address).Take(6).Reverse().Select(b => b.ToString("X2")));

    public string AdvTypeShort => AdvertisementType switch
    {
        BluetoothLEAdvertisementType.ConnectableUndirected   => "Conn",
        BluetoothLEAdvertisementType.ConnectableDirected     => "Conn-D",
        BluetoothLEAdvertisementType.ScannableUndirected     => "Scan",
        BluetoothLEAdvertisementType.NonConnectableUndirected => "NonConn",
        BluetoothLEAdvertisementType.ScanResponse            => "ScanRsp",
        _                                                    => "?"
    };

    public string AdvTypeBadge => AdvertisementType switch
    {
        BluetoothLEAdvertisementType.NonConnectableUndirected => "warning",   // trackers
        BluetoothLEAdvertisementType.ScanResponse             => "info",
        _                                                     => "secondary"
    };

    // Always pass db from the page — no fallback hardcoded names here
    public string GetManufacturerLabel(BleCompanyDatabase? db)
    {
        if (!HasManufacturerData) return string.Empty;
        var name = BleDecoder.GetCompanyName(ManufacturerId, db);
        return name.Length > 0 ? name : $"0x{ManufacturerId:X4}";
    }

    public bool IsNamed => !string.IsNullOrEmpty(Name);
    public bool HasManufacturerData => ManufacturerId > 0;

    public bool IsStale => (DateTime.UtcNow - LastSeen).TotalSeconds > 30;

    public string ProximityLabel => Rssi >= -60 ? "Very Close"
        : Rssi >= -75 ? "Close"
        : Rssi >= -90 ? "Near"
        : "Far";

    public string ProximityBadge => Rssi >= -60 ? "success"
        : Rssi >= -75 ? "info"
        : Rssi >= -90 ? "warning"
        : "danger";

    public int SignalPercent => Math.Clamp((Rssi + 100) * 100 / 60, 0, 100);
}

public class BluetoothService : IDisposable
{
    private BluetoothLEAdvertisementWatcher _watcher = new();

    private readonly Dictionary<ulong, BleDevice> _devices = new();
    private readonly object _lock = new();

    // GATT name resolution — tracks which addresses we've already tried
    private readonly HashSet<ulong> _nameResolved = new();
    private readonly SemaphoreSlim _nameSem = new(3, 3); // max 3 concurrent lookups

    public TimeSpan StaleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public event Action? DevicesChanged;
    public event Action? AdapterInfoLoaded;

    public bool IsScanning => _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;
    public ScanMode CurrentMode { get; private set; } = ScanMode.Passive;
    public string? LastError { get; private set; }
    public int TotalAdvertisementsReceived { get; private set; }
    public AdapterInfo Adapter { get; private set; } = AdapterInfo.None;

    // Breakdown of advertisement types received
    public Dictionary<string, int> AdvTypeCounts { get; } = new();

    public BluetoothService()
    {
        AttachHandlers();
        _ = LoadAdapterInfoAsync();
    }

    private async Task LoadAdapterInfoAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null)
            {
                Adapter = AdapterInfo.None;
            }
            else
            {
                var addr = string.Join(":",
                    BitConverter.GetBytes(adapter.BluetoothAddress).Take(6).Reverse()
                        .Select(b => b.ToString("X2")));
                Adapter = new AdapterInfo(
                    Found: true,
                    Address: addr,
                    IsLeSupported: adapter.IsLowEnergySupported,
                    IsCentralRoleSupported: adapter.IsCentralRoleSupported,
                    IsExtendedAdvertisingSupported: adapter.IsExtendedAdvertisingSupported,
                    IsAdvertisementOffloadSupported: adapter.IsAdvertisementOffloadSupported
                );
            }
        }
        catch (Exception ex)
        {
            LastError = $"Adapter query failed: {ex.Message}";
        }
        AdapterInfoLoaded?.Invoke();
    }

    // Start fresh scan — clears device cache
    public bool StartScanning(ScanMode mode)
    {
        RebuildWatcher(clearDevices: true);
        return ApplyModeAndStart(mode);
    }

    // Switch mode live — keeps existing device cache so the list doesn't blank out
    public bool SwitchMode(ScanMode mode)
    {
        if (mode == CurrentMode && IsScanning) return true;
        RebuildWatcher(clearDevices: false);
        return ApplyModeAndStart(mode);
    }

    public void StopScanning()
    {
        _watcher.Stop();
        DevicesChanged?.Invoke();
    }

    private bool ApplyModeAndStart(ScanMode mode)
    {
        CurrentMode = mode;
        _watcher.ScanningMode = mode == ScanMode.Active || mode == ScanMode.Extended
            ? BluetoothLEScanningMode.Active
            : BluetoothLEScanningMode.Passive;

        if (mode == ScanMode.Extended)
        {
            try { _watcher.AllowExtendedAdvertisements = true; }
            catch (Exception ex)
            {
                LastError = $"Extended advertisements not supported by this adapter: {ex.Message}";
                DevicesChanged?.Invoke();
                return false;
            }
        }

        try
        {
            LastError = null;
            _watcher.Start();
            DevicesChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            DevicesChanged?.Invoke();
            return false;
        }
    }

    public IReadOnlyList<BleDevice> GetDevices()
    {
        lock (_lock)
        {
            PruneStale();
            return _devices.Values.OrderByDescending(d => d.Rssi).ToList();
        }
    }

    public BleDevice? GetDevice(ulong address)
    {
        lock (_lock)
        {
            _devices.TryGetValue(address, out var device);
            return device;
        }
    }

    private void RebuildWatcher(bool clearDevices)
    {
        _watcher.Received -= OnReceived;
        _watcher.Stopped -= OnStopped;
        try { _watcher.Stop(); } catch { }

        _watcher = new BluetoothLEAdvertisementWatcher();
        AttachHandlers();

        if (clearDevices)
        {
            lock (_lock)
            {
                _devices.Clear();
                _nameResolved.Clear();
            }
        }

        TotalAdvertisementsReceived = 0;
        AdvTypeCounts.Clear();
    }

    private void AttachHandlers()
    {
        _watcher.Received += OnReceived;
        _watcher.Stopped += OnStopped;
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher _,
        BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error != BluetoothError.Success)
            LastError = $"Watcher stopped: {args.Error}";
        DevicesChanged?.Invoke();
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher _,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        TotalAdvertisementsReceived++;

        var typeKey = args.AdvertisementType.ToString();
        lock (_lock)
            AdvTypeCounts[typeKey] = AdvTypeCounts.GetValueOrDefault(typeKey) + 1;

        var name = args.Advertisement.LocalName?.Trim() ?? string.Empty;

        ushort mfrId = 0;
        byte[] mfrBytes = Array.Empty<byte>();
        foreach (var section in args.Advertisement.DataSections)
        {
            if (section.DataType != 0xFF || section.Data.Length < 2) continue;
            var reader = DataReader.FromBuffer(section.Data);
            // Read company ID bytes manually — BLE stores little-endian (low byte first)
            var lo = reader.ReadByte();
            var hi = reader.ReadByte();
            mfrId = (ushort)(lo | (hi << 8));
            mfrBytes = new byte[section.Data.Length - 2];
            reader.ReadBytes(mfrBytes);
            break;
        }

        var uuids = args.Advertisement.ServiceUuids.Select(g => g.ToString()).ToList();

        BleDevice device;
        lock (_lock)
        {
            if (_devices.TryGetValue(args.BluetoothAddress, out var existing))
            {
                existing.Rssi = args.RawSignalStrengthInDBm;
                existing.LastSeen = DateTime.UtcNow;
                existing.AdvertisementType = args.AdvertisementType;
                if (!string.IsNullOrEmpty(name)) existing.Name = name;
                if (mfrId > 0) { existing.ManufacturerId = mfrId; existing.ManufacturerBytes = mfrBytes; }
                if (uuids.Count > 0) existing.ServiceUuids = uuids;
                device = existing;
            }
            else
            {
                device = new BleDevice
                {
                    Address = args.BluetoothAddress,
                    Name = name,
                    Rssi = args.RawSignalStrengthInDBm,
                    LastSeen = DateTime.UtcNow,
                    AdvertisementType = args.AdvertisementType,
                    ManufacturerId = mfrId,
                    ManufacturerBytes = mfrBytes,
                    ServiceUuids = uuids
                };
                _devices[args.BluetoothAddress] = device;
            }
        }

        // Try to resolve a name from GATT for unnamed connectable devices
        if (!device.IsNamed)
            TryResolveNameInBackground(device);

        DevicesChanged?.Invoke();
    }

    // ── Background GATT name resolution ──────────────────────────────────────

    private void TryResolveNameInBackground(BleDevice device)
    {
        if (device.AdvertisementType != BluetoothLEAdvertisementType.ConnectableUndirected &&
            device.AdvertisementType != BluetoothLEAdvertisementType.ConnectableDirected)
            return;

        lock (_lock)
        {
            if (!_nameResolved.Add(device.Address)) return; // already tried or in progress
        }

        _ = Task.Run(async () =>
        {
            await _nameSem.WaitAsync();
            try
            {
                var resolvedName = await ReadGattDeviceNameAsync(device.Address);
                if (string.IsNullOrWhiteSpace(resolvedName)) return;

                lock (_lock)
                {
                    if (_devices.TryGetValue(device.Address, out var d) && !d.IsNamed)
                        d.Name = resolvedName;
                }
                DevicesChanged?.Invoke();
            }
            finally
            {
                _nameSem.Release();
            }
        });
    }

    private static async Task<string?> ReadGattDeviceNameAsync(ulong address)
    {
        var deviceNameUuid = Guid.Parse("00002a00-0000-1000-8000-00805f9b34fb");
        var genericAccessUuid = Guid.Parse("00001800-0000-1000-8000-00805f9b34fb");

        foreach (var addrType in new[] { BluetoothAddressType.Public, BluetoothAddressType.Random })
        {
            BluetoothLEDevice? device = null;
            try
            {
                // Race the connection against a 5-second timeout
                var connectTask = BluetoothLEDevice.FromBluetoothAddressAsync(address, addrType).AsTask();
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) != connectTask) continue;

                device = await connectTask;
                if (device is null) continue;

                var svcResult = await device.GetGattServicesForUuidAsync(
                    genericAccessUuid, BluetoothCacheMode.Uncached);
                if (svcResult.Status != GattCommunicationStatus.Success) continue;

                var svc = svcResult.Services.FirstOrDefault();
                if (svc is null) continue;

                var charResult = await svc.GetCharacteristicsForUuidAsync(
                    deviceNameUuid, BluetoothCacheMode.Uncached);

                if (charResult.Status != GattCommunicationStatus.Success)
                {
                    svc.Dispose();
                    continue;
                }

                var ch = charResult.Characteristics.FirstOrDefault();
                if (ch is null) { svc.Dispose(); continue; }

                var read = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
                svc.Dispose();

                if (read.Status != GattCommunicationStatus.Success) continue;

                var reader = DataReader.FromBuffer(read.Value);
                reader.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                var nameStr = reader.ReadString(read.Value.Length).Trim();
                if (!string.IsNullOrEmpty(nameStr)) return nameStr;
            }
            catch { /* device unreachable or declined — skip */ }
            finally { device?.Dispose(); }
        }
        return null;
    }

    private void PruneStale()
    {
        var cutoff = DateTime.UtcNow - StaleTimeout;
        foreach (var key in _devices.Keys.Where(k => _devices[k].LastSeen < cutoff).ToList())
            _devices.Remove(key);
    }

    public void Dispose()
    {
        _watcher.Received -= OnReceived;
        _watcher.Stopped -= OnStopped;
        try { _watcher.Stop(); } catch { }
    }
}
