using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using StackExchange.Redis;
using System.Text;
using tdtd_be.Common.Cache;
using tdtd_be.Common.Middleware;
using tdtd_be.Data;
using tdtd_be.Data.Indexes;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Options;
using tdtd_be.Services;

// + middleware/cache namespaces (bệ hạ tạo 2 file này)
// using tdtd_be.Common.Cache;
// using tdtd_be.Common.Middleware;

var builder = WebApplication.CreateBuilder(args);

// ================== config ==================
builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection("Mongo"));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

// ================== mongo ==================
builder.Services.AddSingleton<MongoDbContext>();

// ================== core services ==================
builder.Services.AddSingleton<JwtService>();
builder.Services.AddScoped<AuthService>();

// ================== redis (cache) ==================
// appsettings.json:
// "Redis": { "ConnectionString": "localhost:6379", "MeTtlMinutes": 720 }
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var cs = cfg["Redis:ConnectionString"] ?? "localhost:6379";
    return ConnectionMultiplexer.Connect(cs);
});

// bệ hạ tạo RedisUserCache + MeContextRedisMiddleware theo code mình đã gửi trước
builder.Services.AddSingleton<RedisUserCache>();
builder.Services.AddTransient<MeContextRedisMiddleware>();

builder.Services.AddHttpContextAccessor();

builder.Services.AddControllers();

// ================== CORS ==================
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("fe", p =>
    {
        p.WithOrigins("http://localhost:5173") // sửa đúng FE origin
         .AllowAnyHeader()
         .AllowAnyMethod()
         .AllowCredentials();
    });
});

// ================== auth ==================
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()!;
var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(10)
        };
    });

builder.Services.AddAuthorization();

// ================== swagger ==================
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "TDTD API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Description = "JWT Authorization header using the Bearer scheme.",
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

app.UseCors("fe");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();

// ✅ Middleware cache: set HttpContext.Items["me"] từ Redis/claims + check tokenVersion (tv)
// IMPORTANT: đặt SAU UseAuthentication và TRƯỚC UseAuthorization
app.UseMiddleware<MeContextRedisMiddleware>();

app.UseAuthorization();
app.MapControllers();

// ================== bootstrap indexes ==================
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<MongoDbContext>();
    await MongoIndexInitializer.EnsureAsync(ctx.Db);
}

app.Run();
