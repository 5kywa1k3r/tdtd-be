using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using System.Text;
using tdtd_be.Caching;
using tdtd_be.Data;
using tdtd_be.Data.Indexes;
using tdtd_be.Middleware;
using tdtd_be.Options;
using tdtd_be.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ===== Options + ValidateOnStart =====
builder.Services.AddOptions<MongoOptions>()
    .Bind(builder.Configuration.GetSection("Mongo"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString) && !string.IsNullOrWhiteSpace(o.Database),
        "Mongo config invalid")
    .ValidateOnStart();

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection("Jwt"))
    .Validate(o =>
        !string.IsNullOrWhiteSpace(o.Issuer) &&
        !string.IsNullOrWhiteSpace(o.Audience) &&
        !string.IsNullOrWhiteSpace(o.Key) &&
        o.Key.Length >= 32 &&
        o.AccessTokenMinutes > 0 &&
        o.RefreshTokenDays > 0,
        "Jwt config invalid")
    .ValidateOnStart();

builder.Services.AddOptions<CacheOptions>()
    .Bind(builder.Configuration.GetSection("Cache"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Provider), "Cache config invalid")
    .ValidateOnStart();

// ===== Mongo DI =====
builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var opt = sp.GetRequiredService<IOptions<MongoOptions>>().Value;
    return new MongoClient(opt.ConnectionString);
});
builder.Services.AddSingleton<MongoDbContext>();

// ===== Redis Cache DI =====
var cacheOpt = builder.Configuration.GetSection("Cache").Get<CacheOptions>()!;
builder.Services.AddStackExchangeRedisCache(o =>
{
    o.Configuration = cacheOpt.RedisConnectionString;
    o.InstanceName = cacheOpt.InstanceName;
});
builder.Services.AddSingleton<IAppCache, RedisAppCache>();

// ===== AuthN/AuthZ =====
var jwtOpt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.RequireHttpsMetadata = false; // dev
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOpt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOpt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOpt.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = System.Security.Claims.ClaimTypes.Role
        };
    });

builder.Services.AddAuthorization();

// ===== App Services =====
builder.Services.AddSingleton<IJwtService, JwtService>();
builder.Services.AddScoped<AuthService>();

// ===== Middleware + Context =====
builder.Services.AddScoped<UserContext>();
builder.Services.AddScoped<UserContextMiddleware>();
builder.Services.AddScoped<ExceptionHandlingMiddleware>();

var app = builder.Build();

// ===== Init Mongo Indexes (1 lần khi start) =====
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<MongoDbContext>();
    await MongoIndexInitializer.EnsureAsync(ctx.Db);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseMiddleware<UserContextMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.Run();
