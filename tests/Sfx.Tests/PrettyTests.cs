using Sfx.Core;
using Sfx.Tui;

namespace Sfx.Tests;

public class PrettyTests
{
    [Fact]
    public void Indents_json_objects_and_arrays()
    {
        var (text, format) = Inspect.TryPretty("""{"a":1,"b":[true,null,"x"]}""");

        Assert.Equal(Inspect.Format.Json, format);
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": [\n    true,\n    null,\n    \"x\"\n  ]\n}", text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Indents_xml_keeping_the_declaration_comments_and_attribute_order()
    {
        const string manifest = """<?xml version="1.0" encoding="utf-8"?><App Name="a" Version="1"><!-- note --><Svc Type="t"/></App>""";

        var (text, format) = Inspect.TryPretty(manifest);

        Assert.Equal(Inspect.Format.Xml, format);
        Assert.Equal(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<App Name=\"a\" Version=\"1\">\n  <!-- note -->\n  <Svc Type=\"t\" />\n</App>",
            text);
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("{not json")]
    [InlineData("<open><unclosed>")]
    [InlineData("42")]
    [InlineData("")]
    public void Leaves_anything_else_alone(string text)
    {
        Assert.Equal((text, Inspect.Format.None), Inspect.TryPretty(text));
    }

    [Theory]
    [InlineData("  \"Name\": \"fabric:/a\\\"b\",")]
    [InlineData("  \"Count\": -12.5e3,")]
    [InlineData("  \"Flags\": [true, false, null]")]
    [InlineData("}")]
    public void Coloring_json_keeps_the_text(string line)
    {
        Assert.Equal(line, Highlight.Json(line).PlainText);
    }

    [Theory]
    [InlineData("<Service Name=\"x\" Type='y'>")]
    [InlineData("  <Parameter Name=\"a\" Value=\"1 > 0\" />")]
    [InlineData("</Service>")]
    [InlineData("text <b>bold</b> more")]
    [InlineData("<?xml version=\"1.0\"?>")]
    public void Coloring_xml_keeps_the_text(string line)
    {
        var inComment = false;
        Assert.Equal(line, Highlight.Xml(line, ref inComment).PlainText);
        Assert.False(inComment);
    }

    [Fact]
    public void Carries_an_xml_comment_over_lines()
    {
        var inComment = false;

        Highlight.Xml("<!-- starts", ref inComment);
        Assert.True(inComment);
        Assert.Equal("  ends --> <A/>", Highlight.Xml("  ends --> <A/>", ref inComment).PlainText);
        Assert.False(inComment);
    }

    [Fact]
    public void Fields_holding_json_or_xml_unfold_below_their_name()
    {
        var lines = Lines.Fields(
        [
            new Field("Id", """{"Id":"x"}"""),
            new Field("Policy", """{"MaxPercentUnhealthy":0,"ConsiderWarningAsError":false}"""),
            new Field("Name", "fabric:/a"),
        ]).Select(l => l.PlainText).ToList();

        Assert.Equal(
        [
            "Id      {\"Id\":\"x\"}",
            "Policy  ",
            "  {",
            "    \"MaxPercentUnhealthy\": 0,",
            "    \"ConsiderWarningAsError\": false",
            "  }",
            "Name    fabric:/a",
        ], lines);
    }
}
