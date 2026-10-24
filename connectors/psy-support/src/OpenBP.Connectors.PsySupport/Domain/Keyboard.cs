using System.Text.Json.Nodes;

namespace OpenBP.Connectors.PsySupport.Domain;

/// <summary>Кнопка inline-клавіатури: <c>callback_data</c> (до 64 байт, §6.1) або посилання.</summary>
public sealed record Button(string Text, string? Data = null, string? Url = null);

/// <summary>Об'єкти <c>reply_markup</c> Bot API для Telegram-конектора (F3-27).</summary>
public static class Keyboard
{
    /// <summary>Inline-клавіатура з рядків кнопок; порожні рядки пропускаються.</summary>
    public static JsonObject Inline(IEnumerable<IEnumerable<Button>> rows)
    {
        var keyboard = new JsonArray();
        foreach (var row in rows)
        {
            var line = new JsonArray();
            foreach (var button in row)
            {
                var b = new JsonObject { ["text"] = button.Text };
                if (button.Url is not null)
                {
                    b["url"] = button.Url;
                }
                else
                {
                    if (button.Data is null || System.Text.Encoding.UTF8.GetByteCount(button.Data) > 64)
                    {
                        throw new ArgumentException($"callback_data має бути від 1 до 64 байт: '{button.Data}'.", nameof(rows));
                    }

                    b["callback_data"] = button.Data;
                }

                line.Add(b);
            }

            if (line.Count > 0)
            {
                keyboard.Add(line);
            }
        }

        return new JsonObject { ["inline_keyboard"] = keyboard };
    }

    /// <summary>Inline-клавіатура: кожна кнопка — окремий рядок.</summary>
    public static JsonObject Column(IEnumerable<Button> buttons) => Inline(buttons.Select(b => new[] { b }));

    /// <summary>Reply-клавіатура з однією кнопкою «Поділитися номером» (<c>request_contact</c>).</summary>
    public static JsonObject RequestContact(string text) => new()
    {
        ["keyboard"] = new JsonArray(new JsonArray(new JsonObject { ["text"] = text, ["request_contact"] = true })),
        ["resize_keyboard"] = true,
        ["one_time_keyboard"] = true,
    };

    /// <summary>Текст постійної reply-кнопки під полем вводу; бот розпізнає його як «головне меню».</summary>
    public const string HomeText = "🏠 Головне меню";

    /// <summary>
    /// Постійна reply-клавіатура «🏠 Головне меню»: замінює «Поділитися номером» після реєстрації і відкриває меню, навіть
    /// коли повідомлення з меню загубилось угорі чату (як у n8n-боті).
    /// </summary>
    public static JsonObject Home() => new()
    {
        ["keyboard"] = new JsonArray(new JsonArray(new JsonObject { ["text"] = HomeText })),
        ["resize_keyboard"] = true,
        ["is_persistent"] = true,
    };

    /// <summary>Розбиває кнопки на рядки по <paramref name="perRow"/>.</summary>
    public static IEnumerable<IEnumerable<Button>> Rows(IEnumerable<Button> buttons, int perRow) => buttons.Chunk(perRow);
}
