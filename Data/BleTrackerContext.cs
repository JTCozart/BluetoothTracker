using Microsoft.EntityFrameworkCore;

namespace BluetoothTracker.Data;

public class BleTrackerContext : DbContext
{
    public BleTrackerContext(DbContextOptions<BleTrackerContext> options) : base(options) { }

    public DbSet<TrackedDevice>    Devices         { get; set; }
    public DbSet<DeviceStats>      DeviceStats     { get; set; }
    public DbSet<MonitoringSession> Sessions       { get; set; }
    public DbSet<ProximityLog>     ProximityLogs   { get; set; }
    public DbSet<GattSnapshot>     GattSnapshots   { get; set; }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<TrackedDevice>()
            .HasIndex(d => d.Address)
            .IsUnique();

        model.Entity<DeviceStats>()
            .HasOne(s => s.Device)
            .WithOne(d => d.Stats)
            .HasForeignKey<DeviceStats>(s => s.DeviceId);

        model.Entity<ProximityLog>()
            .HasIndex(p => new { p.DeviceId, p.Timestamp });

        model.Entity<GattSnapshot>()
            .HasIndex(g => new { g.DeviceId, g.Timestamp });
    }
}
