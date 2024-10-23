using System.Text;
using Atari8Calp2Pdf.Models;
using Atari8Calp2Pdf.Resolvers;
using PdfSharp.Fonts;

namespace Atari8Calp2Pdf;

internal class Program
{
    private static async Task Main()
    {
        // Registrujeme poskytovatele kódování pro podporu windows-1250
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        GlobalFontSettings.FontResolver = new AratiFontsResolvers();

        var downloader = new DocDownloader();
        List<Publication> publicationLinks = await downloader.GetPublicationsAsync();

        // // Testovací omezení na konkrétní dokument. V tomto například chybí 2. stránka.
        // publicationLinks.Clear();
        // publicationLinks.Add(new Publication {Title = "T2000", Url = "http://www.atari8.cz/calp/data/pha_pr_2/"});

        // // Testovací omezení na konkrétní dokument. V tomto například se špatně zobrzuje hlavní stránka a nejde zjistit celkový počet stránek.
        // // URL:
        // // vrací: 'man_qmeg', 'imgdir' => 'img/', 'fc' => 'pg_000a.gif', 'covercount' => 1, 'pages' => 19, 'ext' => '.gif', ); pagecontentnew($book); pagefoot(); ?>
        // publicationLinks.Clear();
        // publicationLinks.Add(new Publication {Title = "MULE", Url = "http://www.atari8.cz/calp/data/man_mule/"});

        // Zpracujeme publikace s výchozími parametry
        await downloader.ProcessPublicationsAsync(publicationLinks, includeFinalPage: true, renderMissingPagesAsImages: false);

        Console.WriteLine("Všechny publikace byly zpracovány.");
    }
}
