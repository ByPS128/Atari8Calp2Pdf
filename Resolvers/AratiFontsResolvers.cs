using PdfSharp.Fonts;

namespace Atari8Calp2Pdf.Resolvers;

public sealed class AratiFontsResolvers : IFontResolver
{
    public byte[] GetFont(string faceName)
    {
        if (faceName == "AtariFont1")
        {
            // Načti font z resources nebo ze souboru
            string fontPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fonts", "PressStart2P-vaV7.ttf");
            return File.ReadAllBytes(fontPath);
        }

        return null;
    }

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyName);

        // Urči font podle názvu a stylu
        if (familyName.Equals("AtariFont1", StringComparison.OrdinalIgnoreCase))
        {
            return new FontResolverInfo("AtariFont1");
        }

        return PlatformFontResolver.ResolveTypeface(familyName, isBold, isItalic);
    }
}
