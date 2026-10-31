using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenBP.Api.Tests;

/// <summary>F10-01: Swagger UI на <c>/swagger</c> відкритий без входу і показує <c>/openapi/v1.json</c>.</summary>
[Collection(ApiCollection.Name)]
public class SwaggerUiTests(ApiFactory api)
{
    [Fact]
    public async Task Index_IsAnonymousHtmlWithProductTitle()
    {
        var response = await api.CreateClient().GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        // Swagger UI кодує заголовок як HTML: «·» → &#183;.
        Assert.Contains("<title>AI_BPMS &#183; REST API</title>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_RedirectsToIndex()
    {
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/swagger");

        Assert.True(response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect, $"Очікували переадресацію, отримали {response.StatusCode}.");
        Assert.EndsWith("swagger/index.html", response.Headers.Location?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_PointsToOpenApiDocument_WithoutPersistedAuthorization()
    {
        var config = await api.CreateClient().GetStringAsync("/swagger/index.js");

        Assert.Contains("/openapi/v1.json", config, StringComparison.Ordinal);
        Assert.DoesNotContain("\"persistAuthorization\":true", config, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Document_StillOpenForSwaggerUi()
    {
        var response = await api.CreateClient().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
