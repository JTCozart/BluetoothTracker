using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BluetoothTracker.Services;

public record CharacteristicValue(
    string ServiceName,
    string ServiceUuid,
    string CharacteristicName,
    string CharacteristicUuid,
    string Value,
    string RawHex
);

public static class GattCharacteristicReader
{
    public static async Task<(List<CharacteristicValue> Values, string? Error)> ReadAllAsync(
        ulong address,
        BluetoothAddressType addrType,
        BleCompanyDatabase? db = null)
    {
        BluetoothLEDevice? device = null;
        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addrType);
            if (device is null)
                return (new(), "Device not found — it may no longer be in connectable mode.");

            var svcResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (svcResult.Status != GattCommunicationStatus.Success)
                return (new(), $"Could not read services: {svcResult.Status}");

            var results = new List<CharacteristicValue>();

            foreach (var svc in svcResult.Services)
            {
                var svcName  = BleDecoder.GetServiceName(svc.Uuid, db);
                var svcUuid  = svc.Uuid.ToString();

                var charResult = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (charResult.Status != GattCommunicationStatus.Success)
                {
                    svc.Dispose();
                    continue;
                }

                foreach (var ch in charResult.Characteristics)
                {
                    // Only read characteristics that support Read
                    if (!ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
                        continue;

                    var chName = KnownCharacteristicName(ch.Uuid);
                    var chUuid = ch.Uuid.ToString();

                    try
                    {
                        var read = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
                        if (read.Status != GattCommunicationStatus.Success) continue;

                        var raw  = BufferToHex(read.Value);
                        var decoded = DecodeCharacteristic(ch.Uuid, read.Value);

                        results.Add(new CharacteristicValue(
                            string.IsNullOrEmpty(svcName) ? svcUuid[..8] + "…" : svcName,
                            svcUuid,
                            string.IsNullOrEmpty(chName) ? chUuid[..8] + "…" : chName,
                            chUuid,
                            decoded,
                            raw
                        ));
                    }
                    catch { /* unreadable — skip */ }
                }
                svc.Dispose();
            }

            return (results, null);
        }
        catch (Exception ex)
        {
            return (new(), ex.Message);
        }
        finally
        {
            device?.Dispose();
        }
    }

    // ── Characteristic decoder ───────────────────────────────────────────────

    public static string DecodeBuffer(Guid uuid, IBuffer buffer) => DecodeCharacteristic(uuid, buffer);

    private static string DecodeCharacteristic(Guid uuid, IBuffer buffer)
    {
        var short16 = Short16(uuid);
        try
        {
            var reader = DataReader.FromBuffer(buffer);
            return short16 switch
            {
                // Strings
                0x2A00 or 0x2A24 or 0x2A25 or 0x2A26 or 0x2A27 or 0x2A28 or 0x2A29 =>
                    ReadUtf8(reader, buffer.Length),

                // Battery level — uint8 percentage
                0x2A19 => $"{reader.ReadByte()} %",

                // Tx Power — int8 dBm
                0x2A07 => $"{(sbyte)reader.ReadByte()} dBm",

                // Appearance — uint16 category
                0x2A01 => AppearanceName(reader.ReadUInt16()),

                // Temperature — int16, units 0.01°C
                0x2A6E => $"{reader.ReadInt16() / 100.0:F1} °C",

                // Humidity — uint16, units 0.01%
                0x2A6F => $"{reader.ReadUInt16() / 100.0:F1} %",

                // Atmospheric pressure — uint32, units 0.1 Pa
                0x2A6D => $"{reader.ReadUInt32() / 10.0:F1} Pa",

                // Heart rate measurement — first byte is flags, second is BPM (if uint8 flag)
                0x2A37 => DecodeHeartRate(reader),

                // Step count — uint32
                0x2A56 => $"{reader.ReadUInt32()} steps",

                // Firmware / hardware revision strings already handled above
                // Current Time — 10 bytes: year(2) month day hours mins secs weekday(0=unknown) fractions(1/256s) adjust
                0x2A2B => DecodeCurrentTime(reader),

                _ => BufferToHex(buffer)
            };
        }
        catch
        {
            return BufferToHex(buffer);
        }
    }

    private static string ReadUtf8(DataReader reader, uint length)
    {
        reader.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
        var s = reader.ReadString(length);
        return string.IsNullOrWhiteSpace(s) ? "(empty)" : s;
    }

    private static string DecodeHeartRate(DataReader reader)
    {
        var flags = reader.ReadByte();
        var bpm = (flags & 0x01) == 0 ? reader.ReadByte() : reader.ReadUInt16();
        return $"{bpm} bpm";
    }

    private static string DecodeCurrentTime(DataReader reader)
    {
        if (reader.UnconsumedBufferLength < 7) return BufferToHex(reader.DetachBuffer());
        var year  = reader.ReadUInt16();
        var month = reader.ReadByte();
        var day   = reader.ReadByte();
        var hour  = reader.ReadByte();
        var min   = reader.ReadByte();
        var sec   = reader.ReadByte();
        return $"{year:D4}-{month:D2}-{day:D2} {hour:D2}:{min:D2}:{sec:D2}";
    }

    private static string AppearanceName(ushort value) => (value >> 6) switch
    {
        0x00 => "Unknown",
        0x01 => "Phone",
        0x02 => "Computer",
        0x03 => "Watch",
        0x04 => "Clock",
        0x05 => "Display",
        0x06 => "Remote Control",
        0x07 => "Eye Glasses",
        0x08 => "Tag / Tracker",
        0x09 => "Keyring",
        0x0A => "Media Player",
        0x0B => "Barcode Scanner",
        0x0C => "Thermometer",
        0x0D => "Heart Rate Sensor",
        0x0E => "Blood Pressure",
        0x0F => "HID Device",
        0x11 => "Glucose Meter",
        0x14 => "Cycling",
        0x31 => "Pulse Oximeter",
        0x32 => "Weight Scale",
        0x33 => "Personal Mobility",
        0x51 => "Insulin Pump",
        _ => $"Category 0x{value >> 6:X2}"
    };

    // ── Known characteristic names ────────────────────────────────────────────

    private static string KnownCharacteristicName(Guid uuid) => Short16(uuid) switch
    {
        0x2A00 => "Device Name",
        0x2A01 => "Appearance",
        0x2A04 => "Preferred Connection Parameters",
        0x2A07 => "Tx Power Level",
        0x2A19 => "Battery Level",
        0x2A24 => "Model Number",
        0x2A25 => "Serial Number",
        0x2A26 => "Firmware Revision",
        0x2A27 => "Hardware Revision",
        0x2A28 => "Software Revision",
        0x2A29 => "Manufacturer Name",
        0x2A2B => "Current Time",
        0x2A37 => "Heart Rate",
        0x2A38 => "Body Sensor Location",
        0x2A56 => "Digital (GPIO)",
        0x2A6D => "Pressure",
        0x2A6E => "Temperature",
        0x2A6F => "Humidity",
        0x2A99 => "Database Change Increment",
        0x2AAD => "Indoor Positioning",
        _ => string.Empty
    };

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ushort Short16(Guid uuid)
    {
        var b = uuid.ToByteArray();
        return (ushort)((b[1] << 8) | b[0]);
    }

    private static string BufferToHex(IBuffer buffer)
    {
        var reader = DataReader.FromBuffer(buffer);
        var bytes  = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return BitConverter.ToString(bytes).Replace("-", " ");
    }
}
