using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Minio;
using Minio.DataModel.Args;
using StackExchange.Redis;
using System.Text;
using tdtd_be.Common.Cache;
using tdtd_be.Common.Middleware;
using tdtd_be.Data;
using tdtd_be.Data.Indexes;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models;
using tdtd_be.Options;
using tdtd_be.Services;
using tdtd_be.Uploads;
using tusdotnet.Interfaces;
using tusdotnet.Stores;
// + middleware/cache namespaces (tạo 2 file này)
// using tdtd_be.Common.Cache;
// using tdtd_be.Common.Middleware;

var builder = WebApplication.CreateBuilder(args);

// ================== config ==================
builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection("Mongo"));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

// ================== mongo ==================
builder.Services.AddSingleton<MongoDbContext>();

// ================== MinIO client + TUS ==================
builder.Services.AddScoped<UploadFinalizeService>();
builder.Services.AddHostedService<TusTempCleanupHostedService>();
builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection("Uploads"));
builder.Services.AddSingleton<UploadTokenService>();
builder.Services.AddSingleton<IMinioClient>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();

    var rawEndpoint = cfg.GetValue<string>("Minio:Endpoint") ?? "localhost:9000";
    var accessKey = cfg.GetValue<string>("Minio:AccessKey") ?? throw new Exception("Minio:AccessKey missing");
    var secretKey = cfg.GetValue<string>("Minio:SecretKey") ?? throw new Exception("Minio:SecretKey missing");
    var secure = cfg.GetValue<bool>("Minio:Secure");

    // normalize endpoint: bỏ scheme, bỏ path
    var ep = rawEndpoint.Trim()
        .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
        .Replace("https://", "", StringComparison.OrdinalIgnoreCase);
    var slash = ep.IndexOf('/');
    if (slash >= 0) ep = ep[..slash];

    var parts = ep.Split(':', 2);
    var host = parts[0];
    var port = (parts.Length == 2 && int.TryParse(parts[1], out var p)) ? p : (secure ? 443 : 80);

    // LOG để chắc chắn app đang dùng đúng config
    Console.WriteLine($"[MinIO] endpoint={host}:{port} secure={secure} accessKey={accessKey} secretLen={secretKey.Length}");

    // Bật trace để nhìn request thật sự (rất hữu ích)
    var traceWriter = TextWriter.Synchronized(Console.Out);

    return new MinioClient()
        .WithEndpoint(host, port)
        .WithCredentials(accessKey, secretKey)
        .WithSSL(secure)
        .WithRegion("us-east-1")
        .Build();
});

// ================== core services ==================
builder.Services.AddSingleton<PasswordHasher<AppUser>>();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<UserContext>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<tdtd_be.Common.Auth.MeAccessor>();
builder.Services.AddScoped<tdtd_be.Common.Auth.UnitTreeHelper>();
builder.Services.AddScoped<IUnitTypeAdminService, UnitTypeAdminService>();
builder.Services.AddScoped<IUnitService, UnitService>();
builder.Services.AddScoped<IUserAdminService, UserAdminService>();

// ================== redis (cache) ==================
// appsettings.json:
// "Redis": { "ConnectionString": "localhost:6379", "MeTtlMinutes": 720 }
var redisEnabled = builder.Configuration.GetValue<bool>("Redis:Enabled");

if (redisEnabled)
{
    var cs = builder.Configuration["Redis:ConnectionString"];
    if (string.IsNullOrWhiteSpace(cs))
        throw new Exception("Redis:ConnectionString is missing");

    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        ConnectionMultiplexer.Connect($"{cs},abortConnect=false")
    );

    // only register cache + middleware when redis is enabled
    builder.Services.AddSingleton<RedisUserCache>();
}
builder.Services.AddTransient<MeContextRedisMiddleware>();

// ================== ApiExceptionMiddleware ==================
builder.Services.AddScoped<ApiExceptionMiddleware>();

builder.Services.AddControllers();
// ✅ Tus temp path
var tusTempPath = builder.Configuration["Tus:TempPath"] ?? "App_Data/tus";
Directory.CreateDirectory(tusTempPath);

// ✅ One TusDiskStore instance
builder.Services.AddSingleton<TusDiskStore>(_ => new TusDiskStore(tusTempPath));

// ✅ Map store interfaces (đúng version)
builder.Services.AddSingleton<ITusStore>(sp => sp.GetRequiredService<TusDiskStore>());
builder.Services.AddSingleton<ITusTerminationStore>(sp => sp.GetRequiredService<TusDiskStore>());

// ================== CORS ==================
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("fe", p =>
    {
        p.WithOrigins("http://localhost:5173") // sửa đúng FE origin
         .AllowAnyHeader()
         .AllowAnyMethod()
         .AllowCredentials()
         .WithExposedHeaders(
            "Location",
            "Upload-Offset",
            "Tus-Resumable",
            "Upload-Length"
         ); ;
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
    using var scope = app.Services.CreateScope();
    var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var bucket = cfg.GetValue<string>("Minio:Bucket") ?? "tdtd-attachments";

    try
    {
        var ok = await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket));
        app.Logger.LogInformation("[MinIO] BucketExists({Bucket}) => {Ok}", bucket, ok);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "[MinIO] sanity failed");
    }
}

app.UseAuthentication();

// ✅ Middleware cache: set HttpContext.Items["me"] từ Redis/claims + check tokenVersion (tv)
// IMPORTANT: đặt SAU UseAuthentication và TRƯỚC UseAuthorization
app.UseMiddleware<MeContextRedisMiddleware>();
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapTusUploads();

// ================== bootstrap indexes ==================
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<MongoDbContext>();
    var mongoOpt = scope.ServiceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<MongoOptions>>().Value;
    await MongoIndexInitializer.EnsureAsync(ctx.Db, mongoOpt);
}

app.Run();
    