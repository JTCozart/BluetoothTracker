using System.Text.Json;
using System.Text.Json.Serialization;

namespace BluetoothTracker.Services;

public class BleCompanyDatabase
{
    private Dictionary<ushort, string> _companies = new();
    private Dictionary<string, string> _services  = new();  // keyed by 4-char hex e.g. "1800"

    public bool IsLoaded { get; private set; }
    public string? LoadError { get; private set; }
    public event Action? DatabaseLoaded;

    private static readonly string CacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "BluetoothTracker");

    private const string CompanyUrl = "https://raw.githubusercontent.com/NordicSemiconductor/" +
                                      "bluetooth-numbers-database/master/v1/company_ids.json";
    private const string ServiceUrl = "https://raw.githubusercontent.com/NordicSemiconductor/" +
                                      "bluetooth-numbers-database/master/v1/service_uuids.json";

    public BleCompanyDatabase() => _ = LoadAsync();

    // ── Public lookup ────────────────────────────────────────────────────────

    public string GetCompanyName(ushort code)
    {
        if (_companies.TryGetValue(code, out var name)) return name;
        return string.Empty;
    }

    public string GetServiceName(string shortUuid4)
    {
        if (_services.TryGetValue(shortUuid4.ToLowerInvariant(), out var name)) return name;
        return string.Empty;
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        Directory.CreateDirectory(CacheDir);

        var companies = await LoadJsonAsync<List<CompanyEntry>>(
            CompanyUrl, Path.Combine(CacheDir, "company_ids.json"));

        var services = await LoadJsonAsync<List<ServiceEntry>>(
            ServiceUrl, Path.Combine(CacheDir, "service_uuids.json"));

        if (companies is not null)
            _companies = companies
                .Where(c => c.Code >= 0 && c.Code <= 0xFFFF)
                .ToDictionary(c => (ushort)c.Code, c => c.Name);

        if (services is not null)
            _services = services
                .Where(s => s.Uuid?.Length == 4)
                .ToDictionary(s => s.Uuid!.ToLowerInvariant(), s => s.Name ?? string.Empty);

        IsLoaded = true;
        DatabaseLoaded?.Invoke();
    }

    private async Task<T?> LoadJsonAsync<T>(string url, string cachePath)
    {
        // Try network first, fall back to disk cache
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = await http.GetStringAsync(url);
            await File.WriteAllTextAsync(cachePath, json);
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch
        {
            // Network failed — try disk cache
            if (File.Exists(cachePath))
            {
                try
                {
                    var cached = await File.ReadAllTextAsync(cachePath);
                    return JsonSerializer.Deserialize<T>(cached, JsonOpts);
                }
                catch { }
            }
            LoadError = "Could not load BLE database (offline and no cache yet).";
            return default;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ── JSON models ──────────────────────────────────────────────────────────

    private record CompanyEntry(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("name")] string Name);

    private record ServiceEntry(
        [property: JsonPropertyName("uuid")]  string? Uuid,
        [property: JsonPropertyName("name")]  string? Name,
        [property: JsonPropertyName("id")]    string? Id);
}
