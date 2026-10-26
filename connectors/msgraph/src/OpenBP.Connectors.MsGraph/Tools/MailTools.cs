using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OpenBP.Connectors.MsGraph.Tools;

/// <summary>
/// Tools пошти Microsoft Graph (Outlook / Exchange Online), описані вручну; описи — англійською (їх читають ШІ-агенти).
/// Колекції — <c>structuredContent</c> <c>{ "result": [...] }</c> (у <c>mcp.tool</c> — <c>itemsPath = result</c>, у
/// <c>mcp.poll</c> — <c>keyField = id</c>); вміст вкладення — <c>resource</c>-блок (у OpenBP — binary item).
/// <see cref="ConnectorException"/> → результат з <c>isError</c>.
/// </summary>
/// <param name="graph">Клієнт Graph.</param>
[McpServerToolType]
public sealed class MailTools(GraphClient graph)
{
    private const string MailboxDescription = "Mailbox: user principal name or email address, e.g. orders@contoso.com.";
    internal const string MessageFields = "id,subject,from,toRecipients,ccRecipients,receivedDateTime,hasAttachments,isRead,bodyPreview,internetMessageId,webLink";

    /// <summary><c>list_messages</c>.</summary>
    /// <param name="mailbox">Скринька.</param>
    /// <param name="folder">Тека.</param>
    /// <param name="filter">OData $filter.</param>
    /// <param name="since">Лише отримані від цього часу.</param>
    /// <param name="top">Скільки листів.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "list_messages", Title = "List messages", ReadOnly = true, OpenWorld = true)]
    [Description("Lists messages in a mailbox (newest first unless filter is set). Returns { result: Message[] } with id, subject, from, receivedDateTime, hasAttachments, bodyPreview.")]
    public Task<CallToolResult> ListMessages(
        [Description(MailboxDescription)] string mailbox,
        [Description("Folder id or well-known name: inbox, sentitems, drafts, archive. Omit for all folders.")] string? folder = null,
        [Description("OData $filter, e.g. from/emailAddress/address eq 'a@b.com' or isRead eq false.")] string? filter = null,
        [Description("Only messages received at or after this ISO 8601 date-time, e.g. 2026-10-23T00:00:00Z.")] string? since = null,
        [Description("Max number of messages, 1-1000 (default 25).")] int? top = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            if (top is < 1 or > 1000)
            {
                throw new ConnectorException("top — від 1 до 1000.");
            }

            var filters = new List<string>();
            if (!string.IsNullOrWhiteSpace(filter))
            {
                filters.Add($"({filter})");
            }

            if (!string.IsNullOrWhiteSpace(since))
            {
                if (!DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var from))
                {
                    throw new ConnectorException("since — дата й час ISO 8601, напр. 2026-10-23T00:00:00Z.");
                }

                filters.Add($"receivedDateTime ge {from.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}");
            }

            var query = new List<string> { $"$select={MessageFields}", $"$top={top ?? 25}" };
            if (filters.Count > 0)
            {
                query.Add($"$filter={Uri.EscapeDataString(string.Join(" and ", filters))}");
            }

            // Graph відхиляє $orderby за полем, якого немає у $filter, тож власний фільтр — без сортування.
            if (string.IsNullOrWhiteSpace(filter))
            {
                query.Add($"$orderby={Uri.EscapeDataString("receivedDateTime desc")}");
            }

            var folderPath = string.IsNullOrWhiteSpace(folder) ? "" : $"mailFolders/{Uri.EscapeDataString(folder)}/";
            var page = await graph.SendAsync(HttpMethod.Get, $"{Mailbox(mailbox)}/{folderPath}messages?{string.Join('&', query)}", null, ct).ConfigureAwait(false);
            return List(page);
        });

    /// <summary><c>get_message</c>.</summary>
    /// <param name="mailbox">Скринька.</param>
    /// <param name="id">Id листа.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "get_message", Title = "Get message", ReadOnly = true, OpenWorld = true)]
    [Description("Returns one message with its full body (body.contentType, body.content), recipients and flags.")]
    public Task<CallToolResult> GetMessage(
        [Description(MailboxDescription)] string mailbox,
        [Description("Message id from list_messages.")] string id,
        CancellationToken ct = default) =>
        Call(async () => Structured(await graph.SendAsync(HttpMethod.Get, $"{Mailbox(mailbox)}/messages/{Uri.EscapeDataString(id)}", null, ct).ConfigureAwait(false)));

    /// <summary><c>list_attachments</c>.</summary>
    /// <param name="mailbox">Скринька.</param>
    /// <param name="message_id">Id листа.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "list_attachments", Title = "List attachments", ReadOnly = true, OpenWorld = true)]
    [Description("Lists attachments of a message without their content. Returns { result: [{ id, name, contentType, size, isInline }] }.")]
    public Task<CallToolResult> ListAttachments(
        [Description(MailboxDescription)] string mailbox,
        [Description("Message id.")] string message_id,
        CancellationToken ct = default) =>
        Call(async () => List(await graph.SendAsync(
            HttpMethod.Get,
            $"{Mailbox(mailbox)}/messages/{Uri.EscapeDataString(message_id)}/attachments?$select=id,name,contentType,size,isInline",
            null,
            ct).ConfigureAwait(false)));

    /// <summary><c>get_attachment</c>: вміст файлу — <c>resource</c>-блоком.</summary>
    /// <param name="mailbox">Скринька.</param>
    /// <param name="message_id">Id листа.</param>
    /// <param name="attachment_id">Id вкладення.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "get_attachment", Title = "Get attachment", ReadOnly = true, OpenWorld = true)]
    [Description("Downloads a file attachment. Returns its content as an embedded resource (blob) with the file name and MIME type.")]
    public Task<CallToolResult> GetAttachment(
        [Description(MailboxDescription)] string mailbox,
        [Description("Message id.")] string message_id,
        [Description("Attachment id from list_attachments.")] string attachment_id,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            var attachment = await graph.SendAsync(
                HttpMethod.Get,
                $"{Mailbox(mailbox)}/messages/{Uri.EscapeDataString(message_id)}/attachments/{Uri.EscapeDataString(attachment_id)}",
                null,
                ct).ConfigureAwait(false);
            if (attachment is not { ValueKind: JsonValueKind.Object } a || !a.TryGetProperty("contentBytes", out var bytes) || bytes.ValueKind != JsonValueKind.String)
            {
                throw new ConnectorException("Вкладення не є файлом (fileAttachment): елемент Outlook чи посилання вміст не віддають.");
            }

            var name = a.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } s ? s : "attachment";
            var mime = a.TryGetProperty("contentType", out var t) && t.GetString() is { Length: > 0 } m ? m : "application/octet-stream";
            return new CallToolResult
            {
                Content =
                [
                    new EmbeddedResourceBlock
                    {
                        Resource = new BlobResourceContents
                        {
                            Uri = $"msgraph://messages/{Uri.EscapeDataString(message_id)}/attachments/{Uri.EscapeDataString(name)}",
                            MimeType = mime,
                            Blob = System.Text.Encoding.UTF8.GetBytes(bytes.GetString()!),
                        },
                    },
                ],
            };
        });

    /// <summary><c>send_mail</c>.</summary>
    /// <param name="from">Скринька відправника.</param>
    /// <param name="to">Одержувачі.</param>
    /// <param name="subject">Тема.</param>
    /// <param name="html">Тіло HTML.</param>
    /// <param name="cc">Копія.</param>
    /// <param name="attachments">Вкладення.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "send_mail", Title = "Send mail", Destructive = false, OpenWorld = true)]
    [Description("Sends an HTML email from a mailbox (saved to Sent Items). Attachments are base64 files up to 3 MB each. Returns { sent: true }.")]
    public Task<CallToolResult> SendMail(
        [Description("Sender mailbox (the app needs Mail.Send for it).")] string from,
        [Description("Recipient email addresses.")] string[] to,
        [Description("Subject.")] string subject,
        [Description("HTML body.")] string html,
        [Description("Optional CC email addresses.")] string[]? cc = null,
        [Description("Optional file attachments.")] MailAttachment[]? attachments = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            if (to is not { Length: > 0 } || to.Any(string.IsNullOrWhiteSpace))
            {
                throw new ConnectorException("to — хоча б одна адреса.");
            }

            var files = (attachments ?? []).Select(f =>
            {
                try
                {
                    _ = Convert.FromBase64String(f.ContentBase64);
                }
                catch (FormatException)
                {
                    throw new ConnectorException($"Вкладення «{f.Name}»: content_base64 — некоректний base64.");
                }

                return new Dictionary<string, object?>
                {
                    ["@odata.type"] = "#microsoft.graph.fileAttachment",
                    ["name"] = f.Name,
                    ["contentType"] = f.ContentType ?? "application/octet-stream",
                    ["contentBytes"] = f.ContentBase64,
                };
            }).ToList();

            var message = new Dictionary<string, object?>
            {
                ["subject"] = subject,
                ["body"] = new { contentType = "HTML", content = html },
                ["toRecipients"] = Recipients(to),
            };
            if (cc is { Length: > 0 })
            {
                message["ccRecipients"] = Recipients(cc);
            }

            if (files.Count > 0)
            {
                message["attachments"] = files;
            }

            await graph.SendAsync(HttpMethod.Post, $"{Mailbox(from)}/sendMail", new { message, saveToSentItems = true }, ct).ConfigureAwait(false);
            return Structured(JsonSerializer.SerializeToElement(new { sent = true, from, to }));
        });

    internal static string Mailbox(string mailbox) =>
        string.IsNullOrWhiteSpace(mailbox) ? throw new ConnectorException("mailbox — адреса скриньки.") : $"users/{Uri.EscapeDataString(mailbox.Trim())}";

    private static object[] Recipients(IEnumerable<string> addresses) =>
        [.. addresses.Select(a => new { emailAddress = new { address = a.Trim() } })];

    /// <summary>Сторінка Graph <c>{ value: [...] }</c> → <c>{ result: [...] }</c>.</summary>
    private static CallToolResult List(JsonElement? page)
    {
        var value = page is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array
            ? v
            : JsonSerializer.SerializeToElement(Array.Empty<object>());
        return Structured(JsonSerializer.SerializeToElement(new { result = value }));
    }

    private static CallToolResult Structured(JsonElement? json)
    {
        var value = json ?? JsonSerializer.SerializeToElement(new { });
        return new CallToolResult { StructuredContent = value, Content = [new TextContentBlock { Text = value.GetRawText() }] };
    }

    /// <summary>Помилку конектора віддає агенту текстом (<see cref="McpException"/> → <c>isError</c>).</summary>
    private static async Task<CallToolResult> Call(Func<Task<CallToolResult>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (ConnectorException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}

/// <summary>Файл для <c>send_mail</c>.</summary>
/// <param name="Name">Назва файлу.</param>
/// <param name="ContentBase64">Вміст у base64.</param>
/// <param name="ContentType">MIME-тип.</param>
public sealed record MailAttachment(
    [property: JsonPropertyName("name"), Description("File name, e.g. report.xlsx.")] string Name,
    [property: JsonPropertyName("content_base64"), Description("File content in base64.")] string ContentBase64,
    [property: JsonPropertyName("content_type"), Description("Optional MIME type.")] string? ContentType = null);
