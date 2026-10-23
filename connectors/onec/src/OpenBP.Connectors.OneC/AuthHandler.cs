using System.Net.Http.Headers;
using System.Text;

namespace OpenBP.Connectors.OneC;

/// <summary>Додає до кожного запиту в OData 1С <c>Authorization: Basic</c> з логіном і паролем із середовища.</summary>
/// <param name="secrets">Секрети.</param>
internal sealed class AuthHandler(ConnectorSecrets secrets) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var pair = $"{secrets.Username}:{secrets.Password}";
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
        return base.SendAsync(request, cancellationToken);
    }
}
