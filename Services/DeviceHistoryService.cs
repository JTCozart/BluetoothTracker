using BluetoothTracker.Data;
using Microsoft.EntityFrameworkCore;

namespace BluetoothTracker.Services;

public class DeviceHistoryService(IDbContextFactory<BleTrackerContext> factory)
{
    // ── Device upsert ─────────────────────────────────────────────────────────

    public async Task<TrackedDevice> UpsertDeviceAsync(
        string address, string? name, ushort manufacturerId, string? manufacturerName)
    {
        await using var db = await factory.CreateDbContextAsync();

        var device = await db.Devices
            .Include(d => d.Stats)
            .FirstOrDefaultAsync(d => d.Address == address);

        if (device is null)
        {
            device = new TrackedDevice
            {
                Address          = address,
                Name             = name,
                ManufacturerId   = manufacturerId,
                ManufacturerName = manufacturerName,
                FirstSeen        = DateTime.UtcNow,
                LastSeen         = DateTime.UtcNow,
                Stats            = new DeviceStats { MinRssi = 0, MaxRssi = -127 }
            };
            db.Devices.Add(device);
        }
        else
        {
            if (!string.IsNullOrEmpty(name))         device.Name             = name;
            if (!string.IsNullOrEmpty(manufacturerName)) device.ManufacturerName = manufacturerName;
            device.LastSeen = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        return device;
    }

    // ── Proximity logging (throttled — caller decides when to call) ───────────

    public async Task LogProximityAsync(
        string address, int rssi, string proximityLabel, string advType)
    {
        await using var db = await factory.CreateDbContextAsync();

        var device = await db.Devices
            .Include(d => d.Stats)
            .FirstOrDefaultAsync(d => d.Address == address);

        if (device is null) return;

        db.ProximityLogs.Add(new ProximityLog
        {
            DeviceId       = device.Id,
            Timestamp      = DateTime.UtcNow,
            Rssi           = rssi,
            ProximityLabel = proximityLabel,
            AdvType        = advType
        });

        // Update stats
        if (device.Stats is not null)
        {
            device.Stats.TotalSightings++;
            device.Stats.LastProximityLabel = proximityLabel;

            if (device.Stats.TotalSightings == 1)
            {
                device.Stats.AverageRssi = rssi;
                device.Stats.MinRssi = rssi;
                device.Stats.MaxRssi = rssi;
            }
            else
            {
                device.Stats.AverageRssi =
                    (device.Stats.AverageRssi * (device.Stats.TotalSightings - 1) + rssi)
                    / device.Stats.TotalSightings;
                if (rssi < device.Stats.MinRssi) device.Stats.MinRssi = rssi;
                if (rssi > device.Stats.MaxRssi) device.Stats.MaxRssi = rssi;
            }
        }

        device.LastSeen = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    // ── Sessions ──────────────────────────────────────────────────────────────

    public async Task<int> StartSessionAsync(string address, string scanMode)
    {
        await using var db = await factory.CreateDbContextAsync();

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Address == address);
        if (device is null) return -1;

        var session = new MonitoringSession
        {
            DeviceId  = device.Id,
            StartedAt = DateTime.UtcNow,
            ScanMode  = scanMode,
            MinRssi   = 0,
            MaxRssi   = -127
        };
        db.Sessions.Add(session);

        if (device.Stats is not null)
            device.Stats.TotalSessions++;

        await db.SaveChangesAsync();
        return session.Id;
    }

    public async Task EndSessionAsync(int sessionId, int readingCount, double avgRssi, int min, int max)
    {
        await using var db = await factory.CreateDbContextAsync();

        var session = await db.Sessions
            .Include(s => s.Device).ThenInclude(d => d.Stats)
            .FirstOrDefaultAsync(s => s.Id == sessionId);

        if (session is null) return;

        session.EndedAt      = DateTime.UtcNow;
        session.ReadingCount = readingCount;
        session.AverageRssi  = avgRssi;
        session.MinRssi      = min;
        session.MaxRssi      = max;

        if (session.Device.Stats is not null && session.Duration.HasValue)
            session.Device.Stats.TotalSecondsMonitored +=
                (long)session.Duration.Value.TotalSeconds;

        await db.SaveChangesAsync();
    }

    // ── GATT snapshots ────────────────────────────────────────────────────────

    public async Task SaveGattSnapshotAsync(
        string address, IEnumerable<CharacteristicValue> values)
    {
        await using var db = await factory.CreateDbContextAsync();

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Address == address);
        if (device is null) return;

        var ts = DateTime.UtcNow;
        foreach (var v in values)
        {
            db.GattSnapshots.Add(new GattSnapshot
            {
                DeviceId           = device.Id,
                Timestamp          = ts,
                ServiceUuid        = v.ServiceUuid,
                ServiceName        = v.ServiceName,
                CharacteristicUuid = v.CharacteristicUuid,
                CharacteristicName = v.CharacteristicName,
                Value              = v.Value,
                RawHex             = v.RawHex
            });
        }

        await db.SaveChangesAsync();
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    public async Task<List<TrackedDevice>> GetAllDevicesAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Devices
            .Include(d => d.Stats)
            .OrderByDescending(d => d.LastSeen)
            .ToListAsync();
    }

    public async Task<TrackedDevice?> GetDeviceAsync(string address)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Devices
            .Include(d => d.Stats)
            .Include(d => d.Sessions)
            .FirstOrDefaultAsync(d => d.Address == address);
    }

    public async Task<List<ProximityLog>> GetProximityLogsAsync(string address, int limit = 200)
    {
        await using var db = await factory.CreateDbContextAsync();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Address == address);
        if (device is null) return new();
        return await db.ProximityLogs
            .Where(p => p.DeviceId == device.Id)
            .OrderByDescending(p => p.Timestamp)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<GattSnapshot>> GetLatestGattSnapshotAsync(string address)
    {
        await using var db = await factory.CreateDbContextAsync();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Address == address);
        if (device is null) return new();

        // Get the most recent snapshot timestamp, then all rows for that timestamp
        var latest = await db.GattSnapshots
            .Where(g => g.DeviceId == device.Id)
            .MaxAsync(g => (DateTime?)g.Timestamp);

        if (latest is null) return new();

        return await db.GattSnapshots
            .Where(g => g.DeviceId == device.Id && g.Timestamp == latest)
            .OrderBy(g => g.ServiceName).ThenBy(g => g.CharacteristicName)
            .ToListAsync();
    }

    // ── Device management ─────────────────────────────────────────────────────

    public async Task UpdateNotesAsync(string address, string notes)
    {
        await using var db = await factory.CreateDbContextAsync();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Address == address);
        if (device is null) return;
        device.Notes = notes;
        await db.SaveChangesAsync();
    }

    public async Task ToggleFavoriteAsync(string address)
    {
        await using var db = await factory.CreateDbContextAsync();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Address == address);
        if (device is null) return;
        device.IsFavorite = !device.IsFavorite;
        await db.SaveChangesAsync();
    }

    public async Task DeleteDeviceAsync(string address)
    {
        await using var db = await factory.CreateDbContextAsync();
        var device = await db.Devices
            .Include(d => d.Stats)
            .Include(d => d.Sessions)
            .Include(d => d.ProximityLogs)
            .Include(d => d.GattSnapshots)
            .FirstOrDefaultAsync(d => d.Address == address);
        if (device is null) return;
        db.Devices.Remove(device);
        await db.SaveChangesAsync();
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public async Task PruneOldLogsAsync(int keepDays = 30)
    {
        await using var db = await factory.CreateDbContextAsync();
        var cutoff = DateTime.UtcNow.AddDays(-keepDays);
        await db.ProximityLogs
            .Where(p => p.Timestamp < cutoff)
            .ExecuteDeleteAsync();
        await db.GattSnapshots
            .Where(g => g.Timestamp < cutoff)
            .ExecuteDeleteAsync();
    }
}
