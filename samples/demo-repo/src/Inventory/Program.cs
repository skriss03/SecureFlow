using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<InventoryDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("InventoryDb")));
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/healthz");
app.MapPost("/reserve", async (List<CartLine> lines, InventoryDbContext db) =>
{
    foreach (var line in lines)
    {
        var stock = await db.Stock.FindAsync(line.Sku);
        if (stock is null || stock.Quantity < line.Quantity) return Results.Conflict();
        stock.Quantity -= line.Quantity;
    }
    await db.SaveChangesAsync();
    return Results.Ok();
});
app.Run();

public record CartLine(string Sku, int Quantity);
public class Stock { public string Sku { get; set; } = ""; public int Quantity { get; set; } }
public class InventoryDbContext : DbContext
{
    public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }
    public DbSet<Stock> Stock => Set<Stock>();
}
