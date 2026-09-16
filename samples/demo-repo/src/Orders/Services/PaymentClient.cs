namespace Orders.Services;

public class PaymentClient
{
    private readonly HttpClient _http;
    public PaymentClient(HttpClient http) => _http = http;

    public async Task ChargeAsync(Guid orderId, decimal amount)
    {
        // Third-party call: no timeout override, no Polly policy, no idempotency key.
        var body = JsonContent.Create(new { orderId, amount, currency = "USD" });
        var resp = await _http.PostAsync("/v1/charges", body);
        resp.EnsureSuccessStatusCode();
    }
}
