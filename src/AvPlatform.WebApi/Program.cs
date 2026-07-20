using AvPlatform.WebApi.Channels;
using AvPlatform.WebApi.Middleware;
using AvPlatform.WebApi.Persistence;
using AvPlatform.WebApi.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// 日志先于其他服务配置，保证启动阶段的异常也能落盘。
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "AvPlatform.WebApi")
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .WriteTo.Console(new CompactJsonFormatter())
    .WriteTo.File(
        new CompactJsonFormatter(),
        "data/logs/system-.json",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true));

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(
        Path.Combine(builder.Environment.ContentRootPath, "data", "keys")))
    .SetApplicationName("AvPlatform");
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=data/avplatform.db";
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddHttpClient<YxfmApiClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AvPlatform/0.1 (+self-hosted)");
});
builder.Services.AddHttpClient<GdApiClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AvPlatform/0.1 (+self-hosted)");
});
builder.Services.AddSingleton<YueShuGeConfigProvider>();
builder.Services.AddHttpClient<YueShuGeBoxClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AvPlatform/0.1 (+self-hosted)");
});
builder.Services.AddHttpClient<YueShuGeHtmlClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.All,
    UseCookies = false
});
builder.Services.AddHttpClient<OneApiClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AvPlatform/0.1 (+self-hosted)");
});
builder.Services.AddHttpClient<TxVlogApiClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(25);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.All
});
builder.Services.AddHttpClient<MissAvChannelAdapter>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36");
    client.DefaultRequestHeaders.Accept.ParseAdd(
        "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.All,
    UseCookies = true
});
builder.Services.AddHttpClient<XvideosChannelAdapter>(client =>
{
    client.BaseAddress = new Uri("https://www.xvideos.com/");
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
});
builder.Services.AddHttpClient("channel-media", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.All
});
builder.Services.AddScoped<IChannelAdapter, YxfmChannelAdapter>();
builder.Services.AddScoped<IChannelAdapter, AiJavChannelAdapter>();
builder.Services.AddScoped<IChannelAdapter, OneChannelAdapter>();
builder.Services.AddScoped<IChannelAdapter, TxVlogChannelAdapter>();
builder.Services.AddScoped<IChannelAdapter, InsAvChannelAdapter>();
builder.Services.AddScoped<IChannelAdapter, SftvChannelAdapter>();
builder.Services.AddScoped<IChannelAdapter, RryyChannelAdapter>();
builder.Services.AddScoped<IChannelAdapter, HsckChannelAdapter>();
builder.Services.AddScoped<IChannelAdapter, Pron91ChannelAdapter>();
builder.Services.AddScoped<IChannelAdapter>(services => services.GetRequiredService<MissAvChannelAdapter>());
builder.Services.AddScoped<IChannelAdapter>(services => services.GetRequiredService<XvideosChannelAdapter>());
builder.Services.AddScoped<IChannelService, ChannelService>();
builder.Services.AddScoped<IChannelMediaProxy, ChannelMediaProxy>();

var app = builder.Build();

// SQLite 文件和日志目录由应用负责创建，容器中通过 /app/data 挂载持久卷。
Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "data"));
Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "data", "logs"));
Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "data", "keys"));
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.UseMiddleware<RequestLoggingMiddleware>();
app.UseExceptionHandler();

if (app.Configuration.GetValue("UseHttpsRedirection", false))
{
    app.UseHttpsRedirection();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "AvPlatform.WebApi",
    time = DateTimeOffset.UtcNow
})).WithTags("系统").WithSummary("检查 API 运行状态");

app.MapControllers();

try
{
    Log.Information("AvPlatform.WebApi 启动，环境：{Environment}", app.Environment.EnvironmentName);
    await app.RunAsync();
}
finally
{
    Log.Information("AvPlatform.WebApi 停止");
    await Log.CloseAndFlushAsync();
}
