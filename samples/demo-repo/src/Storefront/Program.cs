using Storefront.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddHttpClient<OrdersClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Orders:BaseUrl"] ?? "http://orders-api:8080");
});
builder.Services.AddStackExchangeRedisCache(o =>
{
    o.Configuration = builder.Configuration["Redis:ConnectionString"] ?? "redis:6379";
});
builder.Services.AddSession();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseSession();
app.MapRazorPages();
app.MapHealthChecks("/healthz");
app.Run();
