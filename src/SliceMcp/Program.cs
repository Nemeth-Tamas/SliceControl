using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using System.Net;

const string bindAddress =
    "10.10.10.12";

const int port =
    8790;

var builder =
    WebApplication.CreateBuilder(
        args);

builder.WebHost.ConfigureKestrel(
    options =>
    {
        options.Listen(
            IPAddress.Parse(
                bindAddress),
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
    .WithTools<ShopAudioTools>();

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
