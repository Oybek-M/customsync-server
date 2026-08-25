using CustomSync.Api.Endpoints;
using CustomSync.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<SyncDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

var app = builder.Build();

app.MapHealthEndpoints();

app.Run();

// Integration testlar uchun (WebApplicationFactory<Program>).
public partial class Program { }
