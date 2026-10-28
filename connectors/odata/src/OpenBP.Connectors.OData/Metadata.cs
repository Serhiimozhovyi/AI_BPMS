using System.Xml.Linq;

namespace OpenBP.Connectors.OData;

/// <summary>Набір сутностей сервісу: назва, тип, ключі й властивості.</summary>
/// <param name="Name">Назва набору (шлях у запиті).</param>
/// <param name="EntityType">Тип сутності.</param>
/// <param name="Keys">Ключові властивості.</param>
/// <param name="Properties">Властивості з типами EDM.</param>
/// <param name="Navigation">Навігаційні властивості (для <c>$expand</c>).</param>
public sealed record EntitySetInfo(string Name, string EntityType, IReadOnlyList<string> Keys, IReadOnlyList<PropertyInfo> Properties, IReadOnlyList<string> Navigation);

/// <summary>Властивість сутності.</summary>
/// <param name="Name">Назва.</param>
/// <param name="Type">Тип EDM, напр. <c>Edm.String</c>.</param>
public sealed record PropertyInfo(string Name, string Type);

/// <summary>Розбір <c>$metadata</c> (CSDL/EDMX 4.0 і 1.0–3.0) у набори сутностей.</summary>
public static class Metadata
{
    /// <summary>Читає набори сутностей з XML <c>$metadata</c>.</summary>
    /// <param name="xml">Вміст <c>$metadata</c>.</param>
    /// <exception cref="ConnectorException">Це не CSDL.</exception>
    public static IReadOnlyList<EntitySetInfo> Parse(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            throw new ConnectorException("Сервіс не повернув $metadata у форматі CSDL (XML). Перевірте адресу сервісу OData — вона має закінчуватись на корінь сервісу, напр. https://host/odata/.");
        }

        var types = document.Descendants().Where(e => e.Name.LocalName is "EntityType").ToList();
        var sets = new List<EntitySetInfo>();
        foreach (var set in document.Descendants().Where(e => e.Name.LocalName == "EntitySet"))
        {
            var name = (string?)set.Attribute("Name");
            var typeName = (string?)set.Attribute("EntityType");
            if (name is null || typeName is null)
            {
                continue;
            }

            var shortName = typeName[(typeName.LastIndexOf('.') + 1)..];
            var type = types.FirstOrDefault(t => (string?)t.Attribute("Name") == shortName);
            var keys = type?.Elements().FirstOrDefault(e => e.Name.LocalName == "Key")?.Elements().Select(k => (string?)k.Attribute("Name")).OfType<string>().ToList() ?? [];
            var properties = type?.Elements().Where(e => e.Name.LocalName == "Property")
                .Select(p => new PropertyInfo((string?)p.Attribute("Name") ?? "", (string?)p.Attribute("Type") ?? ""))
                .Where(p => p.Name.Length > 0).ToList() ?? [];
            var navigation = type?.Elements().Where(e => e.Name.LocalName == "NavigationProperty").Select(n => (string?)n.Attribute("Name")).OfType<string>().ToList() ?? [];
            sets.Add(new EntitySetInfo(name, typeName, keys, properties, navigation));
        }

        if (sets.Count == 0)
        {
            throw new ConnectorException("У $metadata сервісу немає наборів сутностей (EntitySet).");
        }

        return sets;
    }
}

/// <summary>Кеш <c>$metadata</c> на процес (5 хвилин): набори сутностей для перевірки імен і списку вибору.</summary>
/// <param name="odata">Клієнт.</param>
/// <param name="time">Годинник.</param>
public sealed class MetadataCache(ODataClient odata, TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private (IReadOnlyList<EntitySetInfo> Sets, DateTimeOffset At)? _cached;

    /// <summary>Набори сутностей сервісу.</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<EntitySetInfo>> GetAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is { } cached && time.GetUtcNow() - cached.At < Lifetime)
            {
                return cached.Sets;
            }

            var sets = Metadata.Parse(await odata.GetTextAsync("$metadata", ct).ConfigureAwait(false));
            _cached = (sets, time.GetUtcNow());
            return sets;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Набір за назвою; невідомий — помилка зі списком наявних.</summary>
    /// <param name="name">Назва набору.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ConnectorException">Набору немає в <c>$metadata</c>.</exception>
    public async Task<EntitySetInfo> RequireAsync(string name, CancellationToken ct)
    {
        var sets = await GetAsync(ct).ConfigureAwait(false);
        if (sets.FirstOrDefault(s => string.Equals(s.Name, name?.Trim(), StringComparison.Ordinal)) is { } found)
        {
            return found;
        }

        var close = sets.Where(s => s.Name.Contains(name?.Trim() ?? "", StringComparison.OrdinalIgnoreCase)).Select(s => s.Name).Take(5).ToList();
        var hint = close.Count > 0 ? $" Схожі: {string.Join(", ", close)}." : $" Наявні: {string.Join(", ", sets.Select(s => s.Name).Take(15))}{(sets.Count > 15 ? "…" : "")}.";
        throw new ConnectorException($"Набору сутностей «{name}» немає в сервісі.{hint} Повний список — tool list_entity_sets.");
    }
}
