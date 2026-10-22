using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OpenBP.Api.Auth;
using OpenBP.Api.Endpoints;
using OpenBP.Api.Errors;
using OpenBP.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OpenBpDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<OpenBpExceptionHandler>();
builder.Services.AddOpenBpAuthentication();

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGroup("/api/v1").MapAuthEndpoints();

app.Run();

/// <summary>Точка входу; <c>partial</c> — для <c>WebApplicationFactory&lt;Program&gt;</c> у тестах.</summary>
public partial class Program;
