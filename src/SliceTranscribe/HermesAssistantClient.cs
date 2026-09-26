using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SliceTranscribe;

internal sealed record HermesAssistantConfig(
    string BaseUrl,
    string ApiKey,
    string SessionKey,
    string? SessionId);

internal sealed class HermesAssistantClient :
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented =
                true
        };

    private readonly string _configPath;
    private readonly HttpClient _http =
        new()
        {
            Timeout =
                TimeSpan.FromSeconds(
                    120)
        };

    private readonly SemaphoreSlim _gate =
        new(
            1,
            1);

    private HermesAssistantConfig? _config;

    public HermesAssistantClient()
    {
        string directory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "SliceAppliance");

        Directory.CreateDirectory(
            directory);

        _configPath =
            Path.Combine(
                directory,
                "assistant.json");

        _config =
            LoadConfig();
    }

    public bool IsConfigured =>
        _config is not null &&
        !string.IsNullOrWhiteSpace(
            _config.BaseUrl) &&
        !string.IsNullOrWhiteSpace(
            _config.ApiKey);

    public string ConfigPath =>
        _configPath;

    public string SessionKey =>
        _config?.SessionKey
        ?? "echo-puck-main";

    public string? SessionId =>
        _config?.SessionId;

    public async Task<string> SendAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                $"Hermes assistant is not configured. Run Configure-SliceAssistant.ps1. Config path: {_configPath}");
        }

        await _gate.WaitAsync(
            cancellationToken);

        try
        {
            HermesAssistantConfig config =
                _config!;

            string sessionId =
                await EnsureSessionAsync(
                    config,
                    cancellationToken);

            config =
                _config!;

            Uri endpoint =
                BuildUri(
                    config.BaseUrl,
                    $"api/sessions/{Uri.EscapeDataString(sessionId)}/chat");

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    endpoint);

            ApplyHeaders(
                request,
                config);

            string json =
                JsonSerializer.Serialize(
                    new
                    {
                        message
                    });

            request.Content =
                new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json");

            using HttpResponseMessage response =
                await _http.SendAsync(
                    request,
                    cancellationToken);

            string body =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Hermes chat returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            string reply =
                ExtractAssistantText(
                    body);

            if (string.IsNullOrWhiteSpace(
                reply))
            {
                throw new InvalidOperationException(
                    $"Hermes returned no assistant text. Raw response: {body}");
            }

            return reply.Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> EnsureSessionAsync(
        HermesAssistantConfig config,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(
            config.SessionId))
        {
            return config.SessionId;
        }

        Uri endpoint =
            BuildUri(
                config.BaseUrl,
                "api/sessions");

        using var request =
            new HttpRequestMessage(
                HttpMethod.Post,
                endpoint);

        ApplyHeaders(
            request,
            config);

        request.Content =
            new StringContent(
                "{}",
                Encoding.UTF8,
                "application/json");

        using HttpResponseMessage response =
            await _http.SendAsync(
                request,
                cancellationToken);

        string body =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Hermes session creation returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        string? sessionId =
            ExtractSessionId(
                body);

        if (string.IsNullOrWhiteSpace(
            sessionId))
        {
            throw new InvalidOperationException(
                $"Hermes did not return a session id. Raw response: {body}");
        }

        _config =
            config with
            {
                SessionId =
                    sessionId
            };

        SaveConfig(
            _config);

        Console.WriteLine(
            $"ASSISTANT -> Hermes session created: {sessionId}");

        return sessionId;
    }

    private HermesAssistantConfig? LoadConfig()
    {
        if (!File.Exists(
            _configPath))
        {
            return null;
        }

        try
        {
            string json =
                File.ReadAllText(
                    _configPath);

            HermesAssistantConfig? config =
                JsonSerializer.Deserialize<
                    HermesAssistantConfig>(
                        json);

            if (config is null)
            {
                return null;
            }

            return config with
            {
                SessionKey =
                    string.IsNullOrWhiteSpace(
                        config.SessionKey)
                        ? "echo-puck-main"
                        : config.SessionKey
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"ASSISTANT -> could not read Hermes config: {ex.Message}");

            return null;
        }
    }

    private void SaveConfig(
        HermesAssistantConfig config)
    {
        string json =
            JsonSerializer.Serialize(
                config,
                JsonOptions);

        File.WriteAllText(
            _configPath,
            json,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false));
    }

    private static void ApplyHeaders(
        HttpRequestMessage request,
        HermesAssistantConfig config)
    {
        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                config.ApiKey);

        request.Headers.TryAddWithoutValidation(
            "X-Hermes-Session-Key",
            config.SessionKey);
    }

    private static Uri BuildUri(
        string baseUrl,
        string relative)
    {
        string normalized =
            baseUrl.TrimEnd(
                '/') +
            "/";

        return new Uri(
            new Uri(
                normalized),
            relative);
    }

    private static string? ExtractSessionId(
        string body)
    {
        try
        {
            using JsonDocument document =
                JsonDocument.Parse(
                    body);

            return FindStringByPriority(
                document.RootElement,
                new[]
                {
                    "session_id",
                    "sessionId",
                    "id"
                });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExtractAssistantText(
        string body)
    {
        if (string.IsNullOrWhiteSpace(
            body))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document =
                JsonDocument.Parse(
                    body);

            string? found =
                FindStringByPriority(
                    document.RootElement,
                    new[]
                    {
                        "response",
                        "reply",
                        "content",
                        "text",
                        "message"
                    });

            return found
                   ?? string.Empty;
        }
        catch (JsonException)
        {
            return body.Trim();
        }
    }

    private static string? FindStringByPriority(
        JsonElement element,
        IReadOnlyList<string> names)
    {
        if (element.ValueKind ==
            JsonValueKind.Object)
        {
            foreach (string name in names)
            {
                if (
                    element.TryGetProperty(
                        name,
                        out JsonElement child))
                {
                    string? value =
                        ExtractString(
                            child,
                            names);

                    if (!string.IsNullOrWhiteSpace(
                        value))
                    {
                        return value;
                    }
                }
            }

            foreach (
                JsonProperty property
                in element.EnumerateObject())
            {
                string? value =
                    FindStringByPriority(
                        property.Value,
                        names);

                if (!string.IsNullOrWhiteSpace(
                    value))
                {
                    return value;
                }
            }
        }
        else if (
            element.ValueKind ==
                JsonValueKind.Array)
        {
            foreach (
                JsonElement child
                in element.EnumerateArray())
            {
                string? value =
                    FindStringByPriority(
                        child,
                        names);

                if (!string.IsNullOrWhiteSpace(
                    value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? ExtractString(
        JsonElement element,
        IReadOnlyList<string> names)
    {
        if (element.ValueKind ==
            JsonValueKind.String)
        {
            return element.GetString();
        }

        return FindStringByPriority(
            element,
            names);
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
