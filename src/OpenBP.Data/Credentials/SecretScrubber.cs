using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Data.Credentials;

/// <summary>
/// Secret scrubbing (docs/architecture.md §8, правило 6 <c>CLAUDE.md</c>): значення credentials виконання,
/// що потрапили в items або повідомлення, замінюються на <see cref="Mask"/> перед записом кроку.
/// <list type="bullet">
/// <item>Секрети — усі рядкові й числові листки даних credential'ів, зареєстрованих через <see cref="Add"/>
/// (це робить <see cref="PostgresCredentialProvider"/>).</item>
/// <item>Рядки довжиною від <see cref="MinSubstringLength"/> замінюються і як підрядки; коротші — лише коли
/// значення збігається повністю (інакше секрет «a» зіпсував би кожне слово).</item>
/// <item>Обробляються значення в будь-якій вкладеності об'єктів і масивів; імена властивостей і <c>BinaryRef</c>
/// (метадані файлів) не змінюються.</item>
/// <item>Вхідні items не змінюються — повертаються очищені копії (ті самі items іще йдуть наступним вузлам).</item>
/// </list>
/// Один екземпляр — на одне виконання. Потокобезпечний.
/// </summary>
public sealed class SecretScrubber
{
    /// <summary>Чим замінюється секрет.</summary>
    public const string Mask = "***";

    /// <summary>Мінімальна довжина секрету, з якої він шукається як підрядок.</summary>
    public const int MinSubstringLength = 4;

    private readonly Lock _lock = new();

    // Від довшого до коротшого: секрет, що містить інший, замінюється першим.
    private volatile string[] _secrets = [];

    /// <summary>Чи зареєстровано хоч один секрет.</summary>
    public bool HasSecrets => _secrets.Length > 0;

    /// <summary>Реєструє як секрети всі рядкові й числові значення з <paramref name="data"/> (рекурсивно).</summary>
    /// <param name="data">Розшифровані дані credential'а.</param>
    public void Add(JsonNode? data)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        Collect(data, found);
        if (found.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            found.UnionWith(_secrets);
            _secrets = [.. found.OrderByDescending(s => s.Length).ThenBy(s => s, StringComparer.Ordinal)];
        }
    }

    /// <summary>Очищає рядок.</summary>
    /// <param name="value">Рядок (повідомлення, значення).</param>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(value))]
    public string? Scrub(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var secrets = _secrets;
        foreach (var secret in secrets)
        {
            if (value == secret)
            {
                return Mask;
            }
        }

        foreach (var secret in secrets)
        {
            if (secret.Length >= MinSubstringLength && value.Contains(secret, StringComparison.Ordinal))
            {
                value = value.Replace(secret, Mask, StringComparison.Ordinal);
            }
        }

        return value;
    }

    /// <summary>Повертає очищену копію JSON-вузла.</summary>
    /// <param name="node">Вузол; не змінюється.</param>
    public JsonNode? Scrub(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var copy = new JsonObject(obj.Options);
                foreach (var (name, value) in obj)
                {
                    copy[name] = Scrub(value);
                }

                return copy;
            case JsonArray array:
                var items = new JsonArray(array.Options);
                foreach (var value in array)
                {
                    items.Add(Scrub(value));
                }

                return items;
            default:
                return ScrubValue(node.AsValue());
        }
    }

    /// <summary>Повертає очищені копії items за портами.</summary>
    /// <param name="ports">Items на кожному порту; не змінюються.</param>
    public Item[][] Scrub(Item[][] ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        if (!HasSecrets)
        {
            return ports;
        }

        return [.. ports.Select(port => port.Select(ScrubItem).ToArray())];
    }

    /// <summary>Повертає крок з очищеними входами, виходами і повідомленням помилки.</summary>
    /// <param name="step">Крок; не змінюється.</param>
    public StepRecord Scrub(StepRecord step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (!HasSecrets)
        {
            return step;
        }

        return step with
        {
            Input = Scrub(step.Input),
            Output = Scrub(step.Output),
            Error = step.Error is null ? null : step.Error with { Message = Scrub(step.Error.Message), Type = Scrub(step.Error.Type) },
        };
    }

    private Item ScrubItem(Item item) => new()
    {
        Json = (JsonObject)Scrub(item.Json)!,
        Binary = item.Binary,
        ExtensionData = item.ExtensionData?.ToDictionary(
            e => e.Key,
            e => JsonSerializer.SerializeToElement(Scrub(JsonSerializer.SerializeToNode(e.Value)))),
    };

    private JsonNode ScrubValue(JsonValue value)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                var text = value.GetValue<string>();
                var scrubbed = Scrub(text);
                return ReferenceEquals(scrubbed, text) ? value.DeepClone() : JsonValue.Create(scrubbed);
            case JsonValueKind.Number:
                return Array.IndexOf(_secrets, value.ToJsonString()) >= 0 ? JsonValue.Create(Mask) : value.DeepClone();
            default:
                return value.DeepClone();
        }
    }

    private static void Collect(JsonNode? node, HashSet<string> found)
    {
        switch (node)
        {
            case null:
                return;
            case JsonObject obj:
                foreach (var (_, value) in obj)
                {
                    Collect(value, found);
                }

                return;
            case JsonArray array:
                foreach (var value in array)
                {
                    Collect(value, found);
                }

                return;
            default:
                var leaf = node.AsValue();
                switch (leaf.GetValueKind())
                {
                    case JsonValueKind.String:
                        var text = leaf.GetValue<string>();
                        if (text.Length > 0)
                        {
                            found.Add(text);
                        }

                        break;
                    case JsonValueKind.Number:
                        found.Add(leaf.ToJsonString());
                        break;
                }

                return;
        }
    }
}
