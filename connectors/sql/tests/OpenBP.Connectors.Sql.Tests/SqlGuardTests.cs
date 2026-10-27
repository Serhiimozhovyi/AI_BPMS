namespace OpenBP.Connectors.Sql.Tests;

/// <summary>Розбір тексту SQL: одна інструкція, лише читання в <c>query</c>, рядки й коментарі за правилами діалекту.</summary>
public sealed class SqlGuardTests
{
    [Theory]
    [InlineData("select 1", SqlProvider.Postgres)]
    [InlineData("  WITH x AS (select 1) select * from x ;  ", SqlProvider.Postgres)]
    [InlineData("select ';' as semicolon -- ; drop", SqlProvider.Postgres)]
    [InlineData("select $tag$ ; delete $tag$", SqlProvider.Postgres)]
    [InlineData("select \"a;b\" from t /* ; */", SqlProvider.Postgres)]
    [InlineData("select 'it''s; fine'", SqlProvider.MySql)]
    [InlineData("select 'a\\'; still string' # ; comment", SqlProvider.MySql)]
    [InlineData("show tables", SqlProvider.MySql)]
    [InlineData("explain select * from t", SqlProvider.MySql)]
    public void Read_statements_pass(string sql, SqlProvider provider) =>
        Assert.NotEmpty(SqlGuard.ReadStatement(sql, provider));

    [Theory]
    [InlineData("delete from t", SqlProvider.Postgres)]
    [InlineData("select 1; delete from t", SqlProvider.Postgres)]
    [InlineData("select 'a\\'; delete from t; --'", SqlProvider.Postgres)]
    [InlineData("select 1 # 2; delete from t", SqlProvider.Postgres)]
    [InlineData("select * from t into outfile '/tmp/x'", SqlProvider.MySql)]
    [InlineData("insert into t values (1)", SqlProvider.MySql)]
    [InlineData("", SqlProvider.MySql)]
    [InlineData("  -- only a comment", SqlProvider.Postgres)]
    public void Writes_and_several_statements_are_rejected(string sql, SqlProvider provider) =>
        Assert.Throws<ConnectorException>(() => SqlGuard.ReadStatement(sql, provider));

    [Fact]
    public void Single_statement_drops_trailing_semicolon_and_keeps_text()
    {
        Assert.Equal("update t set a = ';'", SqlGuard.SingleStatement("update t set a = ';';  ", SqlProvider.Postgres));
        Assert.Throws<ConnectorException>(() => SqlGuard.SingleStatement("update t set a = 1; update t set a = 2", SqlProvider.MySql));
    }

    [Fact]
    public void Postgres_e_strings_honour_backslash_escapes()
    {
        Assert.NotEmpty(SqlGuard.ReadStatement("select E'a\\'; still string'", SqlProvider.Postgres));
    }
}
