using bsckend.Repository;
using bsckend.Models.User;
using bsckend.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Minio;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite("Data Source=users.db"));

builder.Services
    .AddIdentity<UserModel, IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrEmpty(jwtKey))
{
    throw new InvalidOperationException("JWT Key is not configured in appsettings.json");
}

var keyBytes = Encoding.UTF8.GetBytes(jwtKey);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidateAudience = true,
        ValidAudience = builder.Configuration["Jwt:Audience"],
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(2)
    };
});

builder.Services.AddScoped<JwtService>();
builder.Services.AddSingleton<IWopiLockService, WopiLockService>();
builder.Services.AddScoped<IWopiTokenService, WopiTokenService>();

builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var minioEndpointValue = builder.Configuration["MinIO:Endpoint"]
                         ?? throw new InvalidOperationException("MinIO:Endpoint is not configured");
var minioAccessKey = builder.Configuration["MinIO:AccessKey"]
                     ?? throw new InvalidOperationException("MinIO:AccessKey is not configured");
var minioSecretKey = builder.Configuration["MinIO:SecretKey"]
                     ?? throw new InvalidOperationException("MinIO:SecretKey is not configured");
var minioUri = Uri.TryCreate(minioEndpointValue, UriKind.Absolute, out var parsedMinioUri)
    ? parsedMinioUri
    : null;
var minioEndpoint = minioUri is not null
    ? minioUri.Authority
    : minioEndpointValue;
var useMinioSsl = minioUri?.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) == true;

builder.Services.AddMinio(configureSource => configureSource
    .WithEndpoint(minioEndpoint)
    .WithCredentials(minioAccessKey, minioSecretKey)
    .WithSSL(useMinioSsl));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var db = services.GetRequiredService<ApplicationDbContext>();
    var minioClient = services.GetRequiredService<IMinioClient>();
    var logger = services.GetRequiredService<ILogger<Program>>();

    await DatabaseInitializer.InitializeAsync(db);
    await StorageInitializer.InitializeAsync(minioClient, builder.Configuration, logger);
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseRouting();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
