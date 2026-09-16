using Microsoft.EntityFrameworkCore;
using Orders.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OrdersDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("OrdersDb")));

builder.Services.AddHttpClient<InventoryClient>(c => c.BaseAddress = new Uri("http://inventory-api:8080"));
builder.Services.AddHttpClient<PaymentClient>(c =>
{
    c.BaseAddress = new Uri("https://api.payments.example.com");
    c.DefaultRequestHeaders.Add("X-Api-Key", builder.Configuration["Payments:ApiKey"]);
});
builder.Services.AddHttpClient<NotifyClient>(c => c.BaseAddress = new Uri("http://notify-worker:8080"));

var app = builder.Build();

app.MapPost("/orders", async (CartDto cart, OrdersDbContext db, InventoryClient inventory, PaymentClient payments, NotifyClient notify) =>
{
    await inventory.ReserveAsync(cart.Lines);
    var order = new Order { Id = Guid.NewGuid(), CustomerId = cart.CustomerId, Total = cart.Lines.Sum(l => l.Quantity * 9.99m) };
    db.Orders.Add(order);
    await db.SaveChangesAsync();
    await payments.ChargeAsync(order.Id, order.Total);
    await notify.SendConfirmationAsync(order.Id, cart.CustomerId);
    return Results.Ok(new { order.Id, order.Total });
});

app.Run();

public record CartDto(string CustomerId, List<CartLine> Lines);
public record CartLine(string Sku, int Quantity);

public class Order { public Guid Id { get; set; } public string CustomerId { get; set; } = ""; public decimal Total { get; set; } }

public class OrdersDbContext : DbContext
{
    public OrdersDbContext(DbContextOptions<OrdersDbContext> options) : base(options) { }
    public DbSet<Order> Orders => Set<Order>();
}
