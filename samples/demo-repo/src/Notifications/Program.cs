using SendGrid;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());
builder.Services.AddSingleton(_ => new SendGridClient(builder.Configuration["SendGrid:ApiKey"]));

var app = builder.Build();
app.MapPost("/send", (SendRequest req, Worker worker) => { worker.Enqueue(req); return Results.Accepted(); });
app.Run();

public record SendRequest(Guid OrderId, string CustomerId);
