using System.Text;

namespace Atari8Calp2Pdf;

internal class Program
{
    private static async Task Main()
    {
        // Registrujeme poskytovatele kódování pro podporu windows-1250
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var downloader = new DocDownloader();
        var publicationLinks = await downloader.GetPublicationsAsync();

        // List<string> publicationLinks =
        // [
        //     "man_tosprt"
        // ];

        // Zpracujeme publikace, dopňující stránky vložíme jako text, nikoli jako obrázek.
        await downloader.ProcessPublicationsAsync(publicationLinks, 5);

        Console.WriteLine("Všechny publikace byly zpracovány.");
    }
}
