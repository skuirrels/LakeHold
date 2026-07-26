using Lakehold.Engine.Catalog;
using Xunit;

namespace Lakehold.Engine.Tests;

/// <summary>Catalog-alias validation at the final boundary before DuckDB ATTACH.</summary>
public sealed class SqlIdentifierTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("MAIN")]
    [InlineData("system")]
    [InlineData("temp")]
    public void DuckDB_reserved_catalog_names_are_refused(string name)
    {
        Assert.False(SqlIdentifier.IsValidCatalogName(name));

        var error = Assert.Throws<ArgumentException>(() => SqlIdentifier.ValidateCatalogName(name));
        Assert.Contains("reserved by DuckDB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_regular_catalog_name_remains_valid()
    {
        Assert.True(SqlIdentifier.IsValidCatalogName("analytics"));
        Assert.Equal("analytics", SqlIdentifier.ValidateCatalogName("analytics"));
    }
}
