using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace DynamicIsland.Services;

/// <summary>Which weather symbol to show. The symbols themselves are vector drawings in MainWindow.xaml ("Weather.Sun", …).</summary>
public enum WeatherKind { Sun, Moon, CloudSun, CloudMoon, Cloud, Fog, Drizzle, Rain, Snow, Thunder }

public sealed record WeatherInfo(int Temperature, WeatherKind Kind, string Description);

/// <summary>
/// Optional current weather from Open-Meteo (free, no API key, no account). The window only creates this
/// service when a city is configured, so with weather off the HTTP and JSON stacks are never even loaded.
/// </summary>
public sealed class WeatherService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _place;
    private readonly bool _fahrenheit;
    private (double Lat, double Lon)? _location;

    /// <param name="place">A city name ("Warsaw") or "lat,lon" coordinates ("52.23,21.01").</param>
    public WeatherService(string place, bool fahrenheit)
    {
        _place = place;
        _fahrenheit = fahrenheit;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/1.1");
    }

    /// <summary>Returns null when offline or the place cannot be found; the island then simply hides the weather.</summary>
    public async Task<WeatherInfo?> GetCurrentAsync()
    {
        try
        {
            _location ??= ParseCoordinates(_place) ?? await GeocodeAsync(_place);
            if (_location is not { } loc)
                return null;

            string url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/forecast?latitude={loc.Lat:0.####}&longitude={loc.Lon:0.####}" +
                $"&current=temperature_2m,weather_code,is_day&temperature_unit={(_fahrenheit ? "fahrenheit" : "celsius")}");

            using JsonDocument doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            JsonElement current = doc.RootElement.GetProperty("current");
            int temperature = (int)Math.Round(current.GetProperty("temperature_2m").GetDouble());
            int code = current.GetProperty("weather_code").GetInt32();
            bool isDay = !current.TryGetProperty("is_day", out JsonElement day) || day.GetInt32() == 1;

            var (kind, description) = Describe(code, isDay);
            return new WeatherInfo(temperature, kind, description);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Weather update failed: {ex.Message}");
            return null;
        }
    }

    private async Task<(double, double)?> GeocodeAsync(string city)
    {
        string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&format=json&name=" + Uri.EscapeDataString(city);
        using JsonDocument doc = JsonDocument.Parse(await _http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("results", out JsonElement results) || results.GetArrayLength() == 0)
            return null;

        JsonElement first = results[0];
        return (first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
    }

    private static (double, double)? ParseCoordinates(string text)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)
            ? (lat, lon)
            : null;
    }

    /// <summary>WMO weather code → symbol + short text.</summary>
    private static (WeatherKind Kind, string Description) Describe(int code, bool isDay) => code switch
    {
        0 => (isDay ? WeatherKind.Sun : WeatherKind.Moon, "Clear"),
        1 => (isDay ? WeatherKind.Sun : WeatherKind.Moon, "Mostly clear"),
        2 => (isDay ? WeatherKind.CloudSun : WeatherKind.CloudMoon, "Partly cloudy"),
        3 => (WeatherKind.Cloud, "Cloudy"),
        45 or 48 => (WeatherKind.Fog, "Fog"),
        >= 51 and <= 57 => (WeatherKind.Drizzle, "Drizzle"),
        >= 61 and <= 67 => (WeatherKind.Rain, "Rain"),
        >= 71 and <= 77 => (WeatherKind.Snow, "Snow"),
        >= 80 and <= 82 => (WeatherKind.Rain, "Showers"),
        85 or 86 => (WeatherKind.Snow, "Snow showers"),
        >= 95 => (WeatherKind.Thunder, "Thunderstorm"),
        _ => (WeatherKind.Cloud, ""),
    };

    public void Dispose() => _http.Dispose();
}
