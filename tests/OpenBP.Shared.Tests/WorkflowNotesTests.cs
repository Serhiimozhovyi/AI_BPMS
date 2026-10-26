using System.Text.Json.Nodes;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;
using Codes = OpenBP.Shared.Validation.WorkflowValidationCodes;

namespace OpenBP.Shared.Tests;

/// <summary>F3-62: нотатки на канвасі у форматі (docs/workflow-format.md §«Нотатки»).</summary>
public class WorkflowNotesTests
{
    private static readonly WorkflowValidationOptions Active = new() { IsActive = true };

    /// <summary>Приклад з фікстури з двома нотатками: повна і мінімальна (без розміру й кольору).</summary>
    private static JsonObject Noted()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["notes"] = JsonNode.Parse("""
            [
              { "id": "note_1", "text": "## Вхід\n\nЗаявки з **Telegram**; пікове навантаження — ранок.", "position": [80, 40],
                "size": [260, 140], "color": "yellow" },
              { "id": "note_2", "text": "", "position": [620, 380] }
            ]
            """);
        return doc;
    }

    // ---------- Round-trip і моделі ----------

    [Fact]
    public void NotesRoundTripWithoutChanges()
    {
        var doc = Noted();

        var roundTripped = JsonNode.Parse(WorkflowJson.Serialize(WorkflowJson.Deserialize(doc.ToJsonString())));

        Assert.True(JsonNode.DeepEquals(doc, roundTripped), roundTripped?.ToJsonString(WorkflowJson.Options));
    }

    [Fact]
    public void NotesDeserializeIntoModels()
    {
        var workflow = WorkflowJson.Deserialize(Noted().ToJsonString());

        var notes = Assert.IsType<List<WorkflowNote>>(workflow.Notes);
        Assert.Equal(["note_1", "note_2"], notes.Select(n => n.Id));
        Assert.StartsWith("## Вхід", notes[0].Text, StringComparison.Ordinal);
        Assert.Equal([80, 40], notes[0].Position);
        Assert.Equal([260, 140], notes[0].Size!);
        Assert.Equal(NoteColors.Yellow, notes[0].Color);
        Assert.Equal("", notes[1].Text);
        Assert.Null(notes[1].Size);
        Assert.Null(notes[1].Color);
    }

    [Fact]
    public void UnknownFieldsInsideNotesArePreserved()
    {
        var doc = Noted();
        doc["notes"]![0]!["author"] = "agent";
        doc["notes"]![1]!["x-pinned"] = true;

        var roundTripped = JsonNode.Parse(WorkflowJson.Serialize(WorkflowJson.Deserialize(doc.ToJsonString())));

        Assert.True(JsonNode.DeepEquals(doc, roundTripped), roundTripped?.ToJsonString(WorkflowJson.Options));
    }

    /// <summary>
    /// «Міграція» формату: <c>notes</c> додано до formatVersion 1 як необов'язкове поле — документ без нотаток лишається
    /// валідним, після round-trip не отримує нового поля, а <c>formatVersion</c> не змінюється ні без нотаток, ні з ними.
    /// </summary>
    [Fact]
    public async Task DocumentWithoutNotesIsUnchangedAndFormatVersionStaysOne()
    {
        var doc = WorkflowTestData.ExampleNode;

        var workflow = WorkflowJson.Deserialize(doc.ToJsonString());
        var roundTripped = JsonNode.Parse(WorkflowJson.Serialize(workflow))!.AsObject();

        Assert.Equal(1, WorkflowDefinition.CurrentFormatVersion);
        Assert.Null(workflow.Notes);
        Assert.False(roundTripped.ContainsKey("notes"));
        Assert.True(JsonNode.DeepEquals(doc, roundTripped));
        Assert.Empty(await new WorkflowValidator().ValidateAsync(doc, Active));

        var noted = Noted();
        Assert.Equal(1, noted["formatVersion"]!.GetValue<int>());
        Assert.Equal(1, WorkflowJson.Deserialize(noted.ToJsonString()).FormatVersion);
        Assert.Empty(await new WorkflowValidator().ValidateAsync(noted, Active));
    }

    [Fact]
    public void ModelsSerializeOnlyWhatIsSet()
    {
        var workflow = WorkflowJson.Deserialize(WorkflowTestData.ExampleJson);
        workflow.Notes = [new WorkflowNote { Id = "note", Text = "Привіт", Position = [10, 20] }];

        var json = JsonNode.Parse(WorkflowJson.Serialize(workflow))!;

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""[ { "id": "note", "text": "Привіт", "position": [10, 20] } ]"""), json["notes"]), json["notes"]!.ToJsonString());
    }

    // ---------- Валідатор ігнорує нотатки ----------

    /// <summary>Нотатки не пов'язані з графом: порожній текст, положення поза вузлами й нотатки без жодного вузла поруч — не помилки.</summary>
    [Fact]
    public async Task ValidatorChecksOnlySchemaOfNotes()
    {
        var doc = Noted();
        doc["notes"]![0]!["position"] = JsonNode.Parse("[-5000, 99999]");
        doc["notes"]![0]!["text"] = "Посилання на [вузол](#n_ghost), якого немає — це лише текст.";

        Assert.Empty(await new WorkflowValidator().ValidateAsync(doc, Active));

        // Помилки графа не залежать від нотаток і не ховаються за ними.
        doc["connections"]![2]!["to"]!["node"] = "n_missing";
        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc));
        Assert.Equal(Codes.ConnectionNodeUnknown, error.Code);
    }

    // ---------- Схема ----------

    public static TheoryData<string> BrokenVariants =>
    [
        "note-without-id",
        "note-empty-id",
        "note-without-text",
        "note-without-position",
        "note-position-one-coordinate",
        "note-size-zero",
        "note-bad-color",
        "notes-not-array",
    ];

    [Theory]
    [MemberData(nameof(BrokenVariants))]
    public async Task SchemaRejectsBrokenNoteVariant(string variant)
    {
        var doc = Noted();
        switch (variant)
        {
            case "note-without-id":
                doc["notes"]![0]!.AsObject().Remove("id");
                break;
            case "note-empty-id":
                doc["notes"]![0]!["id"] = "";
                break;
            case "note-without-text":
                doc["notes"]![1]!.AsObject().Remove("text");
                break;
            case "note-without-position":
                doc["notes"]![1]!.AsObject().Remove("position");
                break;
            case "note-position-one-coordinate":
                doc["notes"]![0]!["position"] = JsonNode.Parse("[80]");
                break;
            case "note-size-zero":
                doc["notes"]![0]!["size"] = JsonNode.Parse("[0, 140]");
                break;
            case "note-bad-color":
                doc["notes"]![0]!["color"] = "#fac701";
                break;
            case "notes-not-array":
                doc["notes"] = "Нотатка";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant), variant, null);
        }

        var errors = await new WorkflowValidator().ValidateAsync(doc);

        var error = Assert.Single(errors);
        Assert.Equal(Codes.SchemaViolation, error.Code);
        Assert.StartsWith("/notes", error.Path);
    }
}
