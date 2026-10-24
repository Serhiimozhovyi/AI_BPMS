using System.Text;
using System.Text.Json;
using OpenBP.Application.Auth;
using OpenBP.Application.Auth.Oidc;

namespace OpenBP.Application.Tests;

/// <summary>F4-14: claims токенів Entra ID і Keycloak → користувач OpenBP, мапінг ролей, домени пошти.</summary>
public class OidcClaimsMapperTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static OidcProviderOptions Entra(bool trustEmail = false) => new()
    {
        Key = "entra",
        RoleClaim = "roles",
        TrustEmail = trustEmail,
        RoleMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["admin"] = ["OpenBP.Admin"],
            ["editor"] = ["OpenBP.Editor"],
        },
    };

    // id_token Entra ID v2: ролі застосунку в "roles", пошта часто лише в preferred_username/upn, email_verified немає.
    private const string EntraIdToken = """
        {
          "aud": "6b0f7a2e-0000-0000-0000-000000000000",
          "iss": "https://login.microsoftonline.com/0f1e2d3c-0000-0000-0000-000000000000/v2.0",
          "sub": "AAAAAAAAAAAAAAAAAAAAAIkzqFVrSaSaFHy782bbtaQ",
          "oid": "00000000-0000-0000-66f3-3332eca7ea81",
          "tid": "0f1e2d3c-0000-0000-0000-000000000000",
          "name": "Олена Петренко",
          "preferred_username": "Olena.Petrenko@Astarta.ua",
          "roles": ["OpenBP.Editor", "Other.App.Role"]
        }
        """;

    [Fact]
    public void Entra_roles_and_upn_email_are_mapped()
    {
        var identity = OidcClaimsMapper.Map(Entra(trustEmail: true), [Json(EntraIdToken)]);

        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAIkzqFVrSaSaFHy782bbtaQ", identity.Subject);
        Assert.Equal("olena.petrenko@astarta.ua", identity.Email);
        Assert.True(identity.EmailVerified);
        Assert.Equal(UserRole.Editor, Entra().MapRole(identity.Roles));
    }

    [Fact]
    public void Entra_email_is_unverified_without_trust_or_xms_edov()
    {
        Assert.False(OidcClaimsMapper.Map(Entra(), [Json(EntraIdToken)]).EmailVerified);

        var withEdov = OidcClaimsMapper.Map(Entra(), [Json("""{ "sub": "s", "email": "a@astarta.ua", "xms_edov": true }""")]);
        Assert.True(withEdov.EmailVerified);
        Assert.Equal("a@astarta.ua", withEdov.Email);
    }

    [Fact]
    public void Entra_email_claim_wins_over_upn_and_non_email_username_is_ignored()
    {
        var identity = OidcClaimsMapper.Map(Entra(), [Json("""{ "sub": "s", "email": "Real@Astarta.ua", "upn": "other@astarta.ua" }""")]);
        Assert.Equal("real@astarta.ua", identity.Email);

        var noEmail = OidcClaimsMapper.Map(Entra(), [Json("""{ "sub": "s", "preferred_username": "olena" }""")]);
        Assert.Null(noEmail.Email);
    }

    [Fact]
    public void Entra_highest_mapped_role_wins_and_no_match_is_viewer()
    {
        var provider = Entra();

        Assert.Equal(UserRole.Admin, provider.MapRole(["OpenBP.Editor", "openbp.admin"]));
        Assert.Equal(UserRole.Viewer, provider.MapRole(["Other.App.Role"]));
        Assert.Equal(UserRole.Viewer, provider.MapRole([]));
    }

    [Fact]
    public void Without_role_map_role_is_not_managed()
    {
        Assert.Null(new OidcProviderOptions().MapRole(["anything"]));
    }

    [Fact]
    public void Keycloak_realm_and_client_roles_are_read_from_nested_claims_of_several_tokens()
    {
        var provider = new OidcProviderOptions { RoleClaim = "realm_access.roles, resource_access.openbp.roles, missing.path" };
        var idToken = Json("""{ "sub": "kc-1", "email": "a@openbp.test", "email_verified": true }""");
        var accessToken = Json("""
            {
              "sub": "kc-1",
              "realm_access": { "roles": ["openbp-editor", "offline_access"] },
              "resource_access": { "openbp": { "roles": ["openbp-admin"] }, "account": { "roles": ["manage-account"] } }
            }
            """);

        var identity = OidcClaimsMapper.Map(provider, [idToken, accessToken]);

        Assert.True(identity.EmailVerified);
        Assert.Equal(["offline_access", "openbp-admin", "openbp-editor"], identity.Roles.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("a@astarta.ua", true)]
    [InlineData("A@ASTARTA.UA", true)]
    [InlineData("a@sub.astarta.ua", false)]
    [InlineData("a@evil.ua", false)]
    [InlineData(null, false)]
    public void Allowed_email_domains(string? email, bool allowed)
    {
        var provider = new OidcProviderOptions { AllowedEmailDomains = ["astarta.ua", "@agrichain.ua"] };

        Assert.Equal(allowed, provider.IsEmailAllowed(email));
        Assert.True(new OidcProviderOptions().IsEmailAllowed(null));
    }

    [Fact]
    public void Jwt_payload_is_read_and_garbage_is_ignored()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"iss":"https://idp/realms/a","sub":"x"}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal("x", OidcClaimsMapper.ReadPayload($"e30.{payload}.sig")!.Value.GetProperty("sub").GetString());
        Assert.Null(OidcClaimsMapper.ReadPayload("obp_notajwt"));
        Assert.Null(OidcClaimsMapper.ReadPayload("a.!!!.c"));
        Assert.Null(OidcClaimsMapper.ReadPayload(null));
    }
}
