namespace Storefront.Services;

public class OrdersClient
{
    private readonly HttpClient _http;

    public OrdersClient(HttpClient http)
    {
        _http = http;
        // Default HttpClient timeout is 100s; no retry, no circuit breaker.
    }

    public async Task<OrderResult?> PlaceOrderAsync(CartDto cart)
    {
        var response = await _http.PostAsJsonAsync("/orders", cart);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OrderResult>();
    }
}

public record CartDto(string CustomerId, List<CartLine> Lines);
public record CartLine(string Sku, int Quantity);
public record OrderResult(Guid OrderId, decimal Total);
