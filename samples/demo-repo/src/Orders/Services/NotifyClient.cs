namespace Orders.Services;

public class NotifyClient
{
    private readonly HttpClient _http;
    public NotifyClient(HttpClient http) => _http = http;

    public async Task SendConfirmationAsync(Guid orderId, string customerId)
    {
        // Synchronous call into the worker: the order request waits for the e-mail to be sent.
        var resp = await _http.PostAsJsonAsync("/send", new { orderId, customerId });
        resp.EnsureSuccessStatusCode();
    }
}
