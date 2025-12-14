using CalderaReport.Clients.Abstract;
using CalderaReport.Clients.Registries;
using CalderaReport.Domain.Configuration;
using CalderaReport.Domain.DestinyApi;
using CalderaReport.Domain.Manifest;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace CalderaReport.Clients;

public class BungieClient : IBungieClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private const int MaxRetryAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private readonly RateLimiterRegistry _rateLimiter;

    public BungieClient(HttpClient httpClient, IOptions<Destiny2Options> options, RateLimiterRegistry rateLimiter)
    {
        _httpClient = httpClient;
        _apiKey = options.Value.Token!;
        _rateLimiter = rateLimiter;

        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new InvalidOperationException("Destiny2ApiToken configuration value is missing or empty");
        }

        _httpClient.BaseAddress = new Uri("https://www.bungie.net/Platform/");
        _httpClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string endpoint, Func<Task<HttpResponseMessage>> sendAsync)
    {
        for (var attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            try
            {
                var endpointKey = GetEndpointKey(endpoint);
                RateLimitLease? lease = null;
                try
                {
                    lease = await _rateLimiter.AcquireAsync(endpointKey);
                    var response = await sendAsync();
                    if (response.IsSuccessStatusCode)
                    {
                        return response;
                    }

                    var exception = await CreateExceptionAsync(response);
                    if (exception is DestinyApiException destinyException &&
                        DestinyApiConstants.NonRetryableErrorCodes.Contains(destinyException.ErrorCode))
                    {
                        throw destinyException;
                    }

                    if (attempt == MaxRetryAttempts)
                    {
                        throw exception;
                    }
                }
                finally
                {
                    lease?.Dispose();
                }
            }
            catch (DestinyApiException ex) when (!DestinyApiConstants.NonRetryableErrorCodes.Contains(ex.ErrorCode) && attempt < MaxRetryAttempts)
            {
                // swallow to retry
            }
            catch (HttpRequestException) when (attempt < MaxRetryAttempts)
            {
                // swallow to retry
            }
            catch (TaskCanceledException) when (attempt < MaxRetryAttempts)
            {
                // swallow to retry
            }
            catch (Exception) when (attempt < MaxRetryAttempts)
            {
                // swallow to retry
            }

            if (attempt < MaxRetryAttempts)
            {
                await Task.Delay(RetryDelay);
            }
        }

        throw new InvalidOperationException("Unreachable code reached in SendWithRetryAsync.");
    }

    private static string GetEndpointKey(string path)
    {
        if (path.Contains("/Profile/", StringComparison.OrdinalIgnoreCase))
            return "characters";
        if (path.Contains("Stats/Activities", StringComparison.OrdinalIgnoreCase))
            return "activityReports";
        if (path.Contains("PostGameCarnageReport", StringComparison.OrdinalIgnoreCase))
            return "pgcr";
        return "default";
    }

    private static async Task<Exception> CreateExceptionAsync(HttpResponseMessage response)
    {
        var reason = response.ReasonPhrase;
        var statusCode = response.StatusCode;
        var content = await response.Content.ReadAsStringAsync();
        response.Dispose();

        var errorResponse = JsonSerializer.Deserialize<DestinyApiResponseError>(content);
        if (errorResponse != null)
        {
            return new DestinyApiException(errorResponse);
        }

        var fallbackMessage = reason ?? statusCode.ToString();
        var message = string.IsNullOrWhiteSpace(content)
            ? $"{(int)statusCode} {fallbackMessage}"
            : content;
        return new HttpRequestException($"Error fetching Data from the Bungie API: {message}");
    }

    public async Task<DestinyApiResponse<DestinyProfileResponse>> GetCharactersForPlayer(long membershipId, int membershipType)
    {
        var url = $"Destiny2/{membershipType}/Profile/{membershipId}?components=100,200"; //Profile and Characters components
        using var response = await SendWithRetryAsync("GetCharactersForPlayer", () => _httpClient.GetAsync(url));
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<DestinyApiResponse<DestinyProfileResponse>>(content)
            ?? throw new InvalidOperationException("Failed to deserialize response.");
    }

    public async Task<DestinyApiResponse<DestinyActivityHistoryResults>> GetHistoricalStatsForCharacter(long destinyMembershipId, int membershipType, string characterId, int page, int activityCount)
    {
        var url = $"Destiny2/{membershipType}/Account/{destinyMembershipId}/Character/{characterId}/Stats/Activities/?page={page}&mode=7&count={activityCount}";
        using var response = await SendWithRetryAsync("GetHistoricalStatsForCharacter", () => _httpClient.GetAsync(url));

        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<DestinyApiResponse<DestinyActivityHistoryResults>>(content)
            ?? throw new InvalidOperationException("Failed to deserialize response.");
    }

    public async Task<DestinyApiResponse<PostGameCarnageReportData>> GetPostGameCarnageReport(long activityReportId)
    {
        var url = $"https://stats.bungie.net/Platform/Destiny2/Stats/PostGameCarnageReport/{activityReportId}/";
        using var response = await SendWithRetryAsync("GetPostGameCarnageReport", () => _httpClient.GetAsync(url));

        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<DestinyApiResponse<PostGameCarnageReportData>>(content)
            ?? throw new InvalidOperationException("Failed to deserialize response.");
    }

    public async Task<DestinyApiResponse<UserSearchPrefixResponse>> PerformSearchByPrefix(UserSearchPrefixRequest name, int page)
    {
        var url = $"User/Search/GlobalName/{page}";
        using var response = await SendWithRetryAsync("PerformSearchByPrefix", () => _httpClient.PostAsync(url, JsonContent.Create(name)));

        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<DestinyApiResponse<UserSearchPrefixResponse>>(content)
            ?? throw new InvalidOperationException("Failed to deserialize response.");
    }

    public async Task<DestinyApiResponse<List<UserInfoCard>>> PerformSearchByBungieName(ExactSearchRequest player, int membershipTypeId)
    {
        var url = $"Destiny2/SearchDestinyPlayerByBungieName/{membershipTypeId}/";
        using var response = await SendWithRetryAsync("PerformSearchByBungieName", () => _httpClient.PostAsync(url, JsonContent.Create(player)));

        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<DestinyApiResponse<List<UserInfoCard>>>(content)
            ?? throw new InvalidOperationException("Failed to deserialize response.");
    }

    public async Task<DestinyApiResponse<Manifest>> GetManifest()
    {
        var url = "Destiny2/Manifest/";
        using var response = await SendWithRetryAsync("GetManifest", () => _httpClient.GetAsync(url));

        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<DestinyApiResponse<Manifest>>(content)
            ?? throw new InvalidOperationException("Failed to deserialize response.");
    }
    public async Task<Dictionary<string, DestinyActivityDefinition>> GetActivityDefinitions(string url)
    {
        var fullUrl = "https://www.bungie.net" + url;
        var response = await SendWithRetryAsync("GetActivityDefinitions", () => _httpClient.GetAsync(fullUrl));
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<Dictionary<string, DestinyActivityDefinition>>(content)
             ?? throw new InvalidOperationException("Failed to deserialize activity definitions.");
        }
        else
        {
            throw new HttpRequestException($"Error fetching activity definitions: {response.ReasonPhrase}");
        }
    }
}
