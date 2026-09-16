namespace Orders.Services;

public class InventoryClient
{
    private readonly HttpClient _http;
    public InventoryClient(HttpClient http) => _http = http;

    public async Task ReserveAsync(IEnumerable<CartLine> lines)
    {
        var resp = await _http.PostAsJsonAsync("/reserve", lines);
        resp.EnsureSuccessStatusCode();
    }
}
