using System.Text.Json;

namespace SliceMcp;

public sealed class SliceApiClient
{
    private readonly HttpClient _http;

    public SliceApiClient(
        HttpClient http)
    {
        _http =
            http;
    }

    public Task<JsonElement> GetAsync(
        string relativeUrl)
    {
        return SendAsync(
            HttpMethod.Get,
            relativeUrl);
    }

    public Task<JsonElement> PostAsync(
        string relativeUrl)
    {
        return SendAsync(
            HttpMethod.Post,
            relativeUrl);
    }

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string relativeUrl)
    {
        using var request =
            new HttpRequestMessage(
                method,
                relativeUrl);

        using HttpResponseMessage response =
            await _http.SendAsync(
                request);

        string body =
            await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Slice control API returned {(int)response.StatusCode}: {body}");
        }

        if (string.IsNullOrWhiteSpace(
            body))
        {
            using JsonDocument empty =
                JsonDocument.Parse(
                    "{}");

            return empty.RootElement.Clone();
        }

        using JsonDocument document =
            JsonDocument.Parse(
                body);

        return document.RootElement.Clone();
    }
}
