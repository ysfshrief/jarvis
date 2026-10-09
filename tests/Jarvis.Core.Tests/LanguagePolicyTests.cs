using Jarvis.Core.Language;
using Jarvis.Core.Settings;
using Xunit;

namespace Jarvis.Core.Tests;

public class LanguagePolicyTests
{
    [Theory]
    [InlineData("auto", "en", Lang.En)]
    [InlineData("auto", "ar", Lang.Ar)] // "auto" follows the interface when there's no message to go by
    [InlineData("en", "ar", Lang.En)]   // an explicit reply language wins
    [InlineData("ar", "en", Lang.Ar)]
    public void Language_for_things_JARVIS_says_on_its_own(string reply, string ui, Lang expected)
    {
        var s = new JarvisSettings();
        s.General.Language = reply;
        s.Appearance.Language = ui;
        Assert.Equal(expected, LanguagePolicy.Default(s));
    }
}
