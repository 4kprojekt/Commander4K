using Commander4K.Core.Plugins;

namespace Commander4K.Core.Tests;

public sealed class PreviewMovesTests
{
    private const string From = @"C:\Users\a\Poczta\Firma\Odebrane\x.eml";

    [Theory]
    [InlineData(@"C:\Users\a\Poczta\Firma\Odebrane\read.eml")]
    [InlineData(@"C:\Users\a\Poczta\Firma\Szkice\draft.eml")]
    [InlineData(@"C:\Users\a\Poczta\Firma\Kosz\Podfolder\x.eml")]
    [InlineData(@"C:\Users\a\Poczta\Inne\Odebrane\x.eml")]
    [InlineData(@"C:\Users\a\Poczta\x.eml")]
    public void Entries_within_the_third_ancestor_are_acceptable(string to) => Assert.True(PreviewMoves.IsAcceptable(From, to));

    [Theory]
    [InlineData(@"C:\Users\a\x.eml")]
    [InlineData(@"C:\Users\a\Poczta2\Firma\Odebrane\x.eml")]
    [InlineData(@"D:\Users\a\Poczta\Firma\Odebrane\x.eml")]
    [InlineData(@"C:\Users\a\Poczta\Firma\..\..\..\Windows\x.eml")]
    [InlineData(@"C:\Users\a\Poczta\Firma\Szkice\")]
    [InlineData(@"relative\x.eml")]
    [InlineData("sftp://host/Poczta/Firma/Odebrane/x.eml")]
    [InlineData("")]
    public void Other_places_are_refused(string to) => Assert.False(PreviewMoves.IsAcceptable(From, to));

    [Fact]
    public void The_scheme_must_match()
    {
        Assert.True(PreviewMoves.IsAcceptable("sftp://host/Poczta/Firma/Odebrane/x.eml", "sftp://host/Poczta/Firma/Szkice/y.eml"));
        Assert.False(PreviewMoves.IsAcceptable("sftp://host/Poczta/Firma/Odebrane/x.eml", "ftp://host/Poczta/Firma/Szkice/y.eml"));
        Assert.False(PreviewMoves.IsAcceptable("sftp://host/Poczta/Firma/Odebrane/x.eml", "sftp://host/Poczta/../etc/y.eml"));
    }
}
