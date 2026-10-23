using System.Xml;
using System.Xml.Linq;

namespace OpenBP.Connectors.OneC.OData;

/// <summary>Властивість типу CSDL.</summary>
/// <param name="Name">Назва.</param>
/// <param name="Type">Тип: <c>Edm.String</c>, <c>Namespace.Type</c>, <c>Collection(…)</c>.</param>
/// <param name="Nullable">Чи допускає <c>null</c>.</param>
/// <param name="MaxLength">Максимальна довжина рядка.</param>
public sealed record CsdlProperty(string Name, string Type, bool Nullable, int? MaxLength);

/// <summary>Навігаційна властивість.</summary>
/// <param name="Name">Назва.</param>
/// <param name="Type">Тип цілі (у v3 — з асоціації), може бути <c>null</c>, якщо його не вдалося визначити.</param>
public sealed record CsdlNavigation(string Name, string? Type);

/// <summary>Сутність або складний тип CSDL.</summary>
/// <param name="QualifiedName">Повна назва <c>Namespace.Name</c>.</param>
/// <param name="BaseType">Базовий тип або <c>null</c>.</param>
/// <param name="Keys">Ключові властивості (лише в цьому типі; успадковані — через <see cref="CsdlModel.KeysOf"/>).</param>
/// <param name="Properties">Властивості цього типу.</param>
/// <param name="Navigations">Навігаційні властивості цього типу.</param>
public sealed record CsdlType(
    string QualifiedName,
    string? BaseType,
    IReadOnlyList<string> Keys,
    IReadOnlyList<CsdlProperty> Properties,
    IReadOnlyList<CsdlNavigation> Navigations);

/// <summary>Entity set контейнера.</summary>
/// <param name="Name">Назва (сегмент URL).</param>
/// <param name="EntityType">Повна назва типу сутності.</param>
public sealed record CsdlEntitySet(string Name, string EntityType);

/// <summary>
/// Модель з OData <c>$metadata</c> (CSDL XML): типи, перелічення й entity sets. Розбирається за локальними назвами
/// елементів, тому читає і v4 (OASIS), і v3 (<c>DataServiceVersion</c> 3.0 — так публікує 1С); для v3 важлива лише форма
/// літералів ключів (<see cref="IsV4"/>).
/// </summary>
public sealed class CsdlModel
{
    private readonly Dictionary<string, CsdlType> _types;
    private readonly Dictionary<string, IReadOnlyList<string>> _enums;
    private readonly Dictionary<string, string> _aliases;

    private CsdlModel(
        bool isV4,
        Dictionary<string, CsdlType> types,
        Dictionary<string, IReadOnlyList<string>> enums,
        Dictionary<string, string> aliases,
        IReadOnlyList<CsdlEntitySet> entitySets)
    {
        IsV4 = isV4;
        _types = types;
        _enums = enums;
        _aliases = aliases;
        EntitySets = entitySets;
    }

    /// <summary>OData v4 (інакше — v3 з літералами <c>guid'…'</c>, <c>datetime'…'</c>).</summary>
    public bool IsV4 { get; }

    /// <summary>Entity sets у порядку документа.</summary>
    public IReadOnlyList<CsdlEntitySet> EntitySets { get; }

    /// <summary>Читає <c>$metadata</c>.</summary>
    /// <param name="location">URL або шлях до файлу (відносний — від теки застосунку).</param>
    /// <param name="baseUrl">Базова адреса сервісу: URL під нею читається з автентифікацією конектора.</param>
    /// <param name="rest">Клієнт сервісу (з автентифікацією).</param>
    /// <param name="http">Клієнт без автентифікації — для URL на іншому хості.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="InvalidOperationException">Документ не вдалося прочитати чи розібрати.</exception>
    public static async Task<CsdlModel> LoadAsync(string location, Uri? baseUrl, RestClient rest, HttpClient http, CancellationToken ct)
    {
        string xml;
        if (Uri.TryCreate(location, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                if (baseUrl is not null && url.AbsoluteUri.StartsWith(baseUrl.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    using var response = await rest.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
                    xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"OData {url}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
                    }

                    xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
            }
            catch (ConnectorException ex)
            {
                throw new InvalidOperationException($"OData $metadata: {ex.Message}", ex);
            }
        }
        else
        {
            var path = Path.GetFullPath(location, AppContext.BaseDirectory);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"OData: файл {path} не знайдено.");
            }

            xml = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }

        try
        {
            return Parse(XDocument.Parse(xml));
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException($"OData {location}: $metadata не розібрано — {ex.Message}", ex);
        }
    }

    /// <summary>Розбирає CSDL.</summary>
    /// <param name="document">Документ <c>edmx:Edmx</c>.</param>
    /// <exception cref="InvalidOperationException">Це не CSDL.</exception>
    public static CsdlModel Parse(XDocument document)
    {
        var root = document.Root;
        if (root?.Name.LocalName != "Edmx")
        {
            throw new InvalidOperationException("OData: $metadata має починатися з edmx:Edmx.");
        }

        var isV4 = root.Attribute("Version")?.Value.StartsWith('4') == true;
        var types = new Dictionary<string, CsdlType>(StringComparer.Ordinal);
        var enums = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var associations = new Dictionary<string, Dictionary<string, (string Type, string Multiplicity)>>(StringComparer.Ordinal);
        var sets = new List<CsdlEntitySet>();
        var navigationsV3 = new List<(string Owner, string Name, string Relationship, string ToRole)>();

        foreach (var schema in root.Descendants().Where(e => e.Name.LocalName == "Schema"))
        {
            var ns = schema.Attribute("Namespace")?.Value ?? "";
            if (schema.Attribute("Alias")?.Value is { Length: > 0 } alias)
            {
                aliases[alias] = ns;
            }

            foreach (var element in schema.Elements())
            {
                var name = element.Attribute("Name")?.Value;
                if (name is null)
                {
                    continue;
                }

                var qualified = $"{ns}.{name}";
                switch (element.Name.LocalName)
                {
                    case "EntityType" or "ComplexType":
                        var keys = element.Elements().Where(e => e.Name.LocalName == "Key")
                            .SelectMany(k => k.Elements().Where(e => e.Name.LocalName == "PropertyRef"))
                            .Select(r => r.Attribute("Name")?.Value).OfType<string>().ToList();
                        var properties = element.Elements().Where(e => e.Name.LocalName == "Property")
                            .Select(p => new CsdlProperty(
                                p.Attribute("Name")?.Value ?? "",
                                p.Attribute("Type")?.Value ?? "Edm.String",
                                p.Attribute("Nullable")?.Value != "false",
                                int.TryParse(p.Attribute("MaxLength")?.Value, out var max) ? max : null))
                            .Where(p => p.Name.Length > 0)
                            .ToList();
                        var navigations = new List<CsdlNavigation>();
                        foreach (var nav in element.Elements().Where(e => e.Name.LocalName == "NavigationProperty"))
                        {
                            var navName = nav.Attribute("Name")?.Value;
                            if (navName is null)
                            {
                                continue;
                            }

                            if (nav.Attribute("Type")?.Value is { } navType)
                            {
                                navigations.Add(new CsdlNavigation(navName, navType));
                            }
                            else if (nav.Attribute("Relationship")?.Value is { } relationship && nav.Attribute("ToRole")?.Value is { } toRole)
                            {
                                navigationsV3.Add((qualified, navName, relationship, toRole));
                            }
                        }

                        types[qualified] = new CsdlType(qualified, element.Attribute("BaseType")?.Value, keys, properties, navigations);
                        break;
                    case "EnumType":
                        enums[qualified] = element.Elements().Where(e => e.Name.LocalName == "Member")
                            .Select(m => m.Attribute("Name")?.Value).OfType<string>().ToList();
                        break;
                    case "Association":
                        associations[qualified] = element.Elements().Where(e => e.Name.LocalName == "End")
                            .Where(e => e.Attribute("Role") is not null)
                            .ToDictionary(
                                e => e.Attribute("Role")!.Value,
                                e => (e.Attribute("Type")?.Value ?? "", e.Attribute("Multiplicity")?.Value ?? "1"),
                                StringComparer.Ordinal);
                        break;
                    case "EntityContainer":
                        sets.AddRange(element.Elements().Where(e => e.Name.LocalName == "EntitySet")
                            .Select(e => (Name: e.Attribute("Name")?.Value, Type: e.Attribute("EntityType")?.Value))
                            .Where(e => e.Name is not null && e.Type is not null)
                            .Select(e => new CsdlEntitySet(e.Name!, e.Type!)));
                        break;
                }
            }
        }

        var model = new CsdlModel(isV4, types, enums, aliases, sets);

        // v3: тип навігації — з кінця асоціації (Multiplicity * — колекція).
        foreach (var (owner, name, relationship, toRole) in navigationsV3)
        {
            string? type = null;
            if (associations.TryGetValue(model.Qualify(relationship), out var ends) && ends.TryGetValue(toRole, out var end))
            {
                type = end.Multiplicity == "*" ? $"Collection({end.Type})" : end.Type;
            }

            var t = types[owner];
            types[owner] = t with { Navigations = [.. t.Navigations, new CsdlNavigation(name, type)] };
        }

        return model;
    }

    /// <summary>Тип за назвою (повною чи через alias) або <c>null</c>.</summary>
    /// <param name="name">Назва.</param>
    public CsdlType? FindType(string name) => _types.GetValueOrDefault(Qualify(name));

    /// <summary>Члени перелічення або <c>null</c>, якщо це не перелічення.</summary>
    /// <param name="name">Назва.</param>
    public IReadOnlyList<string>? FindEnum(string name) => _enums.GetValueOrDefault(Qualify(name));

    /// <summary>Властивості типу разом з успадкованими (спершу базові).</summary>
    /// <param name="type">Тип.</param>
    public IReadOnlyList<CsdlProperty> PropertiesOf(CsdlType type) => Chain(type).SelectMany(t => t.Properties).ToList();

    /// <summary>Навігаційні властивості типу разом з успадкованими.</summary>
    /// <param name="type">Тип.</param>
    public IReadOnlyList<CsdlNavigation> NavigationsOf(CsdlType type) => Chain(type).SelectMany(t => t.Navigations).ToList();

    /// <summary>Ключові властивості (з найближчого типу ланцюжка, де є <c>Key</c>).</summary>
    /// <param name="type">Тип сутності.</param>
    public IReadOnlyList<CsdlProperty> KeysOf(CsdlType type)
    {
        var keys = Chain(type).LastOrDefault(t => t.Keys.Count > 0)?.Keys ?? [];
        var properties = PropertiesOf(type);
        return keys.Select(k => properties.FirstOrDefault(p => p.Name == k) ?? new CsdlProperty(k, "Edm.String", false, null)).ToList();
    }

    /// <summary>Замінює alias простору імен на повну назву.</summary>
    private string Qualify(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && _aliases.TryGetValue(name[..dot], out var ns) ? $"{ns}.{name[(dot + 1)..]}" : name;
    }

    /// <summary>Ланцюжок успадкування від найбазовішого типу до <paramref name="type"/>.</summary>
    private List<CsdlType> Chain(CsdlType type)
    {
        var chain = new List<CsdlType>();
        for (CsdlType? t = type; t is not null && chain.Count < 16 && !chain.Contains(t); t = t.BaseType is { } b ? FindType(b) : null)
        {
            chain.Insert(0, t);
        }

        return chain;
    }
}
