namespace Atari8Calp2Pdf.Models;

public class Publication
{
    /// <summary>
    ///  URL k dokumentu, například: http://www.atari8.cz/calp/data/pha_87_4/
    /// </summary>
    public string Url { get; set; } = null!;

    /// <summary>
    ///  Výchozí název dokumentu, je přepsán ze strányk s detailem dokumentu, pokud je tam uvedený.
    /// </summary>
    public string Title { get; set; } = null!;
}