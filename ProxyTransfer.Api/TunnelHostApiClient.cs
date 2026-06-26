using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ProxyTransfer.Api;

internal sealed class TunnelHostApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public TunnelHostApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task<IReadOnlyList<TunnelHostInstanceResponse>> ListAsync(
        CancellationToken cancellationToken
    )
    {
        return SendAsync<IReadOnlyList<TunnelHostInstanceResponse>>(
            HttpMethod.Get,
            "/api/instances",
            null,
            cancellationToken
        );
    }

    public Task<TunnelHostInstanceResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        return SendOptionalAsync<TunnelHostInstanceResponse>(
            HttpMethod.Get,
            $"/api/instances/{id}",
            null,
            cancellationToken
        );
    }

    public Task<TunnelHostInstanceResponse> CreateDirectAsync(
        TunnelHostCreateDirectRequest request,
        CancellationToken cancellationToken
    )
    {
        return SendAsync<TunnelHostInstanceResponse>(
            HttpMethod.Post,
            "/api/direct",
            request,
            cancellationToken
        );
    }

    public Task<TunnelHostInstanceResponse> CreatePoolAsync(
        TunnelHostCreatePoolRequest request,
        CancellationToken cancellationToken
    )
    {
        return SendAsync<TunnelHostInstanceResponse>(
            HttpMethod.Post,
            "/api/pools",
            request,
            cancellationToken
        );
    }

    public Task<TunnelHostInstanceResponse?> StartAsync(
        Guid id,
        TunnelHostStartRequest? request,
        CancellationToken cancellationToken
    )
    {
        return SendOptionalAsync<TunnelHostInstanceResponse>(
            HttpMethod.Post,
            $"/api/instances/{id}/start",
            request,
            cancellationToken
        );
    }

    public Task<TunnelHostInstanceResponse?> StopAsync(Guid id, CancellationToken cancellationToken)
    {
        return SendOptionalAsync<TunnelHostInstanceResponse>(
            HttpMethod.Post,
            $"/api/instances/{id}/stop",
            null,
            cancellationToken
        );
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        return SendDeleteAsync($"/api/instances/{id}", cancellationToken);
    }

    public Task<TunnelHostPortRangeResponse> GetPortRangeAsync(CancellationToken cancellationToken)
    {
        return SendAsync<TunnelHostPortRangeResponse>(
            HttpMethod.Get,
            "/api/host/port-range",
            null,
            cancellationToken
        );
    }

    private async Task<bool> SendDeleteAsync(
        string relativeUrl,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, relativeUrl);
        using var response = await _httpClient
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowForFailureAsync(response, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string relativeUrl,
        object? payload,
        CancellationToken cancellationToken
    )
    {
        using var request = CreateRequest(method, relativeUrl, payload);
        using var response = await _httpClient
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowForFailureAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var result = await response
            .Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
        {
            throw new InvalidOperationException($"TunnelHost 返回了空响应: {relativeUrl}");
        }

        return result;
    }

    private async Task<TResponse?> SendOptionalAsync<TResponse>(
        HttpMethod method,
        string relativeUrl,
        object? payload,
        CancellationToken cancellationToken
    )
    {
        using var request = CreateRequest(method, relativeUrl, payload);
        using var response = await _httpClient
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowForFailureAsync(response, cancellationToken).ConfigureAwait(false);
        }

        return await response
            .Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string relativeUrl,
        object? payload
    )
    {
        var request = new HttpRequestMessage(method, relativeUrl);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: JsonOptions);
        }

        return request;
    }

    private static async Task ThrowForFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        var body = await response
            .Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var error = JsonSerializer.Deserialize<TunnelHostErrorResponse>(body, JsonOptions);
                if (!string.IsNullOrWhiteSpace(error?.Message))
                {
                    throw new InvalidOperationException(error.Message);
                }
            }
            catch (JsonException) { }
        }

        throw new HttpRequestException(
            $"TunnelHost 请求失败: {(int)response.StatusCode} {response.ReasonPhrase}"
        );
    }

    private sealed record TunnelHostErrorResponse(string? Message);
}
