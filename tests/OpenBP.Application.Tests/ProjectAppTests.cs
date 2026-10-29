using OpenBP.Application.Projects;

namespace OpenBP.Application.Tests;

/// <summary>F8-14: перевірка оболонки застосунку проєкту — назва, пояс, головна й меню зі сторінок проєкту.</summary>
public class ProjectAppTests
{
    private static readonly HashSet<string> Pages = new(
        new[] { "today", "my-calendar", "bookings", "settings-form" }.Concat(Enumerable.Range(0, 30).Select(i => $"p{i}")), StringComparer.Ordinal);

    [Fact]
    public void Valid_shell_is_trimmed_and_round_trips_through_json()
    {
        var app = new ProjectApp("  Кабінет ")
        {
            Icon = "calendar_month",
            Home = "today",
            Timezone = "Europe/Kyiv",
            Nav = [new ProjectAppNav("", ["today", " my-calendar"]), new ProjectAppNav("Записи", ["bookings"])],
        };

        var valid = ProjectApps.Validate(app, Pages);
        var parsed = ProjectApps.Parse(ProjectApps.Serialize(valid));

        Assert.Equal("Кабінет", valid.Title);
        Assert.Equal(["today", "my-calendar"], valid.Nav![0].Pages);
        Assert.Equal(ProjectApps.Serialize(valid), ProjectApps.Serialize(parsed));
        Assert.Equal("Europe/Kyiv", parsed!.Timezone);
        Assert.Null(ProjectApps.Parse(ProjectApps.Serialize(null)));
        Assert.Null(ProjectApps.Parse("{ зіпсовано"));
    }

    [Fact]
    public void Minimal_shell_has_only_a_title()
    {
        var valid = ProjectApps.Validate(new ProjectApp("Кабінет") { Icon = " ", Home = "", Nav = [] }, Pages);

        Assert.Null(valid.Icon);
        Assert.Null(valid.Home);
        Assert.Null(valid.Nav);
        Assert.Null(valid.Timezone);
    }

    public static TheoryData<ProjectApp, string> Wrong => new()
    {
        { new ProjectApp(""), "Назва" },
        { new ProjectApp(new string('x', 101)), "Назва" },
        { new ProjectApp("К") { Timezone = "Kyiv/Europe" }, "пояс" },
        { new ProjectApp("К") { Home = "missing" }, "Головної" },
        { new ProjectApp("К") { Nav = [new ProjectAppNav("x", ["missing"])] }, "меню" },
        { new ProjectApp("К") { Nav = [new ProjectAppNav("x", ["today"]), new ProjectAppNav("y", ["today"])] }, "двічі" },
        { new ProjectApp("К") { Nav = [new ProjectAppNav("x", [])] }, "без сторінок" },
        { new ProjectApp("К") { Nav = [.. Enumerable.Range(0, 21).Select(i => new ProjectAppNav($"g{i}", [$"p{i}"]))] }, "не більше" },
    };

    [Theory]
    [MemberData(nameof(Wrong))]
    public void Wrong_shell_is_rejected(ProjectApp app, string hint)
    {
        var ex = Assert.Throws<ArgumentException>(() => ProjectApps.Validate(app, Pages));

        Assert.Contains(hint, ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
