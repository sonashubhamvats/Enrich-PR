using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Authentication;
using Microsoft.OpenApi.Models;
using PrPrioritizerService.Services;
using PrPrioritizerService.Workers;

var builder = WebApplication.CreateBuilder(args);

var credentialsJson = Environment.GetEnvironmentVariable("FIREBASE_CREDENTIALS_JSON");
if (!string.IsNullOrEmpty(credentialsJson) && FirebaseApp.DefaultInstance == null)
{
    FirebaseApp.Create(new AppOptions
    {
        Credential = GoogleCredential.FromJson(credentialsJson)
    });
}

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "PrPrioritizer API", Version = "v1" });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste your Firebase ID Token below (without the 'Bearer ' prefix)."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
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
            Array.Empty<string>()
        }
    });
});

builder.Services.AddAuthentication("Firebase")
    .AddScheme<AuthenticationSchemeOptions, FirebaseAuthenticationHandler>("Firebase", null);

builder.Services.AddAuthorization();

// Register stateful and background dependencies
builder.Services.AddSingleton<IJobStateRepository, FirebaseJobStateRepository>();
builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
builder.Services.AddSingleton<IBackgroundJevQueryTaskQueue, BackgroundJevQueryTaskQueue>();

// Register HttpClients & External services
builder.Services.AddHttpClient<IGitHubService, GitHubService>();
builder.Services.AddHttpClient<IJevAnalysisService, JevAnalysisService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});

// Register background processing workers
builder.Services.AddHostedService<PrPrioritizerBackgroundWorker>();
builder.Services.AddHostedService<JevBackgroundWorker>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins("http://127.0.0.1:5500", "http://localhost:8000") // Add your frontend URLs here
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();

app.UseCors("AllowFrontend");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();