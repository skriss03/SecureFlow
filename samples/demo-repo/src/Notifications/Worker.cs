using System.Collections.Concurrent;
using SendGrid;
using SendGrid.Helpers.Mail;

public class Worker : BackgroundService
{
    private readonly ConcurrentQueue<SendRequest> _queue = new(); // in-memory: lost on restart
    private readonly SendGridClient _sendGrid;

    public Worker(SendGridClient sendGrid) => _sendGrid = sendGrid;

    public void Enqueue(SendRequest req) => _queue.Enqueue(req);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_queue.TryDequeue(out var req))
            {
                var msg = MailHelper.CreateSingleEmail(
                    new EmailAddress("orders@shopfast.example"), new EmailAddress(req.CustomerId),
                    "Order confirmed", $"Order {req.OrderId} confirmed", null);
                await _sendGrid.SendEmailAsync(msg, stoppingToken);
            }
            else
            {
                await Task.Delay(500, stoppingToken);
            }
        }
    }
}
