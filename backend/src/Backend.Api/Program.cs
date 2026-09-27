using Backend.Api.Data;
using Backend.Api.Endpoints;
using Backend.Api.Ingestion;
using Backend.Api.Messaging;
using Backend.Api.Tools;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Asama 2 Backend API",
        Version = "v1",
        Description = "Geo-enrichment, track-matching, reference-data upload ve Tool API. " +
                      "Detaylar icin backend/README.md.",
    });
    options.OperationFilter<YoloDetectionRequestExampleFilter>();
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=backend.db"));

builder.Services.AddScoped<ReferenceDataLoader>();
builder.Services.AddScoped<DetectionIngestionService>();
builder.Services.AddScoped<TrackContextService>();
builder.Services.AddScoped<ImageStorageService>();
builder.Services.AddScoped<ReferenceDataStorageService>();

builder.Services.AddHttpClient("AgentService", client =>
{
    var agentBaseUrl = builder.Configuration["AgentBaseUrl"] ?? "http://localhost:8001";
    client.BaseAddress = new Uri(agentBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(10); // /assess must respond immediately, not wait for the LLM
});
builder.Services.AddScoped<IAssessmentRequestClient, AssessmentRequestClient>();
builder.Services.AddScoped<AssessmentRequestPublisher>();
builder.Services.AddScoped<AssessmentIngestionService>();

builder.Services.AddHttpClient("DetectionService", client =>
{
    var detectionBaseUrl = builder.Configuration["DetectionServiceBaseUrl"] ?? "http://localhost:8002";
    client.BaseAddress = new Uri(detectionBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(60); // CPU YOLO inference can take a few seconds per image
});
builder.Services.AddScoped<IDetectionServiceClient, DetectionServiceClient>();

var app = builder.Build();

// Safety net for exceptions that occur before an endpoint's own handler runs (e.g. ASP.NET's
// own multipart/form-data binder rejecting a request with no body at all) - these never reach
// EndpointResultHelper, so without this they'd leak a raw stack trace instead of clean JSON.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    context.Response.ContentType = "application/json";
    context.Response.StatusCode = exception is InvalidOperationException or FormatException or ArgumentException
        ? StatusCodes.Status400BadRequest
        : StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new { error = exception?.Message ?? "Unexpected error." });
}));

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Asama 2 Backend API v1");
    options.RoutePrefix = "swagger"; // http://localhost:5299/swagger
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapIngestionEndpoints();
app.MapImageEndpoints();
app.MapToolEndpoints();
app.MapReferenceDataEndpoints();
app.MapAssessmentEndpoints();

app.Run();

public partial class Program; // exposed for WebApplicationFactory-based integration tests
