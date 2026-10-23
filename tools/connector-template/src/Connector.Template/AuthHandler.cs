using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;

namespace Connector.Template;

/// <summary>Додає до кожного запиту в REST API автентифікацію за <see cref="AuthOptions"/> і секретами середовища.</summary>
/// <param name="options">Налаштування.</param>
/// <param name="secrets">Секрети.</param>
internal sealed class AuthHandler(IOptions<ConnectorOptions> options, ConnectorSecrets secrets) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var auth = options.Value.Auth;
        switch (auth.Parse())
        {
            case AuthType.Basic:
                var pair = $"{secrets.Require(ConnectorSecrets.UsernameVariable)}:{secrets.Require(ConnectorSecrets.PasswordVariable)}";
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
                break;
            case AuthType.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.Require(ConnectorSecrets.TokenVariable));
                break;
            case AuthType.ApiKey:
                request.Headers.TryAddWithoutValidation(auth.HeaderName, secrets.Require(ConnectorSecrets.ApiKeyVariable));
                break;
        }

        return base.SendAsync(request, cancellationToken);
    }
}
