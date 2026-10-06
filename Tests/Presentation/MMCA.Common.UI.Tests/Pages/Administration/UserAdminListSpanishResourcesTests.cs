using System.Collections;
using System.Globalization;
using System.Resources;
using AwesomeAssertions;
using MMCA.Common.UI.Pages.Administration;

namespace MMCA.Common.UI.Tests.Pages.Administration;

/// <summary>
/// Pins the Spanish strings of the user-administration list to correctly accented wording: the
/// file once shipped "correo electronico" and similar unaccented forms on screen.
/// </summary>
public sealed class UserAdminListSpanishResourcesTests
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es");

    private static readonly ResourceManager Resources = new(typeof(UserAdminListResources));

    [Theory]
    [InlineData("Column.Email", "Correo electrónico")]
    [InlineData("Placeholder.Search", "Buscar por dirección de correo exacta...")]
    public void SpanishEmailWording_IsAccented(string key, string expected) =>
        Resources.GetString(key, Spanish).Should().Be(expected);

    [Fact]
    public void NoSpanishValue_UsesAKnownUnaccentedForm()
    {
        string[] unaccented = ["electronico", "administracion", "sesion", "cerrara", "podra", "volvera", "elimino"];

        var set = Resources.GetResourceSet(Spanish, createIfNotExists: true, tryParents: false);
        set.Should().NotBeNull("the Spanish satellite resources must ship with the UI assembly");

        var values = set!.Cast<DictionaryEntry>().Select(e => (string)e.Value!).ToList();
        values.Should().NotBeEmpty();
        foreach (var word in unaccented)
        {
            values.Should().NotContain(
                v => v.Contains(word, StringComparison.OrdinalIgnoreCase),
                $"'{word}' is missing its accent");
        }
    }
}
