namespace OpenBP.Engine.Testing.Tests;

public class InMemoryExecutionLoggerTests
{
    [Fact]
    public void Log_CollectsEntriesInOrder_WithNodeAndData()
    {
        var logger = new InMemoryExecutionLogger();
        var data = new { tries = 2 };

        logger.Log(LogLevel.Info, "n_1", "старт");
        logger.Log(LogLevel.Warning, "n_1", "повтор", data);
        logger.Log(LogLevel.Error, null, "виконання впало");

        Assert.Equal(3, logger.Entries.Count);
        Assert.Equal(["старт", "повтор", "виконання впало"], logger.Entries.Select(e => e.Message));
        Assert.Equal(2, logger.ForNode("n_1").Count);
        Assert.Same(data, logger.Entries[1].Data);
        Assert.Null(logger.Entries[2].NodeId);
        Assert.Equal(LogLevel.Error, logger.Entries[2].Level);
    }

    [Fact]
    public void Clear_RemovesEntries()
    {
        var logger = new InMemoryExecutionLogger();
        logger.Log(LogLevel.Debug, "n_1", "x");

        logger.Clear();

        Assert.Empty(logger.Entries);
    }
}
