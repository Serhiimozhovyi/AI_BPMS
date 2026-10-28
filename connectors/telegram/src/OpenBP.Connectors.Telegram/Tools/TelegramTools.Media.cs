using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace OpenBP.Connectors.Telegram.Tools;

public sealed partial class TelegramTools
{
    /// <summary>Найбільший файл, який Bot API приймає завантаженням (<c>multipart</c>), байтів.</summary>
    public const int MaxUploadBytes = 50 * 1024 * 1024;

    private const string FileIdDescription = "Telegram file_id of a file the bot already has (received in an update or sent before); sends it again without uploading. ";

    /// <summary><c>send_document</c> → <c>sendDocument</c>: за URL, <c>file_id</c> або вмістом base64.</summary>
    /// <param name="chat_id">Чат.</param>
    /// <param name="document_url">URL файлу (Telegram завантажить його сам).</param>
    /// <param name="document_base64">Вміст файлу в base64.</param>
    /// <param name="file_id">file_id файлу, який уже є в Telegram.</param>
    /// <param name="file_name">Назва файлу для base64.</param>
    /// <param name="caption">Підпис.</param>
    /// <param name="parse_mode">Розмітка підпису.</param>
    /// <param name="reply_markup">Клавіатура (об'єкт Bot API).</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "send_document", Title = "Send document", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Sends a file to a Telegram chat: by public URL (document_url), by Telegram file_id, or as base64 content (document_base64, up to 50 MB), with an optional caption and buttons. Returns the sent message.")]
    public Task<TelegramMessage> SendDocument(
        [Description(ChatIdDescription)] JsonElement chat_id,
        [Description("Public HTTP URL of the file; Telegram downloads it. Use exactly one of document_url, file_id, document_base64.")] string? document_url = null,
        [Description("File content in base64 (up to 50 MB). Use exactly one of document_url, file_id, document_base64.")] string? document_base64 = null,
        [Description(FileIdDescription + "Use exactly one of document_url, file_id, document_base64.")] string? file_id = null,
        [Description("File name for document_base64, e.g. report.pdf.")] string? file_name = null,
        [Description("Optional caption, 0-1024 characters.")] string? caption = null,
        [Description("Optional caption formatting: HTML, MarkdownV2 or Markdown.")] string? parse_mode = null,
        [Description(ReplyMarkupDescription)] JsonElement? reply_markup = null,
        CancellationToken ct = default) =>
        Call(() => SendFile("sendDocument", "document", chat_id, document_url, file_id, document_base64, file_name, caption, parse_mode, reply_markup, null, ct));

    /// <summary><c>send_video</c> → <c>sendVideo</c>.</summary>
    /// <param name="chat_id">Чат.</param>
    /// <param name="video_url">URL відео.</param>
    /// <param name="video_base64">Вміст відео в base64.</param>
    /// <param name="file_id">file_id відео, яке вже є в Telegram.</param>
    /// <param name="file_name">Назва файлу для base64.</param>
    /// <param name="caption">Підпис.</param>
    /// <param name="parse_mode">Розмітка підпису.</param>
    /// <param name="reply_markup">Клавіатура (об'єкт Bot API).</param>
    /// <param name="duration">Тривалість, секунд.</param>
    /// <param name="width">Ширина.</param>
    /// <param name="height">Висота.</param>
    /// <param name="supports_streaming">Відео можна дивитися, не завантаживши повністю.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "send_video", Title = "Send video", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Sends a video (MPEG4) to a Telegram chat: by public URL (video_url), by Telegram file_id, or as base64 content (video_base64, up to 50 MB), with an optional caption and buttons. Returns the sent message.")]
    public Task<TelegramMessage> SendVideo(
        [Description(ChatIdDescription)] JsonElement chat_id,
        [Description("Public HTTP URL of the video; Telegram downloads it. Use exactly one of video_url, file_id, video_base64.")] string? video_url = null,
        [Description("Video content in base64 (up to 50 MB). Use exactly one of video_url, file_id, video_base64.")] string? video_base64 = null,
        [Description(FileIdDescription + "Use exactly one of video_url, file_id, video_base64.")] string? file_id = null,
        [Description("File name for video_base64, e.g. intro.mp4.")] string? file_name = null,
        [Description("Optional caption, 0-1024 characters.")] string? caption = null,
        [Description("Optional caption formatting: HTML, MarkdownV2 or Markdown.")] string? parse_mode = null,
        [Description(ReplyMarkupDescription)] JsonElement? reply_markup = null,
        [Description("Optional duration in seconds.")] int? duration = null,
        [Description("Optional video width.")] int? width = null,
        [Description("Optional video height.")] int? height = null,
        [Description("true if the video can be streamed (MPEG4 with the index at the start).")] bool? supports_streaming = null,
        CancellationToken ct = default) =>
        Call(() => SendFile("sendVideo", "video", chat_id, video_url, file_id, video_base64, file_name, caption, parse_mode, reply_markup,
            new Dictionary<string, object?> { ["duration"] = duration, ["width"] = width, ["height"] = height, ["supports_streaming"] = supports_streaming }, ct));

    /// <summary><c>send_audio</c> → <c>sendAudio</c>.</summary>
    /// <param name="chat_id">Чат.</param>
    /// <param name="audio_url">URL аудіо.</param>
    /// <param name="audio_base64">Вміст аудіо в base64.</param>
    /// <param name="file_id">file_id аудіо, яке вже є в Telegram.</param>
    /// <param name="file_name">Назва файлу для base64.</param>
    /// <param name="caption">Підпис.</param>
    /// <param name="parse_mode">Розмітка підпису.</param>
    /// <param name="reply_markup">Клавіатура (об'єкт Bot API).</param>
    /// <param name="duration">Тривалість, секунд.</param>
    /// <param name="performer">Виконавець.</param>
    /// <param name="title">Назва доріжки.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "send_audio", Title = "Send audio", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Sends an audio track (MP3 or M4A, shown in the music player) to a Telegram chat: by public URL (audio_url), by Telegram file_id, or as base64 content (audio_base64, up to 50 MB). Returns the sent message.")]
    public Task<TelegramMessage> SendAudio(
        [Description(ChatIdDescription)] JsonElement chat_id,
        [Description("Public HTTP URL of the audio; Telegram downloads it. Use exactly one of audio_url, file_id, audio_base64.")] string? audio_url = null,
        [Description("Audio content in base64 (up to 50 MB). Use exactly one of audio_url, file_id, audio_base64.")] string? audio_base64 = null,
        [Description(FileIdDescription + "Use exactly one of audio_url, file_id, audio_base64.")] string? file_id = null,
        [Description("File name for audio_base64, e.g. meditation.mp3.")] string? file_name = null,
        [Description("Optional caption, 0-1024 characters.")] string? caption = null,
        [Description("Optional caption formatting: HTML, MarkdownV2 or Markdown.")] string? parse_mode = null,
        [Description(ReplyMarkupDescription)] JsonElement? reply_markup = null,
        [Description("Optional duration in seconds.")] int? duration = null,
        [Description("Optional performer shown in the player.")] string? performer = null,
        [Description("Optional track title shown in the player.")] string? title = null,
        CancellationToken ct = default) =>
        Call(() => SendFile("sendAudio", "audio", chat_id, audio_url, file_id, audio_base64, file_name, caption, parse_mode, reply_markup,
            new Dictionary<string, object?> { ["duration"] = duration, ["performer"] = performer, ["title"] = title }, ct));

    /// <summary><c>send_voice</c> → <c>sendVoice</c>.</summary>
    /// <param name="chat_id">Чат.</param>
    /// <param name="voice_url">URL запису.</param>
    /// <param name="voice_base64">Вміст запису в base64.</param>
    /// <param name="file_id">file_id запису, який уже є в Telegram.</param>
    /// <param name="file_name">Назва файлу для base64.</param>
    /// <param name="caption">Підпис.</param>
    /// <param name="parse_mode">Розмітка підпису.</param>
    /// <param name="reply_markup">Клавіатура (об'єкт Bot API).</param>
    /// <param name="duration">Тривалість, секунд.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "send_voice", Title = "Send voice message", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Sends a voice message (OGG with OPUS, MP3 or M4A) to a Telegram chat: by public URL (voice_url), by Telegram file_id, or as base64 content (voice_base64, up to 50 MB). Returns the sent message.")]
    public Task<TelegramMessage> SendVoice(
        [Description(ChatIdDescription)] JsonElement chat_id,
        [Description("Public HTTP URL of the recording; Telegram downloads it. Use exactly one of voice_url, file_id, voice_base64.")] string? voice_url = null,
        [Description("Recording content in base64 (up to 50 MB). Use exactly one of voice_url, file_id, voice_base64.")] string? voice_base64 = null,
        [Description(FileIdDescription + "Use exactly one of voice_url, file_id, voice_base64.")] string? file_id = null,
        [Description("File name for voice_base64, e.g. note.ogg.")] string? file_name = null,
        [Description("Optional caption, 0-1024 characters.")] string? caption = null,
        [Description("Optional caption formatting: HTML, MarkdownV2 or Markdown.")] string? parse_mode = null,
        [Description(ReplyMarkupDescription)] JsonElement? reply_markup = null,
        [Description("Optional duration in seconds.")] int? duration = null,
        CancellationToken ct = default) =>
        Call(() => SendFile("sendVoice", "voice", chat_id, voice_url, file_id, voice_base64, file_name, caption, parse_mode, reply_markup,
            new Dictionary<string, object?> { ["duration"] = duration }, ct));

    /// <summary>
    /// Спільна частина <c>send_document</c>/<c>send_video</c>/<c>send_audio</c>/<c>send_voice</c>: файл — рівно одне з URL,
    /// <c>file_id</c> (обидва йдуть у те саме поле Bot API JSON-ом) або base64 (multipart).
    /// </summary>
    private Task<TelegramMessage> SendFile(
        string method,
        string field,
        JsonElement chat_id,
        string? url,
        string? fileId,
        string? base64,
        string? fileName,
        string? caption,
        string? parseMode,
        JsonElement? replyMarkup,
        Dictionary<string, object?>? extra,
        CancellationToken ct)
    {
        var chat = ChatId(chat_id);
        var given = new[] { url, fileId, base64 }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (given != 1)
        {
            throw new ConnectorException($"Задайте рівно один з аргументів {field}_url, file_id або {field}_base64.");
        }

        if (caption is { Length: > MaxCaptionLength })
        {
            throw new ConnectorException($"caption — до {MaxCaptionLength} символів, а в тексті {caption.Length}: скоротіть підпис або надішліть решту окремим повідомленням.");
        }

        var markup = ReplyMarkup(replyMarkup);
        if (string.IsNullOrWhiteSpace(base64))
        {
            var body = new Dictionary<string, object?>
            {
                ["chat_id"] = chat,
                [field] = string.IsNullOrWhiteSpace(url) ? fileId : url,
                ["caption"] = caption,
                ["parse_mode"] = parseMode,
                ["reply_markup"] = markup,
            };
            foreach (var (key, value) in extra ?? [])
            {
                body[key] = value;
            }

            return api.CallAsync<TelegramMessage>(method, body, ct);
        }

        // Спершу оцінка за довжиною рядка, щоб не розкодовувати гігабайти заради відмови.
        var tooBig = $"{field}_base64 — файл понад {MaxUploadBytes / 1024 / 1024} МБ: Bot API не приймає такі файли завантаженням. Дайте посилання на нього ({field}_url) або file_id.";
        if (base64.Length / 4L * 3 > MaxUploadBytes + 3)
        {
            throw new ConnectorException(tooBig);
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new ConnectorException($"{field}_base64 — некоректний base64.");
        }

        if (bytes.Length > MaxUploadBytes)
        {
            throw new ConnectorException(tooBig);
        }

        var form = new MultipartFormDataContent
        {
            { new StringContent(chat is string s ? s : ((JsonElement)chat).GetRawText()), "chat_id" },
        };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, field, string.IsNullOrWhiteSpace(fileName) ? field : fileName);
        AddField(form, "caption", caption);
        AddField(form, "parse_mode", parseMode);
        AddField(form, "reply_markup", markup?.GetRawText());
        foreach (var (key, value) in extra ?? [])
        {
            AddField(form, key, value switch
            {
                null => null,
                bool b => b ? "true" : "false",
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        return api.CallAsync<TelegramMessage>(method, form, ct);
    }

    private static void AddField(MultipartFormDataContent form, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            form.Add(new StringContent(value), name);
        }
    }
}
