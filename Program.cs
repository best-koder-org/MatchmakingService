using DatingApp.Shared.Middleware;
using MatchmakingService.Data;
using MatchmakingService.Extensions;
using MatchmakingService.Services;
using MatchmakingService.Common;
using MatchmakingService.Filters;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.Grafana.Loki;
using System;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()


        .Enrich.WithCorrelationId()
        .Enrich.WithProperty("ServiceName", "MatchmakingService")
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
        .MinimumLevel.Override("System", LogEventLevel.Warning)
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{ServiceName}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            path: "logs/matchmaking-service-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ServiceName}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}"
        )
        .WriteTo.GrafanaLoki(context.Configuration["Serilog:LokiUrl"] ?? "http://loki:3100", labels: new[]
        {
            new LokiLabel { Key = "app", Value = "MatchmakingService" },
            new LokiLabel { Key = "environment", Value = context.HostingEnvironment.EnvironmentName }
        });
});

builder.Services.AddKeycloakAuthentication(builder.Configuration, options =>
{
    options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            var isWs = context.HttpContext.WebSockets.IsWebSocketRequest;
            Console.WriteLine($"[DEBUG-OMR] OnMessageReceived FIRED: path={path}, isWs={isWs}, hasToken={!string.IsNullOrEmpty(accessToken)}, queryKeys=[{string.Join(",", context.Request.Query.Keys)}]");
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/matchmaking"))
            {
                context.Token = accessToken;
                Console.WriteLine($"[DEBUG-OMR] Token SET for /hubs/matchmaking, tokenLen={accessToken.ToString().Length}");
            }
            return Task.CompletedTask;
        },
        OnAuthenticationFailed = context =>
        {
            Console.WriteLine($"[DEBUG-AUTH-FAIL] Auth FAILED: {context.Exception.GetType().Name}: {context.Exception.Message}");
            if (context.Exception.InnerException != null)
                Console.WriteLine($"[DEBUG-AUTH-FAIL] Inner: {context.Exception.InnerException.Message}");
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization();

builder.Services.AddSignalR();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Matchmaking Service API",
        Version = "v1",
        Description = "Candidate scoring, daily suggestions, like/pass processing, and match creation."
    });

    // JWT Bearer authentication
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter your token in the text input below.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });

    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (System.IO.File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath);
    }
});

builder.Services.AddMemoryCache();

builder.Services.AddScoped<MatchmakingService.Services.MatchmakingService>();
builder.Services.AddScoped<IAdvancedMatchingService, AdvancedMatchingService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IHealthMetricsService, HealthMetricsService>();
builder.Services.AddHttpClient();

// Register scoring configuration with hot-reload support
builder.Services.Configure<MatchmakingService.Models.ScoringConfiguration>(
    builder.Configuration.GetSection("Scoring"));


// Register candidate system configuration with hot-reload support
builder.Services.Configure<MatchmakingService.Models.CandidateOptions>(
    builder.Configuration.GetSection("CandidateOptions"));
// Register daily suggestion limits configuration
builder.Services.Configure<MatchmakingService.Models.DailySuggestionLimits>(
    builder.Configuration.GetSection("DailySuggestionLimits"));
builder.Services.AddSingleton<IDailySuggestionTracker, InMemoryDailySuggestionTracker>();

builder.Services.AddCorrelationIds();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("MatchmakingService requires a configured DefaultConnection connection string.");
}

builder.Services.AddDbContext<MatchmakingDbContext>(options =>
    options.UseMySql(
        connectionString,
        new MySqlServerVersion(new Version(8, 0, 30)),
        mySqlOptions => mySqlOptions.EnableRetryOnFailure()
    ));

// Internal API Key Authentication for service-to-service calls
builder.Services.AddScoped<InternalApiKeyAuthFilter>();
builder.Services.AddTransient<InternalApiKeyAuthHandler>();
// T168/T169: Candidate filter pipeline
builder.Services.AddScoped<ICandidateFilter, MatchmakingService.Filters.SelfExclusionFilter>();
builder.Services.AddScoped<ICandidateFilter, MatchmakingService.Filters.ExcludeBotFilter>();       // Order 5
builder.Services.AddScoped<ICandidateFilter, MatchmakingService.Filters.ActiveUserFilter>();
builder.Services.AddScoped<ICandidateFilter, MatchmakingService.Filters.GenderFilter>();
builder.Services.AddScoped<ICandidateFilter, MatchmakingService.Filters.AgeRangeFilter>();
builder.Services.AddScoped<ICandidateFilter, MatchmakingService.Filters.ExcludeSwipedFilter>();
builder.Services.AddScoped<ICandidateFilter, MatchmakingService.Filters.ExcludeBlockedFilter>();
builder.Services.AddScoped<ICandidateFilter, MatchmakingService.Filters.DistanceFilter>();
builder.Services.AddScoped<MatchmakingService.Filters.CandidateFilterPipeline>();

// Phase 14.4: Candidate strategies
builder.Services.AddScoped<MatchmakingService.Strategies.LiveScoringStrategy>();
builder.Services.AddScoped<MatchmakingService.Strategies.PreComputedStrategy>();
builder.Services.AddScoped<MatchmakingService.Strategies.StrategyResolver>();
builder.Services.AddScoped<MatchmakingService.Strategies.DailyPickStrategy>();

// Phase 14.5: Background scoring service
builder.Services.AddHostedService<MatchmakingService.Services.Background.ScoreRefreshBackgroundService>();
builder.Services.AddHostedService<MatchmakingService.Services.Background.DailyPickGenerationService>();
// T524 (spec 005): Pre-compute pairwise compatibility scores for users who have answered questions
builder.Services.Configure<MatchmakingService.Services.Background.CompatibilityPrecomputeOptions>(
    builder.Configuration.GetSection(MatchmakingService.Services.Background.CompatibilityPrecomputeOptions.SectionName));
builder.Services.AddHostedService<MatchmakingService.Services.Background.CompatibilityPrecomputeService>();
builder.Services.AddScoped<MatchmakingService.Services.DesirabilityCalculator>();
builder.Services.AddScoped<MatchmakingService.Services.ICompatibilityScorer, MatchmakingService.Services.CompatibilityScorer>();
builder.Services.AddScoped<MatchmakingService.Services.IMatchInsightService, MatchmakingService.Services.MatchInsightService>();
builder.Services.AddScoped<MatchmakingService.Services.IUserProfileSyncService, MatchmakingService.Services.UserProfileSyncService>();

builder.Services.AddHttpClient<IUserServiceClient, UserServiceClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Gateway:BaseUrl"] ?? "http://dejting-yarp:8080");
})
.AddHttpMessageHandler<InternalApiKeyAuthHandler>();

builder.Services.AddHttpClient<ISafetyServiceClient, SafetyServiceClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Gateway:BaseUrl"] ?? "http://dejting-yarp:8080");
})
.AddHttpMessageHandler<InternalApiKeyAuthHandler>();


builder.Services.AddHttpClient<ISwipeServiceClient, SwipeServiceClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Gateway:BaseUrl"] ?? "http://dejting-yarp:8080");
})
.AddHttpMessageHandler<InternalApiKeyAuthHandler>();
builder.Services.AddHealthChecks();

// Configure OpenTelemetry for metrics and distributed tracing
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(serviceName: "matchmaking-service",
                    serviceVersion: "1.0.0"))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddMeter("MatchmakingService")
        .AddPrometheusExporter())
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation(options =>
        {
            options.RecordException = true;
            options.Filter = (httpContext) =>
            {
                // Don't trace health checks and metrics endpoints
                var path = httpContext.Request.Path.ToString();
                return !path.Contains("/health") && !path.Contains("/metrics");
            };
        })
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation(options =>
        {
            options.SetDbStatementForText = true;
            options.EnrichWithIDbCommand = (activity, command) =>
            {
                activity.SetTag("db.query", command.CommandText);
            };
        }));

// Create custom meters for business metrics

// Register injectable business metrics
builder.Services.AddSingleton<MatchmakingService.Metrics.MatchmakingServiceMetrics>();

// CORS: config-driven origins — AllowAnyOrigin in dev, restricted in staging/production
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        if (allowedOrigins != null && allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
        else
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<MatchmakingDbContext>();
    if (dbContext.Database.IsRelational())
    {
        dbContext.Database.Migrate();
    }
    await MatchmakingService.Data.SeedData.CompatibilityQuestionSeed.SeedAsync(dbContext);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment()) { app.UseHttpsRedirection(); }

app.UseCorrelationIds();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

app.MapHub<MatchmakingService.Hubs.MatchmakingHub>("/hubs/matchmaking");

app.MapControllers();
app.MapHealthChecks("/health");

// Map Prometheus metrics endpoint
app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();
