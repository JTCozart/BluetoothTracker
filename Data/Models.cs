using System.ComponentModel.DataAnnotations;

namespace BluetoothTracker.Data;

public class TrackedDevice
{
    public int Id { get; set; }

    [MaxLength(17)]
    public string Address { get; set; } = string.Empty;  // "AA:BB:CC:DD:EE:FF"

    [MaxLength(128)]
    public string? Name { get; set; }

    public ushort ManufacturerId { get; set; }

    [MaxLength(128)]
    public string? ManufacturerName { get; set; }

    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }

    [MaxLength(512)]
    public string? Notes { get; set; }

    public bool IsFavorite { get; set; }

    // Navigation
    public DeviceStats? Stats { get; set; }
    public List<MonitoringSession> Sessions { get; set; } = new();
    public List<ProximityLog> ProximityLogs { get; set; } = new();
    public List<GattSnapshot> GattSnapshots { get; set; } = new();
}

public class DeviceStats
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public TrackedDevice Device { get; set; } = null!;

    public int TotalSightings { get; set; }
    public int TotalSessions { get; set; }
    public double AverageRssi { get; set; }
    public int MinRssi { get; set; }
    public int MaxRssi { get; set; }
    public long TotalSecondsMonitored { get; set; }

    [MaxLength(16)]
    public string? LastProximityLabel { get; set; }
}

public class MonitoringSession
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public TrackedDevice Device { get; set; } = null!;

    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    [MaxLength(16)]
    public string? ScanMode { get; set; }

    public int ReadingCount { get; set; }
    public double AverageRssi { get; set; }
    public int MinRssi { get; set; }
    public int MaxRssi { get; set; }

    public TimeSpan? Duration => EndedAt.HasValue ? EndedAt.Value - StartedAt : null;
}

public class ProximityLog
{
    public long Id { get; set; }
    public int DeviceId { get; set; }
    public TrackedDevice Device { get; set; } = null!;

    public DateTime Timestamp { get; set; }
    public int Rssi { get; set; }

    [MaxLength(16)]
    public string? ProximityLabel { get; set; }

    [MaxLength(16)]
    public string? AdvType { get; set; }
}

public class GattSnapshot
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public TrackedDevice Device { get; set; } = null!;

    public DateTime Timestamp { get; set; }

    [MaxLength(36)]
    public string ServiceUuid { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? ServiceName { get; set; }

    [MaxLength(36)]
    public string CharacteristicUuid { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? CharacteristicName { get; set; }

    [MaxLength(512)]
    public string? Value { get; set; }

    [MaxLength(512)]
    public string? RawHex { get; set; }
}
