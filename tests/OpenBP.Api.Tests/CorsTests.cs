using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace OpenBP.Api.Tests;

/// <summary>F4-13: CORS за <c>OPENBP_CORS_ORIGINS</c> для вбудованого редактора на сторінках інших origin-ів.</summary>
[Collection(ApiCollection.Name)]
public class CorsTests(ApiFactory api)
{
    private const string Kit = "https://kit.example.com";

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData(" https://a.example.com/ , ,https://B.example.com,https://a.example.com", new[] { "https://a.example.com", "https://B.example.com" })]
    public void ParseOrigins_TrimsAndDeduplicates(string? value, string[] expected)
    {
        Assert.Equal(expected, CorsSetup.ParseOrigins(value));
    }

    [Fact]
    public async Task Preflight_FromAllowedOrigin_AllowsAuthorizationAndCookies()
    {
        using var factory = api.WithWebHostBuilder(b => b.UseSetting(CorsSetup.OriginsKey, $"{Kit},https://other.example.com"));
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/workflows");
        request.Headers.Add("Origin", Kit);
        request.Headers.Add("Access-Control-Request-Method", "PUT");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Kit, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
        Assert.Contains("authorization", string.Join(',', response.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Request_FromOtherOrigin_GetsNoCorsHeaders()
    {
        using var factory = api.WithWebHostBuilder(b => b.UseSetting(CorsSetup.OriginsKey, Kit));
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", "https://evil.example.com");

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task WithoutSetting_NoCorsHeaders()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", Kit);

        var response = await api.CreateClient().SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
