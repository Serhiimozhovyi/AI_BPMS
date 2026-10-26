using System.Net;
using System.Net.Sockets;

namespace OpenBP.Connectors.Http;

/// <summary>
/// Які адреси можна відкривати: лише <c>http</c>/<c>https</c>; хост з <see cref="ConnectorSettings.AllowedHosts"/> (якщо список
/// заданий); приватні, loopback, link-local адреси й адреси хмарних метаданих — лише для хостів з
/// <see cref="ConnectorSettings.AllowPrivate"/>. Перевірка адреси — на самому підключенні (<see cref="IsAddressAllowed"/>),
/// тож перепризначення DNS між перевіркою й запитом нічого не дає.
/// </summary>
/// <param name="settings">Налаштування хостів.</param>
public sealed class HostPolicy(ConnectorSettings settings)
{
    /// <summary>Перевіряє адресу запиту до з'єднання.</summary>
    /// <param name="uri">Адреса.</param>
    /// <exception cref="ConnectorException">Схема чи хост не дозволені.</exception>
    public void Check(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            throw new ConnectorException($"Дозволені лише адреси http і https, а не «{uri.Scheme}».");
        }

        var allowed = settings.AllowedHosts;
        if (allowed.Count > 0 && !allowed.Any(pattern => Matches(pattern, uri.Host)))
        {
            throw new ConnectorException($"Хост «{uri.Host}» не входить до дозволених ({ConnectorSettings.AllowedHostsVariable}).");
        }
    }

    /// <summary>Чи можна підключатися до <paramref name="address"/> за іменем <paramref name="host"/>.</summary>
    /// <param name="host">Хост з адреси запиту.</param>
    /// <param name="address">IP, у який він перетворився.</param>
    public bool IsAddressAllowed(string host, IPAddress address) =>
        !IsPrivate(address) || settings.AllowPrivate.Any(p => p == "*" || p.Equals(host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Чи приватна адреса: loopback, RFC 1918, link-local (метадані хмари), CGNAT, ULA, multicast, нульова.</summary>
    /// <param name="address">IP.</param>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 or 10 or >= 224 => true,
                100 => bytes[1] is >= 64 and <= 127,
                169 => bytes[1] == 254,
                172 => bytes[1] is >= 16 and <= 31,
                192 => bytes[1] == 168 || (bytes[1] == 0 && bytes[2] == 0),
                _ => false,
            };
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (bytes[0] & 0xFE) == 0xFC;
    }

    private static bool Matches(string pattern, string host) =>
        pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : pattern.Equals(host, StringComparison.OrdinalIgnoreCase);
}
