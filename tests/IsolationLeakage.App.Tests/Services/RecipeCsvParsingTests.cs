using FluentAssertions;
using IsolationLeakage.App.Services;
using Xunit;

namespace IsolationLeakage.App.Tests.Services;

/// <summary>
/// CSV 解析纯逻辑测试（不依赖数据库）
/// </summary>
public class RecipeCsvParsingTests
{
    // ── ParseCsvLine ──

    [Fact]
    public void ParseCsvLine_SimpleRow_ReturnsCorrectFields()
    {
        var result = RecipeService.ParseCsvLine("AA,1,CAS,10.2");
        result.Should().Equal("AA", "1", "CAS", "10.2");
    }

    [Fact]
    public void ParseCsvLine_QuotedFieldWithComma_ReturnsSingleField()
    {
        var result = RecipeService.ParseCsvLine("\"Hello, World\",1,CAS");
        result.Should().Equal("Hello, World", "1", "CAS");
    }

    [Fact]
    public void ParseCsvLine_EscapedQuotes_ReturnsUnescaped()
    {
        // CSV 标准：双引号转义为 ""
        var result = RecipeService.ParseCsvLine("\"He said \"\"hi\"\"\",1");
        result.Should().Equal("He said \"hi\"", "1");
    }

    [Fact]
    public void ParseCsvLine_EmptyFields_ReturnsEmptyStrings()
    {
        var result = RecipeService.ParseCsvLine(",1,,CAS,");
        result.Should().Equal("", "1", "", "CAS", "");
    }

    [Fact]
    public void ParseCsvLine_SingleField_ReturnsOneElement()
    {
        var result = RecipeService.ParseCsvLine("hello");
        result.Should().HaveCount(1);
        result[0].Should().Be("hello");
    }

    [Fact]
    public void ParseCsvLine_EmptyString_ReturnsOneEmptyElement()
    {
        var result = RecipeService.ParseCsvLine("");
        result.Should().Equal("");
    }

    [Fact]
    public void ParseCsvLine_QuotedEmptyField_ReturnsEmptyString()
    {
        var result = RecipeService.ParseCsvLine("\"\",1");
        result.Should().Equal("", "1");
    }

    // ── CsvEscape ──

    [Fact]
    public void CsvEscape_NormalString_ReturnsUnchanged()
    {
        RecipeService.CsvEscape("hello").Should().Be("hello");
    }

    [Fact]
    public void CsvEscape_CommaInValue_WrapsInQuotes()
    {
        RecipeService.CsvEscape("a,b").Should().Be("\"a,b\"");
    }

    [Fact]
    public void CsvEscape_QuotesInValue_EscapesAndWraps()
    {
        RecipeService.CsvEscape("He \"said\"").Should().Be("\"He \"\"said\"\"\"");
    }

    [Fact]
    public void CsvEscape_NewlineInValue_WrapsInQuotes()
    {
        RecipeService.CsvEscape("line1\nline2").Should().Be("\"line1\nline2\"");
    }

    [Fact]
    public void CsvEscape_EmptyString_ReturnsEmpty()
    {
        RecipeService.CsvEscape("").Should().BeEmpty();
    }

    // ── BuildColumnMap ──

    [Fact]
    public void BuildColumnMap_MapsHeadersCorrectly()
    {
        var headers = new List<string> { "配方名称", "序号", "系统" };
        var map = RecipeService.BuildColumnMap(headers);

        map.Should().ContainKey("配方名称").WhoseValue.Should().Be(0);
        map.Should().ContainKey("序号").WhoseValue.Should().Be(1);
        map.Should().ContainKey("系统").WhoseValue.Should().Be(2);
    }

    [Fact]
    public void BuildColumnMap_TrimWhitespace()
    {
        var headers = new List<string> { " 配方名称 ", " 序号 " };
        var map = RecipeService.BuildColumnMap(headers);

        map.Should().ContainKey("配方名称");
        map.Should().ContainKey("序号");
    }

    // ── FieldAt ──

    [Fact]
    public void FieldAt_InRange_ReturnsTrimmedValue()
    {
        var fields = new List<string> { "  AA  ", " 1 ", "CAS" };
        RecipeService.FieldAt(fields, 0).Should().Be("AA");
        RecipeService.FieldAt(fields, 1).Should().Be("1");
    }

    [Fact]
    public void FieldAt_OutOfRange_ReturnsEmptyString()
    {
        var fields = new List<string> { "AA" };
        RecipeService.FieldAt(fields, 5).Should().BeEmpty();
    }

    [Fact]
    public void FieldAt_ExactBoundary_ReturnsValue()
    {
        var fields = new List<string> { "AA", "BB" };
        RecipeService.FieldAt(fields, 1).Should().Be("BB");
    }
}
