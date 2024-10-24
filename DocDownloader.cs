using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using HtmlAgilityPack;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Atari8Calp2Pdf;

public sealed class DocDownloader
{
    private static readonly string[] _acceptableImageExtensions = {".gif", ".jpg", ".png"};

    public async Task<List<string>> GetPublicationsAsync()
    {
        var url = "http://www.atari8.cz/calp/list.php";
        var client = new HttpClient();

        // Načteme stránku jako byte array a použijeme správné kódování
        var responseBytes = await client.GetByteArrayAsync(url);
        var responseString = Encoding.GetEncoding("windows-1250").GetString(responseBytes);

        var doc = new HtmlDocument();
        doc.LoadHtml(responseString);

        var publications = new List<string>();
        var contentDiv = doc.DocumentNode.SelectSingleNode("//div[@id='content']");

        if (contentDiv is not null)
        {
            // Vybereme všechny odkazy v divu 'content' s href začínajícím na 'data/'
            var linkNodes = contentDiv.SelectNodes(".//a[starts-with(@href, 'data/')]");
            if (linkNodes != null)
            {
                foreach (var link in linkNodes)
                {
                    var href = link.GetAttributeValue("href", string.Empty);
                    var fullLink = new Uri(new Uri(url), href).ToString();
                    var publicationFolderName = Path.GetFileName(fullLink.TrimEnd('/'));

                    if (publications.Any(p => p == publicationFolderName) is false)
                    {
                        publications.Add(publicationFolderName);
                    }
                }
            }
        }

        return publications;
    }

    public async Task ProcessPublicationsAsync(List<string> publications, int parallelismThreshold)
    {
        ArgumentNullException.ThrowIfNull(publications);

        var semaphore = new SemaphoreSlim(parallelismThreshold);
        var tasks = new List<Task>();

        foreach (var publication in publications)
        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ProcessPublicationAsync(publication);
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    public async Task ProcessPublicationAsync(string publication)
    {
        ArgumentNullException.ThrowIfNull(publication);

        var client = new HttpClient();

        // Load the publication's index page
        var indexUrl = $"https://atari8.cz/calp/data/{publication}/index.php?c=0";
        byte[] responseBytes;

        try
        {
            responseBytes = await client.GetByteArrayAsync(indexUrl);
        }
        catch
        {
            Console.WriteLine($"Cannot load page {indexUrl}");
            return;
        }

        var pageContent = Encoding.GetEncoding("windows-1250").GetString(responseBytes);
        var doc = new HtmlDocument();
        doc.LoadHtml(pageContent);

        var title = publication;
        var titleNode = doc.DocumentNode.SelectSingleNode("//h1");
        if (titleNode != null)
        {
            title = HtmlEntity.DeEntitize(titleNode.InnerText.Trim());
        }

        var sanitizedTitle = string.Join("-", title.Split(Path.GetInvalidFileNameChars())).Replace("»", "").Trim();

        // Create directory for images
        var imagesDir = Path.GetFullPath(Path.Combine("Downloads", publication));
        Directory.CreateDirectory(imagesDir);
        // Download images or generate text pages
        var imageFiles = await DownloadImagesAsync(publication, imagesDir, title, client);

        if (imageFiles.Count == 0)
        {
            return;
        }

        // Create PDF
        CreatePdfFromImages(imageFiles, Path.Combine("Downloads", $"{sanitizedTitle}.pdf"), publication);
        DeleteFiles(imageFiles);
        Directory.Delete(imagesDir, true);

        Console.WriteLine($"Completed: {title}");
    }

    private void CreatePdfFromImages(IReadOnlyList<string> imageFiles, string pdfPath, string publicationUrl)
    {
        using var document = new PdfDocument();
        // Some archices contails php files, so we need to filter only images.
        var filesToWorkWith = imageFiles.Where(f => _acceptableImageExtensions.Contains(Path.GetExtension(f))).ToList();
        foreach (var imageFile in filesToWorkWith)
        {
            var page = document.AddPage();
            using var xGraphics = XGraphics.FromPdfPage(page);
            var imagePath = imageFile;

            // Convert GIF to PNG if necessary
            if (Path.GetExtension(imageFile).Equals(".gif", StringComparison.OrdinalIgnoreCase))
            {
                imagePath = ConvertGifToPngWithImageSharp(imageFile);
            }

            try
            {
                using var xImage = XImage.FromFile(imagePath);
                // Fit the image to the page
                double xRatio = page.Width / xImage.PixelWidth;
                double yRatio = page.Height / xImage.PixelHeight;
                var ratio = Math.Min(xRatio, yRatio);
                var width = xImage.PixelWidth * ratio;
                var height = xImage.PixelHeight * ratio;
                double x = (page.Width - width) / 2;
                double y = (page.Height - height) / 2;

                xGraphics.DrawImage(xImage, x, y, width, height);

                // Delete temporary PNG if created
                if (imagePath != imageFile && File.Exists(imagePath))
                {
                    File.Delete(imagePath);
                }
            }
            catch (Exception ex)
            {
                Environment.FailFast($"Error adding image {imagePath}: {ex.Message}");
            }
        }

        document.Save(pdfPath);
    }

    private void DeleteFiles(IReadOnlyList<string> filesToDelete)
    {
        // nyní smažeme všechny soubory v proměnné filesToDelete
        foreach (var file in filesToDelete)
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static string ConvertGifToPngWithImageSharp(string gifPath)
    {
        var pngPath = Path.ChangeExtension(gifPath, ".png");

        using (var image = Image.Load(gifPath))
        {
            image.Save(pngPath, new PngEncoder());
        }

        return pngPath;
    }

    private async Task<IReadOnlyList<string>> DownloadImagesAsync(string publication, string imagesDir, string title, HttpClient client)
    {
        // https://atari8.cz/calp/data/pha_91_4/down/pha_91_4.cbz 

        // připravím si stažení archivu
        var publicationUrl = CalculatePublicationUrl(publication);
        var publicationFolderName = imagesDir;
        var cbzArchiveFileName = Path.Combine(publicationFolderName, $"{publication}.cbz");
        var cbzArchiveUrl = $"https://atari8.cz/calp/data/{publication}/down/{publication}.cbz";

        // stáhnu archiv, zapíšu na disk a rozbalím ho
        var cbzResponse = await client.GetAsync(cbzArchiveUrl);
        var archiveContentBytes = await cbzResponse.Content.ReadAsByteArrayAsync();
        await File.WriteAllBytesAsync(cbzArchiveFileName, archiveContentBytes);

        // Vytvořím proměnnou pro seznam souborů
        var imageFiles = new List<string>();

        // Rozbalím archiv tak, aby soubory v kořenu archivu byly v zadaném adresáři
        try
        {
            using (var archive = ZipFile.OpenRead(cbzArchiveFileName))
            {
                // Zjistím, jestli všechny položky mají stejný kořenový adresář
                var rootFiles = archive.Entries
                    //.Where(f => f.FullName.IndexOf('/') < 0)
                    .Where(f => f.CompressedLength > 0)
                    //.Distinct()
                    .ToList();

                foreach (var entry in rootFiles)
                {
                    var destinationFileName = Path.GetFullPath(Path.Combine(imagesDir, Path.GetFileName(entry.FullName)));
                    entry.ExtractToFile(destinationFileName, true);

                    // Přidám soubor do seznamu
                    imageFiles.Add(destinationFileName);
                }
            }
        }
        catch (Exception ex)
        {
            ConsoleWriteLineRed($"Error extracting archive {cbzArchiveFileName}: {ex.Message}");
        }

        // Smažu soubor s archivem
        File.Delete(cbzArchiveFileName);

        SortedDictionary(imageFiles);

        var result = imageFiles.ToImmutableList();
        if (result.Count == 0)
        {
            ConsoleWriteLineRed($"No images found for {title}, cannot create document.\n{cbzArchiveUrl}");
            imageFiles.Add(GenerateImageNoImagesInDocument(title, publicationUrl, imagesDir));
        }

        return result;
    }

    private void ConsoleWriteLineRed(string message)
    {
        var foregroundColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        ConsoleWriteLineRed(message);
        Console.ForegroundColor = foregroundColor;
    }

    private string CalculatePublicationUrl(string publication)
    {
        return $"https://atari8.cz/calp/data/{publication}/";
    }

    private string GenerateImageNoImagesInDocument(string title, string publicationUrl, string imagesDir)
    {
        var width = 595; // A4 width in pixels at 72 DPI
        var height = 842; // A4 height in pixels at 72 DPI
        var outputPath = Path.Combine(imagesDir, "empty-document.png");

        using (var image = new Image<Rgba32>(width, height))
        {
            image.Mutate(ctx =>
            {
                ctx.Fill(Color.White); // Fill background with white color

                var message = $"Publikace '{title}' neobsahuje žádné listy.";
                var urlMessage = $"URL: {publicationUrl}";

                // Načteme fonty
                var f1 = GetFont("Courier New", 24);
                var f2 = GetFont("Courier New", 12);

                // Nastavíme RichTextOptions pro zprávu
                var messageOptions = new RichTextOptions(f1)
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Origin = new PointF(width / 2, height / 2 - 20),
                    WrappingLength = width - 40
                };

                // Vykreslíme zprávu
                ctx.DrawText(messageOptions, message, Color.Black);

                // Nastavíme RichTextOptions pro URL
                var urlOptions = new RichTextOptions(f2)
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Origin = new PointF(width / 2, height / 2 + 20),
                    WrappingLength = width - 40
                };

                // Vykreslíme URL
                ctx.DrawText(urlOptions, urlMessage, Color.Black);
            });

            image.Save(outputPath);

            return outputPath;
        }
    }

    private void SortedDictionary(List<string> imageFiles)
    {
        imageFiles.Sort((x, y) =>
        {
            var xFileName = Path.GetFileNameWithoutExtension(x);
            var yFileName = Path.GetFileNameWithoutExtension(y);

            // Pokud je x 'pg_000a', umístíme ho na první místo
            if (xFileName.Equals("pg_000a", StringComparison.OrdinalIgnoreCase))
            {
                return -1;
            }

            // Pokud je y 'pg_000a', umístíme ho na první místo
            if (yFileName.Equals("pg_000a", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            // Ostatní soubory seřadíme abecedně
            return string.Compare(xFileName, yFileName, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static Font GetFont(string fontFamilyName, float size)
    {
        // Nejprve se pokusíme najít font v systémových fontech
        if (SystemFonts.TryGet(fontFamilyName, out var family))
        {
            return family.CreateFont(size);
        }

        // Pokud font není nalezen, použijeme výchozí font
        return SystemFonts.CreateFont("Arial", size);
    }
}
