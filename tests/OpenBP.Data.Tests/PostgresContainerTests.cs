using Npgsql;

namespace OpenBP.Data.Tests;

[Collection(PostgresCollection.Name)]
public class PostgresContainerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ConnectsToPostgres16()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SHOW server_version_num", connection);
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

        Assert.InRange(version, 160000, 169999);
    }
}
