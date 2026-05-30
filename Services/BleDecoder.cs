namespace BluetoothTracker.Services;

public static class BleDecoder
{
    // ── Company name — database only, no hardcoded list ──────────────────────

    public static string GetCompanyName(ushort companyId, BleCompanyDatabase? db = null) =>
        db?.GetCompanyName(companyId) ?? string.Empty;

    // ── Manufacturer payload decoder — format parsing, not naming ────────────

    public static string DecodeManufacturerData(ushort companyId, byte[] payload)
    {
        if (payload.Length == 0) return string.Empty;
        return companyId switch
        {
            0x004C => DecodeApple(payload),
            0x00E0 => DecodeGoogle(payload),
            0x0006 => DecodeMicrosoft(payload),
            0x0075 => DecodeSamsung(payload),
            _ => string.Empty
        };
    }

    // ── Apple ────────────────────────────────────────────────────────────────

    private static string DecodeApple(byte[] d)
    {
        if (d.Length < 2) return string.Empty;
        return d[0] switch
        {
            0x01 => "Continuity (inter-device, proprietary)",
            0x02 => d.Length >= 23 ? DecodeIBeacon(d) : "iBeacon (short packet)",
            0x03 => "AirPrint",
            0x05 => "AirDrop",
            0x07 => DecodeProximityPairing(d),
            0x08 => "Siri Remote",
            0x09 => "AirPlay Target",
            0x0A => "AirPlay Source",
            0x0B => "Magic Switch",
            0x0C => "Handoff",
            0x0D => "Tethering Target",
            0x0E => "Tethering Source",
            0x10 => DecodeNearbyAction(d),
            0x12 => "Find My (encrypted payload)",
            _ => $"Type 0x{d[0]:X2}"
        };
    }

    private static string DecodeIBeacon(byte[] d)
    {
        try
        {
            var uuidBytes = new byte[16];
            Array.Copy(d, 2, uuidBytes, 0, 16);
            var uuid  = new Guid(uuidBytes);
            var major = (d[18] << 8) | d[19];
            var minor = (d[20] << 8) | d[21];
            var tx    = (sbyte)d[22];
            return $"iBeacon  Major {major}  Minor {minor}  TX {tx} dBm  {uuid}";
        }
        catch { return "iBeacon (parse error)"; }
    }

    private static string DecodeProximityPairing(byte[] d)
    {
        if (d.Length < 5) return "AirPods/Beats";
        var model = (d[3] << 8) | d[4];
        return model switch
        {
            0x2002 => "AirPods 1st gen",
            0x200F => "AirPods 2nd gen",
            0x2013 => "AirPods 3rd gen",
            0x200E => "AirPods Pro 1st gen",
            0x2014 => "AirPods Pro 2nd gen",
            0x200A => "AirPods Max",
            0x2009 => "Beats Solo³",
            0x2005 => "Beats X",
            0x200C => "Powerbeats Pro",
            0x200D => "Beats Studio³",
            _ => $"Apple audio (0x{model:X4})"
        };
    }

    private static string DecodeNearbyAction(byte[] d)
    {
        if (d.Length < 3) return "Apple Nearby";
        return d[2] switch
        {
            0x01 => "Nearby: AirPods setup",
            0x04 => "Nearby: Auto Wi-Fi Join",
            0x05 => "Nearby: Auto Hotspot",
            0x08 => "Nearby: Instant Hotspot",
            0x09 => "Nearby: Join This Network",
            0x20 => "Nearby: Apple Watch setup",
            0x27 => "Nearby: Apple TV setup",
            _ => $"Nearby action 0x{d[2]:X2}"
        };
    }

    // ── Google ───────────────────────────────────────────────────────────────

    private static string DecodeGoogle(byte[] d)
    {
        if (d.Length >= 3)
        {
            var modelId = (d[0] << 16) | (d[1] << 8) | d[2];
            return $"Fast Pair  Model 0x{modelId:X6}";
        }
        return "Nearby";
    }

    // ── Microsoft ────────────────────────────────────────────────────────────

    private static string DecodeMicrosoft(byte[] d)
    {
        if (d.Length < 1) return string.Empty;
        return d[0] switch
        {
            0x03 => "Swift Pair",
            _ => $"Scenario 0x{d[0]:X2}"
        };
    }

    // ── Samsung ──────────────────────────────────────────────────────────────

    private static string DecodeSamsung(byte[] d)
    {
        if (d.Length < 1) return string.Empty;
        return d[0] switch
        {
            0x01 => "SmartTag",
            0x02 => "Galaxy device",
            0x03 => "Buds / audio",
            0x42 => "SmartThings",
            _ => $"Device 0x{d[0]:X2}"
        };
    }

    // ── Service UUIDs ────────────────────────────────────────────────────────

    public static string GetServiceName(Guid uuid, BleCompanyDatabase? db = null)
    {
        var full = uuid.ToString().ToLowerInvariant();

        // 128-bit proprietary UUIDs not in the Bluetooth SIG registry
        if (full == "8d53dc1d-1db7-4cd3-868b-8a527460aa84") return "Google Find My Device";
        if (full == "15190001-12f4-c226-88ed-2ac5579f2a85") return "PebbleBee proprietary";
        if (full == "6e400001-b5a3-f393-e0a9-e50e24dcca9e") return "Nordic UART";

        var b      = uuid.ToByteArray();
        var short16 = (ushort)((b[1] << 8) | b[0]);

        // Apple private range — not in the SIG registry
        if (short16 >= 0xFC00 && short16 <= 0xFCFF)
            return $"Apple private (0x{short16:X4})";

        // Everything else — database only
        return db?.GetServiceName(short16.ToString("X4")) ?? string.Empty;
    }
}
