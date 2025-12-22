using CalderaReport.Clients;
using CalderaReport.Clients.Registries;
using CalderaReport.Domain.Configuration;
using CalderaReport.Domain.DestinyApi;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace CalderaReport.Tests.Clients;

public class BungieClientTests
{
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly RateLimiterRegistry _rateLimiter;
    private readonly HttpClient _httpClient;
    private readonly IOptions<Destiny2Options> _options;

    public BungieClientTests()
    {
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        _rateLimiter = new RateLimiterRegistry(20);
        _httpClient = new HttpClient(_httpMessageHandlerMock.Object);

        _options = Options.Create(new Destiny2Options
        {
            Token = "test-api-key"
        });
    }

    [Fact]
    public void Constructor_WithMissingApiKey_ThrowsInvalidOperationException()
    {
        var optionsWithoutKey = Options.Create(new Destiny2Options { Token = null });

        var act = () => new BungieClient(_httpClient, optionsWithoutKey, _rateLimiter);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Destiny2ApiToken configuration value is missing or empty");
    }

    [Fact]
    public void Constructor_WithValidApiKey_SetsBaseAddressAndHeaders()
    {
        var client = new BungieClient(_httpClient, _options, _rateLimiter);

        _httpClient.BaseAddress.Should().Be(new Uri("https://www.bungie.net/Platform/"));
        _httpClient.DefaultRequestHeaders.GetValues("X-API-Key").Should().Contain("test-api-key");
    }

    [Fact]
    public async Task GetCharactersForPlayer_WithValidRequest_ReturnsCharacters()
    {
        var membershipId = 123456L;
        var membershipType = 3;
        var responseData = new DestinyApiResponse<DestinyProfileResponse>
        {
            ErrorCode = 1,
            ErrorStatus = "Success",
            Message = "",
            MessageData = new Dictionary<string, string>(),
            Response = new DestinyProfileResponse
            {
                characters = new DictionaryComponentResponseOfint64AndDestinyCharacterComponent
                {
                    data = new Dictionary<string, DestinyCharacterComponent>()
                },
                profile = new DestinyProfile
                {
                    data = new ProfileData
                    {
                        userInfo = new UserInfoCard
                        {
                            bungieGlobalDisplayName = "TestPlayer",
                            bungieGlobalDisplayNameCode = 1234
                        }
                    }
                }
            }
        };

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(responseData))
            });

        var client = new BungieClient(_httpClient, _options, _rateLimiter);

        var result = await client.GetCharactersForPlayer(membershipId, membershipType);

        result.Should().NotBeNull();
        result.ErrorCode.Should().Be(1);
    }

    [Fact]
    public async Task PerformSearchByBungieName_WithValidRequest_ReturnsSearchResults()
    {
        var searchRequest = new ExactSearchRequest
        {
            displayName = "TestPlayer",
            displayNameCode = 1234
        };
        var responseData = new DestinyApiResponse<IEnumerable<UserInfoCard>>
        {
            ErrorCode = 1,
            ErrorStatus = "Success",
            Message = "",
            MessageData = new Dictionary<string, string>(),
            Response = new List<UserInfoCard>
            {
                new UserInfoCard
                {
                    membershipId = "123456",
                    membershipType = 3,
                    bungieGlobalDisplayName = "TestPlayer",
                    bungieGlobalDisplayNameCode = 1234,
                    applicableMembershipTypes = new List<int> { 3 }
                }
            }
        };

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(responseData))
            });

        var client = new BungieClient(_httpClient, _options, _rateLimiter);

        var result = await client.PerformSearchByBungieName(searchRequest, -1);

        result.Should().NotBeNull();
        result.Response.Should().NotBeEmpty();
        result.Response.First().bungieGlobalDisplayName.Should().Be("TestPlayer");
    }

    [Fact]
    public async Task SendRequest_WithNonRetryableError_ThrowsDestinyApiException()
    {
        var errorCode = (int)BungieErrorCodes.AccountNotFound;
        var responseData = new DestinyApiResponse<object>
        {
            ErrorCode = errorCode,
            ErrorStatus = "AccountNotFound",
            Message = "Account not found",
            MessageData = new Dictionary<string, string>(),
            Response = null!
        };

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.NotFound,
                Content = new StringContent(JsonSerializer.Serialize(responseData), System.Text.Encoding.UTF8, "application/json")
            });

        var client = new BungieClient(_httpClient, _options, _rateLimiter);

        var act = async () => await client.GetCharactersForPlayer(123456L, 3);

        await act.Should().ThrowAsync<DestinyApiException>();
    }

    [Fact]
    public async Task GetHistoricalStatsForCharacter_WithValidParameters_ReturnsActivityHistory()
    {
        var membershipId = 123456L;
        var membershipType = 3;
        var characterId = "2305843009261234567";
        var activityHash = 987L;
        var page = 0;

        var responseData = new DestinyApiResponse<DestinyActivityHistoryResults>
        {
            ErrorCode = 1,
            ErrorStatus = "Success",
            Message = "",
            MessageData = new Dictionary<string, string>(),
            Response = new DestinyActivityHistoryResults
            {
                activities = new List<DestinyHistoricalStatsPeriodGroup>()
            }
        };

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(responseData))
            });

        var client = new BungieClient(_httpClient, _options, _rateLimiter);

        var result = await client.GetHistoricalStatsForCharacter(membershipId, membershipType, characterId, (int)activityHash, page);

        result.Should().NotBeNull();
        result.ErrorCode.Should().Be(1);
    }
}
