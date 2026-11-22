// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Ini;

namespace Osctl.Tests.Ini;

// Each case follows Nini's IniReader in standard mode (opensim-libs, Source/Ini/IniReader.cs).
public class IniParserTests
{
    private static string? Value(string text, string section = "S", string key = "k") =>
        IniParser.Parse(text).Get(section, key);

    [Theory]
    [InlineData("[S]\nk = v", "v")]
    [InlineData("[S]\nk=v", "v")]
    [InlineData("[S]\n   k   =    v   ", "v")]
    [InlineData("[S]\n\tk\t=\tv\t", "v")]
    [InlineData("[S]\nk = two words", "two words")]
    [InlineData("[S]\nk =", "")]
    [InlineData("[S]\nk = v ; comment", "v")]
    [InlineData("[S]\nk = v;comment", "v")]
    [InlineData("[S]\nk = # not a comment", "# not a comment")]
    [InlineData("[S]\nk = a=b", "a=b")]
    [InlineData("[S]\nk = ${Const|BaseURL}:8002", "${Const|BaseURL}:8002")]
    public void UnquotedValues(string text, string expected) => Assert.Equal(expected, Value(text));

    [Theory]
    [InlineData("[S]\nk = \"v\"", "v")]
    [InlineData("[S]\nk = \"  spaced  \"", "  spaced  ")]
    [InlineData("[S]\nk = \"a;b\"", "a;b")]
    [InlineData("[S]\nk = \"\"", "")]
    [InlineData("[S]\nk = \"v\" ; comment", "v")]
    [InlineData("[S]\nk = \"v\" anything after the quote is ignored", "v")]
    [InlineData("[S]\nk = \"Data Source=localhost;Database=opensim;User ID=opensim;Password=***;Old Guids=true;\"",
        "Data Source=localhost;Database=opensim;User ID=opensim;Password=***;Old Guids=true;")]
    public void QuotedValues(string text, string expected) => Assert.Equal(expected, Value(text));

    [Fact]
    public void AQuoteInsideAnUnquotedValueEndsIt() => Assert.Equal("foo", Value("[S]\nk = foo\"bar\""));

    [Fact]
    public void CarriageReturnsAreIgnored()
    {
        var doc = IniParser.Parse("[S]\r\nk = v\r\nq = \"x\"\r\n");
        Assert.Equal("v", doc.Get("S", "k"));
        Assert.Equal("x", doc.Get("S", "q"));
    }

    [Fact]
    public void CommentsAndBlankLinesAreSkipped()
    {
        var doc = IniParser.Parse("; top\n\n[S]\n    ; indented comment\n;# {Key} {} {doc} {}\nk = v\n");
        Assert.Equal(["k"], doc["S"]!.Keys);
    }

    [Theory]
    [InlineData("[Startup]", "Startup")]
    [InlineData("[Startup]   ; trailing comment", "Startup")]
    [InlineData("[Startup] junk", "Startup")]
    [InlineData("[ClientStack.LindenUDP]", "ClientStack.LindenUDP")]
    [InlineData("[Region Defaults  ]", "Region Defaults")]
    [InlineData("   [Indented]", "Indented")]
    public void SectionNames(string header, string expected) =>
        Assert.Equal(expected, IniParser.Parse(header + "\nk = v").Sections.Single().Name);

    [Fact]
    public void NamesAreCaseSensitive()
    {
        var doc = IniParser.Parse("[S]\nKey = 1\nkey = 2\n[s]\nKey = 3");
        Assert.Equal("1", doc.Get("S", "Key"));
        Assert.Equal("2", doc.Get("S", "key"));
        Assert.Equal("3", doc.Get("s", "Key"));
    }

    [Fact]
    public void ARepeatedKeyKeepsItsFirstValue() => Assert.Equal("first", Value("[S]\nk = first\nk = second"));

    [Fact]
    public void ARepeatedSectionReplacesTheEarlierOne()
    {
        var doc = IniParser.Parse("[S]\na = 1\nb = 2\n[T]\nx = 1\n[S]\nb = 3\n");
        Assert.Equal(["T", "S"], doc.Sections.Select(s => s.Name));
        Assert.Null(doc.Get("S", "a"));
        Assert.Equal("3", doc.Get("S", "b"));
    }

    [Fact]
    public void KeysKeepTheirOrder()
    {
        var doc = IniParser.Parse("[S]\nz = 1\na = 2\nm = 3");
        Assert.Equal(["z", "a", "m"], doc["S"]!.Keys);
    }

    [Fact]
    public void OriginsGiveFileAndLine()
    {
        var doc = IniParser.Parse("[S]\n\n; c\nk = v", "test.ini");
        Assert.Equal(new IniOrigin("test.ini", 4), doc["S"]!.GetEntry("k")!.Origin);
    }

    [Theory]
    [InlineData("[S", "Expected section end", 1)]
    [InlineData("[S]\njust words", "Expected assignment operator", 2)]
    [InlineData("[S]\n# comment-looking line", "Expected assignment operator", 2)]
    [InlineData("[S]\nk = \"unclosed", "Expected closing quote", 2)]
    [InlineData("k = v", "outside any section", 1)]
    public void Errors(string text, string message, int line)
    {
        var e = Assert.Throws<IniParseException>(() => IniParser.Parse(text, "bad.ini"));
        Assert.Contains(message, e.Message);
        Assert.Equal(new IniOrigin("bad.ini", line), e.Origin);
    }

    [Fact]
    public void ByteOrderMarkIsDropped()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "bom.ini");
        File.WriteAllText(path, "[S]\nk = v\n", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal("v", IniParser.ParseFile(path).Get("S", "k"));
    }
}
