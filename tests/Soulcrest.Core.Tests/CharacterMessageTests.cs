using System.Text;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>
/// "Entering the world" message 33 36 (user capture 2026-10-06). The header bytes are those of the
/// capture; the names are made up, real character names stay out of the repository.
/// </summary>
public sealed class CharacterMessageTests
{
    private static byte[] Message(string name, string header = "3336c3245fa1c12837", int size = 2300, byte? length = null)
    {
        var bytes = new List<byte>(Convert.FromHexString(header));
        var text = Encoding.UTF8.GetBytes(name);
        bytes.Add(length ?? (byte)text.Length);
        bytes.AddRange(text);
        bytes.AddRange(Convert.FromHexString("00091f000000022d000000"));
        while (bytes.Count < size)
            bytes.Add(0);
        return [.. bytes];
    }

    [Theory]
    [InlineData("3336c3245fa1c12837", "Testchar")]
    [InlineData("3336a0035e81c10837", "Zweit")]   // other varint widths, as for the second character
    [InlineData("3336c4285fa1c12837", "Ärwin")]   // UTF-8 letters
    public void ReadsTheOwnCharactersName(string header, string name) =>
        Assert.Equal(name, CharacterMessage.TryParseName(Message(name, header)));

    [Fact]
    public void OtherMessagesAreNoCharacter()
    {
        Assert.Null(CharacterMessage.TryParseName(Message("Testchar", "048d9bec0386b10501836c0009")));  // nameplate of any player
        Assert.Null(CharacterMessage.TryParseName(Message("Testchar", size: 120)));                    // too short for the world entry
        Assert.Null(CharacterMessage.TryParseName(Message("Test char")));                              // not a character name
        Assert.Null(CharacterMessage.TryParseName(Message("Testchar", length: 1)));
        Assert.Null(CharacterMessage.TryParseName(Message("Testchar", length: 200)));
        Assert.Null(CharacterMessage.TryParseName(Convert.FromHexString("3336ffffffffff")));            // endless varint
    }
}
