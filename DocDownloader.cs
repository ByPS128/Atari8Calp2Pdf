using System.Text;
using System.Text.RegularExpressions;
using Atari8Calp2Pdf.Models;
using HtmlAgilityPack;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Atari8Calp2Pdf;

public sealed class DocDownloader
{
    private const int ParallelismThreshold = 10; // 1 = single thread
    private static readonly XFont _textFont = GetAtariXFont1(24);
    private static readonly XFont _urlFont = GetAtariXFont1(16);
    private static readonly Font _textAtariFont = GetFont("PressStart2P-vaV7", 24);
    private static readonly Font _urlAtariFont = GetFont("PressStart2P-vaV7", 12);
    private static string[] _acceptableImageExtensions = new[] {".gif", ".jpg", ".png"};

    public async Task<List<Publication>> GetPublicationsAsync()
    {
        var url = "http://www.atari8.cz/calp/list.php";
        var client = new HttpClient();

        // Načteme stránku jako byte array a použijeme správné kódování
        var responseBytes = await client.GetByteArrayAsync(url);
        var responseString = Encoding.GetEncoding("windows-1250").GetString(responseBytes);

        var doc = new HtmlDocument();
        doc.LoadHtml(responseString);

        var publications = new List<Publication>();
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
                    var title = HtmlEntity.DeEntitize(link.InnerText).Trim();
                    var fullLink = new Uri(new Uri(url), href).ToString();

                    if (!publications.Any(p => p.Url == fullLink))
                    {
                        publications.Add(new Publication
                        {
                            Url = fullLink,
                            Title = title
                        });
                    }
                }
            }
        }

        return publications;
    }

    public async Task ProcessPublicationsAsync(List<Publication> publications, bool includeFinalPage = true, bool renderMissingPagesAsImages = true)
    {
        ArgumentNullException.ThrowIfNull(publications);

        var semaphore = new SemaphoreSlim(ParallelismThreshold);
        var tasks = new List<Task>();

        foreach (var publication in publications)
        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ProcessPublicationAsync(publication, includeFinalPage, renderMissingPagesAsImages);
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    public async Task ProcessPublicationAsync(Publication publication, bool includeFinalPage, bool renderMissingPagesAsImages)
    {
        ArgumentNullException.ThrowIfNull(publication);

        var client = new HttpClient();

        // Load the publication's index page
        var indexUrl = publication.Url + "index.php?c=0";
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

        var title = publication.Title;

        var doc = new HtmlDocument();
        doc.LoadHtml(pageContent);

        var titleNode = doc.DocumentNode.SelectSingleNode("//h1");
        if (titleNode != null)
        {
            title = HtmlEntity.DeEntitize(titleNode.InnerText.Trim());
        }

        var sanitizedTitle = string.Join("-", title.Split(Path.GetInvalidFileNameChars()));

        // Create directory for images
        var imagesDir = Path.Combine("Downloads", sanitizedTitle);
        Directory.CreateDirectory(imagesDir);

        // Get total number of pages
        var totalPages = await GetTotalPagesAsync(publication.Url, client);

        // Download images or generate text pages
        var imageFiles = await DownloadImagesAsync(publication.Url, totalPages, imagesDir, client, renderMissingPagesAsImages);

        if (imageFiles.Count == 0)
        {
            Console.WriteLine($"No images found for {title}");
            return;
        }

        // Create PDF
        CreatePdfFromImages(imageFiles, Path.Combine("Downloads", sanitizedTitle + ".pdf"), includeFinalPage, publication.Url);

        Console.WriteLine($"Completed: {title}");
    }

    private void CreatePdfFromImages(List<string> imageFiles, string pdfPath, bool includeFinalPage, string publicationUrl)
    {
        using (var document = new PdfDocument())
        {
            foreach (var imageFile in imageFiles)
            {
                var page = document.AddPage();
                using (var xGraphics = XGraphics.FromPdfPage(page))
                {
                    if (imageFile.StartsWith("text_page_"))
                    {
                        var pageIndex = int.Parse(imageFile.Replace("text_page_", ""));
                        DrawMissingPageText(xGraphics, page, pageIndex, publicationUrl);
                    }
                    else
                    {
                        var imagePath = imageFile;

                        // Convert GIF to PNG if necessary
                        if (Path.GetExtension(imageFile).Equals(".gif", StringComparison.OrdinalIgnoreCase))
                        {
                            imagePath = ConvertGifToPngWithImageSharp(imageFile);
                        }

                        using (var xImage = XImage.FromFile(imagePath))
                        {
                            // Fit the image to the page
                            double xRatio = page.Width / xImage.PixelWidth;
                            double yRatio = page.Height / xImage.PixelHeight;
                            var ratio = Math.Min(xRatio, yRatio);
                            var width = xImage.PixelWidth * ratio;
                            var height = xImage.PixelHeight * ratio;
                            double x = (page.Width - width) / 2;
                            double y = (page.Height - height) / 2;

                            xGraphics.DrawImage(xImage, x, y, width, height);
                        }

                        // Delete temporary PNG if created
                        if (imagePath != imageFile && File.Exists(imagePath))
                        {
                            File.Delete(imagePath);
                        }
                    }
                }
            }

            // Add final page if requested
            if (includeFinalPage)
            {
                var page = document.AddPage();
                using (var xGraphics = XGraphics.FromPdfPage(page))
                {
                    DrawFinalPage(xGraphics, page, publicationUrl);
                }
            }

            document.Save(pdfPath);
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

    private async Task<int> GetTotalPagesAsync(string publicationUrl, HttpClient client)
    {
        var indexUrl = publicationUrl + "index.php?c=1";
        try
        {
            var responseBytes = await client.GetByteArrayAsync(indexUrl);
            var pageContent = Encoding.GetEncoding("windows-1250").GetString(responseBytes);

            var doc = new HtmlDocument();
            doc.LoadHtml(pageContent);

            var counterNode = doc.DocumentNode.SelectSingleNode("//div[@class='counter']");
            if (counterNode != null)
            {
                var counterText = counterNode.InnerText.Trim(); // e.g., "1/26"
                var parts = counterText.Split('/');
                if (parts.Length == 2 && int.TryParse(parts[1], out var totalPages))
                {
                    return totalPages;
                }
            }
        }
        catch
        {
            Console.WriteLine($"Cannot get total pages for {publicationUrl}");
        }

        return 0;
    }

    private async Task<List<string>> DownloadImagesAsync(string publicationUrl, int totalPages, string imagesDir, HttpClient client, bool renderMissingPagesAsImages)
    {
        var imageFiles = new List<string>();
        Directory.CreateDirectory(imagesDir);

        var imagesThatNBotFound = 0;
        var lastNotFoundImageIndex = -1;

        var baseImageUrl = publicationUrl.TrimEnd('/') + "/img/";

        // Download the first page (if it exists)
        foreach (var ext in _acceptableImageExtensions)
        {
            var firstImageUrl = baseImageUrl + "pg_000a" + ext;
            var response = await client.GetAsync(firstImageUrl);
            if (response.IsSuccessStatusCode)
            {
                var localPath = Path.Combine(imagesDir, "pg_000a" + ext);
                var imageBytes = await response.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(localPath, imageBytes);
                imageFiles.Add(localPath);
                break;
            }
        }

        // Download other pages
        var totalImagesIsUnknownm = totalPages == 0;
        totalPages = totalPages == 0 ? 999 : totalPages;
        for (var pageIndex = 1; pageIndex <= totalPages; pageIndex++)
        {
            var imageFound = false;
            foreach (var ext in _acceptableImageExtensions)
            {
                var pageNumber = pageIndex.ToString("D3");
                var imageUrl = baseImageUrl + "pg_" + pageNumber + ext;
                var response = await client.GetAsync(imageUrl);

                if (response.IsSuccessStatusCode)
                {
                    var localPath = Path.Combine(imagesDir, "pg_" + pageNumber + ext);
                    var imageBytes = await response.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(localPath, imageBytes);
                    imageFiles.Add(localPath);
                    imageFound = true;
                    break;
                }
            }

            if (imageFound is false)
            {
                if (totalImagesIsUnknownm)
                {
                    imagesThatNBotFound++;
                    // Pokud nenajdu obrázek 2x zas sebou a původně jsem nevěděl kolik obrázků mám tahat, tak končím.
                    if (imagesThatNBotFound > 1 && lastNotFoundImageIndex == pageIndex - 1)
                    {
                        //totalPages = pageIndex - 1;
                        break;
                    }

                    lastNotFoundImageIndex = pageIndex;
                }

                if (renderMissingPagesAsImages)
                {
                    // Create custom image for missing page
                    var localPath = Path.Combine(imagesDir, $"missing_pg_{pageIndex.ToString("D3")}.png");
                    CreateMissingPageImage(pageIndex, publicationUrl, localPath);
                    imageFiles.Add(localPath);
                }
                else
                {
                    // Use a placeholder to indicate a text page
                    imageFiles.Add($"text_page_{pageIndex}");
                }
            }
            else
            {
                imagesThatNBotFound = 0;
            }
        }

        // Remove the last image if the total number of images is unknown and the last image is a text page.
        if (totalImagesIsUnknownm && imageFiles[imageFiles.Count-1].StartsWith("text_page_"))
        {
            imageFiles.RemoveAt(imageFiles.Count-1);
        }

        return imageFiles;
    }

    private void CreateMissingPageImage_o(int pageIndex, string publicationUrl, string outputPath)
    {
        var width = 595; // A4 width in pixels at 72 DPI
        var height = 842; // A4 height in pixels at 72 DPI

        using (var image = new Image<Rgba32>(width, height))
        {
            image.Mutate(ctx =>
            {
                ctx.Fill(Color.White); // Fill background with white color

                var message = $"Stránka číslo {pageIndex} nebyla nalezena.";
                var urlMessage = $"URL: {publicationUrl}index.php?c={pageIndex}";

                // Load font
                var fontCollection = new FontCollection();
                // Use a system font
                var font = GetAtariXFont1(24);
                var urlFont = GetAtariXFont1(16);

                var f1 = GetFont("Courier New", 24);
                var f2 = GetFont("Courier New", 12);
                // Set options for drawing text
                var textOptions = new TextOptions(f1)
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Origin = new PointF(width / 2, height / 2 - 20),
                    WrappingLength = width - 40
                };

                // Draw the message
                ctx.DrawText(message, f1, Color.Black, textOptions.Origin);

                // Draw the URL below the message
                textOptions.Font = f2;
                textOptions.Origin = new PointF(width / 2, height / 2 + 20);
                ctx.DrawText(urlMessage, f2, Color.Black, textOptions.Origin);
            });

            image.Save(outputPath);
        }
    }

    private void CreateMissingPageImage(int pageIndex, string publicationUrl, string outputPath)
    {
        var width = 595; // A4 šířka v pixelech při 72 DPI
        var height = 842; // A4 výška v pixelech při 72 DPI

        var message = $"Stránka číslo {pageIndex} nebyla nalezena.";
        var urlMessage = $"URL: {publicationUrl}index.php?c={pageIndex}";

        using (var image = new Image<Rgba32>(width, height))
        {
            image.Mutate(ctx =>
            {
                ctx.Fill(Color.White); // Vyplníme pozadí bílou barvou

                // Nastavíme RichTextOptions pro zprávu
                var textOptions = new RichTextOptions(_textAtariFont)
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Origin = new PointF(width / 2, height / 2 - 20),
                    WrappingLength = width - 40
                };

                // Vykreslíme zprávu
                ctx.DrawText(textOptions, message, Color.Black);

                // Nastavíme RichTextOptions pro URL
                var urlOptions = new RichTextOptions(_urlAtariFont)
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
        }
    }

    private void DrawMissingPageText(XGraphics gfx, PdfPage page, int pageIndex, string publicationUrl)
    {
        var message = $"Stránka číslo {pageIndex} nebyla nalezena.";
        var url = $"{publicationUrl}index.php?c={pageIndex}";

        // Vycentrujeme text
        var messageSize = gfx.MeasureString(message, _textFont);
        var urlSize = gfx.MeasureString(url, _urlFont);
        var totalHeight = messageSize.Height + urlSize.Height + 20; // 20 pixelů mezera
        double startY = (page.Height - totalHeight) / 2;

        // Vykreslíme zprávu
        double x = (page.Width - messageSize.Width) / 2;
        gfx.DrawString(message, _textFont, XBrushes.Black, new XPoint(x, startY));

        // Vykreslíme URL pod zprávu
        double urlX = (page.Width - urlSize.Width) / 2;
        var urlY = startY + messageSize.Height + 20;
        gfx.DrawString(url, _urlFont, XBrushes.Blue, new XPoint(urlX, urlY));

        // Vytvoříme klikací oblast pro URL
        var linkAnnotation = new PdfLinkAnnotation(page.Owner)
        {
            Title = url
        };
        page.Annotations.Add(linkAnnotation);
    }

    private void DrawFinalPage(XGraphics gfx, PdfPage page, string publicationUrl)
    {
        var url = publicationUrl;

        // Rozdělíme zprávu na řádky
        string[] lines =
        {
            "Dokument byl sestaven aplikací Atari8CalpToPdf.",
            "Zdroj dokumentu je na stránce:",
            $"{publicationUrl}",
            "",
            "",
            "",
            "",
            "Aplikace byla vyvinuta pomocí AI,",
            "jmenovitě ChatGPT o1-preview,",
            "pod dohledem vývojáře Petra Škalouda aka ByPS.",
            "BIO: https://www.linkedin.com/in/skaloudpetr/",
            "",
            "",
            "",
            "Aplikace je naprogramovaná v .net jazyce C#",
            "GIT repozitory je zde:",
            "https://github.com/ByPS128/Atari8Calp2Pdf"
        };

        // Načteme font
        var font = GetAtariXFont1(16);

        // Připravíme regulární výraz pro nalezení URL
        var urlRegex = new Regex(@"(https?://[^\s]+)", RegexOptions.Compiled);

        // Vypočítáme celkovou výšku textu
        double totalHeight = 0;
        double lineSpacing = 5; // Mezera mezi řádky
        var lineHeights = new List<double>();

        foreach (var line in lines)
        {
            var lineHeight = font.GetHeight();
            lineHeights.Add(lineHeight);
            totalHeight += lineHeight + lineSpacing;
        }

        totalHeight -= lineSpacing; // Odečteme poslední mezeru

        // Vypočítáme počáteční Y pozici pro vertikální vystředění
        double startY = (page.Height - totalHeight) / 2;

        // Vykreslíme každý řádek
        var y = startY + font.Height;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineHeight = lineHeights[i];

            // Najdeme URL v řádku
            List<(string text, bool isUrl)> parts = new ();
            var lastIndex = 0;
            foreach (Match match in urlRegex.Matches(line))
            {
                if (match.Index > lastIndex)
                {
                    // Přidáme text před URL
                    var textBefore = line.Substring(lastIndex, match.Index - lastIndex);
                    parts.Add((textBefore, false));
                }

                // Přidáme URL
                parts.Add((match.Value, true));
                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < line.Length)
            {
                // Přidáme zbytek textu po poslední URL
                parts.Add((line.Substring(lastIndex), false));
            }

            // Vypočítáme celkovou šířku řádku
            double lineWidth = 0;
            var partWidths = new List<double>();
            foreach (var part in parts)
            {
                var size = gfx.MeasureString(part.text, font);
                partWidths.Add(size.Width);
                lineWidth += size.Width;
            }

            // Vypočítáme počáteční X pozici pro horizontální vystředění
            double x = (page.Width - lineWidth) / 2;

            // Vykreslíme jednotlivé části řádku
            var xPos = x;
            for (var j = 0; j < parts.Count; j++)
            {
                var part = parts[j];
                var partWidth = partWidths[j];
                if (part.isUrl)
                {
                    // Vykreslíme URL modře
                    gfx.DrawString(part.text, font, XBrushes.Blue, new XPoint(xPos, y));

                    // Vytvoříme klikací oblast pro URL
                    var urlRect = new XRect(xPos, y - font.Height, partWidth, lineHeight);
                    var link = PdfLinkAnnotation.CreateWebLink(new PdfRectangle(urlRect), url);
                    link.Title = url;
                    page.Annotations.Add(link);
                }
                else
                {
                    // Vykreslíme běžný text
                    gfx.DrawString(part.text, font, XBrushes.Black, new XPoint(xPos, y));
                }

                xPos += partWidth;
            }

            // Posuneme se na další řádek
            y += lineHeight + lineSpacing;
        }
    }

    private static Font GetFont(string fontFamilyName, float size)
    {
        var fontCollection = new FontCollection();
        FontFamily family;

        // Nejprve se pokusíme najít font v systémových fontech
        if (SystemFonts.TryGet(fontFamilyName, out family))
        {
            return family.CreateFont(size);
        }

        // Pokud není nalezen v "systémových" fontech, pokusíme se jej načíst ze složky Fonts v aplikaci
        var fontPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fonts", fontFamilyName + ".ttf");
        if (File.Exists(fontPath))
        {
            family = fontCollection.Add(fontPath);
            return family.CreateFont(size);
        }

        // Pokud font není nalezen, použijeme výchozí font
        return SystemFonts.CreateFont("Arial", size);
    }

    private static XFont GetCourierXFont(double size, XFontStyleEx style = XFontStyleEx.Regular)
    {
        var fontFamilyName = "Courier New";
        var font = new XFont(fontFamilyName, size, style, new XPdfFontOptions(PdfFontEncoding.Unicode, PdfFontEmbedding.EmbedCompleteFontFile));
        return font;
    }

    private static XFont GetAtariXFont1(double size, XFontStyleEx style = XFontStyleEx.Regular)
    {
        var font = new XFont("AtariFont1", size / 2, style, new XPdfFontOptions(PdfFontEncoding.Unicode, PdfFontEmbedding.EmbedCompleteFontFile));
        return font;
    }
}
