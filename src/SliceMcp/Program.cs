using SliceMcp;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using System.Net;

const string bindAddress =
    "10.10.10.12";

var builder =
    WebApplication.CreateBuilder(
        args);

// Overrides allow isolated loopback validation without disturbing the appliance.
int port = builder.Configuration.GetValue<int?>("Mcp:Port") ?? 8790;
var listenAddress = IPAddress.Parse(builder.Configuration["Mcp:BindAddress"] ?? "0.0.0.0");
builder.Services.AddSingleton<EagleEyeCamera>();

builder.WebHost.ConfigureKestrel(
    options =>
    {
        options.Listen(
            listenAddress,
            port);
    });

builder.Services.AddHttpClient<
    SliceApiClient>(
        client =>
        {
            client.BaseAddress =
                new Uri(
                    "http://127.0.0.1:8787/");

            client.Timeout =
                TimeSpan.FromSeconds(
                    10);
        });

builder.Services.AddMcpServer()
    .WithHttpTransport(
        options =>
        {
            options.SessionMode =
                HttpServerSessionMode.Stateless;
        })
    .WithTools<ShopAudioTools>()
    .WithTools<EagleEyeTools>()
    .WithTools<EagleEyeImageTools>();

var app =
    builder.Build();

app.MapGet(
    "/",
    () =>
        Results.Json(
            new
            {
                service =
                    "Slice MCP",

                endpoint =
                    $"http://{bindAddress}:{port}/mcp",

                transport =
                    "streamable-http",

                scope =
                    "WireGuard only"
            }));

app.MapMcp(
    "/mcp");

await app.RunAsync();
