using Ets2RoutePlanner.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var dbPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dbPath);
var conn = $"Data Source={Path.Combine(dbPath, "ets2routeplanner.db")}";

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddRoutePlannerData(conn);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DatabaseSchemaBootstrapper.EnsureSchemaAsync(db);
}

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
app.UseStaticFiles();
app.UseRouting();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.Run();
