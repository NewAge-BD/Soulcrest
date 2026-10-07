using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OcrLanguageInstallerTests
{
    [Theory]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void InstallationUsesWindowsCapabilityWithElevationAndNoRestart(string language)
    {
        var command = OcrLanguageInstaller.InstallationCommand(language);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe"), command.FileName);
        Assert.Equal($"/Online /Add-Capability /CapabilityName:Language.OCR~~~{language}~0.0.1.0 /NoRestart /Quiet", command.Arguments);
        Assert.True(command.UseShellExecute);
        Assert.Equal("runas", command.Verb);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, command.WindowStyle);
    }

    [Theory]
    [InlineData("")]
    [InlineData("de-DE /Restart")]
    [InlineData("fr-FR")]
    public void UnsupportedInputCannotBecomeAnInstallationCommand(string language)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OcrLanguageInstaller.InstallationCommand(language));
    }
}
