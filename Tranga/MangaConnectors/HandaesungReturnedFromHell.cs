using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Tranga.Jobs;

namespace Tranga.MangaConnectors;

public class HandaesungReturnedFromHell : MangaConnector
{
    public HandaesungReturnedFromHell(GlobalBase clone) : base(clone, "HandaesungReturnedFromHell", ["en"])
    {
        this.downloadClient = new HttpDownloadClient(clone);
    }

    public override Manga[] GetManga(string publicationTitle = "")
    {
        Log($"Searching Publications. Term=\"{publicationTitle}\"");
        
        // Since this site only has one manga, return it directly
        Manga? manga = GetMangaFromUrl("https://handaesungreturnedfromhell.com/");
        if (manga != null)
        {
            Log($"Retrieved 1 publication. Term=\"{publicationTitle}\"");
            return new Manga[] { (Manga)manga };
        }
        
        return Array.Empty<Manga>();
    }

    public override Manga? GetMangaFromId(string publicationId)
    {
        return GetMangaFromUrl("https://handaesungreturnedfromhell.com/");
    }

    public override Manga? GetMangaFromUrl(string url)
    {
        RequestResult requestResult = downloadClient.MakeRequest(url, RequestType.MangaInfo);
        if ((int)requestResult.statusCode < 200 || (int)requestResult.statusCode >= 300)
            return null;
        
        if (requestResult.htmlDocument is null)
            return null;
            
        return ParseSinglePublicationFromHtml(requestResult.htmlDocument, "handaesung-returned-from-hell", url);
    }

    private Manga ParseSinglePublicationFromHtml(HtmlDocument document, string publicationId, string websiteUrl)
    {
        Dictionary<string, string> altTitles = new();
        Dictionary<string, string>? links = null;
        HashSet<string> tags = new();
        List<string> authors = new();
        string originalLanguage = "ko";
        Manga.ReleaseStatusByte releaseStatus = Manga.ReleaseStatusByte.Continuing;

        // Extract title
        string sortName = "Han Dae Sung Returned From Hell";
        HtmlNode? titleNode = document.DocumentNode.SelectSingleNode("//h1");
        if (titleNode != null)
            sortName = titleNode.InnerText.Trim();

        // Extract authors - look for author information in meta or structured data
        HtmlNode? authorNode = document.DocumentNode.SelectSingleNode("//meta[@name='author']");
        if (authorNode != null)
        {
            authors.Add(authorNode.GetAttributeValue("content", ""));
        }
        else
        {
            // Fallback to known authors
            authors.AddRange(["Brown Panda", "Grujam"]);
        }

        // Extract description
        string description = "Han Dae Sung Returned From Hell";
        HtmlNode? descNode = document.DocumentNode.SelectSingleNode("//meta[@name='description']");
        if (descNode != null)
            description = descNode.GetAttributeValue("content", description);

        // Extract cover image
        string posterUrl = "";
        HtmlNode? imgNode = document.DocumentNode.SelectSingleNode("//img[@class='manga-thumb']");
        if (imgNode != null)
            posterUrl = imgNode.GetAttributeValue("src", "");

        // If no poster found, try other image selectors
        if (string.IsNullOrEmpty(posterUrl))
        {
            imgNode = document.DocumentNode.SelectSingleNode("//img[contains(@src, 'cover') or contains(@src, 'thumb')]");
            if (imgNode != null)
                posterUrl = imgNode.GetAttributeValue("src", "");
        }

        string coverFileNameInCache = "";
        if (!string.IsNullOrEmpty(posterUrl))
        {
            if (!posterUrl.StartsWith("http"))
                posterUrl = "https://handaesungreturnedfromhell.com" + posterUrl;
            coverFileNameInCache = SaveCoverImageToCache(posterUrl, publicationId, RequestType.MangaCover);
        }

        // Set tags
        tags.Add("Manhwa");
        tags.Add("Action");
        tags.Add("Fantasy");

        // Set year (2023 based on website info)
        int year = 2023;

        Manga manga = new(sortName, authors, description, altTitles, tags.ToArray(), posterUrl, coverFileNameInCache, links,
            year, originalLanguage, publicationId, releaseStatus, websiteUrl: websiteUrl);
        AddMangaToCache(manga);
        return manga;
    }

    public override Chapter[] GetChapters(Manga manga, string language = "en")
    {
        Log($"Getting chapters {manga}");
        string requestUrl = "https://handaesungreturnedfromhell.com/";
        RequestResult requestResult = downloadClient.MakeRequest(requestUrl, RequestType.Default);
        if ((int)requestResult.statusCode < 200 || (int)requestResult.statusCode >= 300)
            return Array.Empty<Chapter>();

        if (requestResult.htmlDocument is null)
            return Array.Empty<Chapter>();
            
        List<Chapter> chapters = ParseChaptersFromHtml(manga, requestResult.htmlDocument);
        Log($"Got {chapters.Count} chapters. {manga}");
        return chapters.Order().ToArray();
    }

    private List<Chapter> ParseChaptersFromHtml(Manga manga, HtmlDocument document)
    {
        List<Chapter> ret = new();

        // Look for chapter links
        var chapterNodes = document.DocumentNode.SelectNodes("//a[contains(@href, '/comic/han-dae-sung-returned-from-hell-chapter-')]");
        if (chapterNodes == null)
        {
            // Try alternative selector for chapter links
            chapterNodes = document.DocumentNode.SelectNodes("//a[contains(@href, '/comic/') and contains(@href, 'chapter')]");
        }

        if (chapterNodes != null)
        {
            foreach (HtmlNode chapterNode in chapterNodes)
            {
                string url = chapterNode.GetAttributeValue("href", "");
                if (string.IsNullOrEmpty(url))
                    continue;

                if (!url.StartsWith("http"))
                    url = "https://handaesungreturnedfromhell.com" + url;

                // Extract chapter number from URL
                Regex chapterRex = new(@"chapter-([0-9]+(?:\.[0-9]+)?)");
                Match match = chapterRex.Match(url);
                if (!match.Success)
                    continue;

                string chapterNumber = match.Groups[1].Value;
                string chapterName = chapterNode.InnerText.Trim();
                
                // Clean up chapter name
                if (string.IsNullOrEmpty(chapterName))
                    chapterName = $"Chapter {chapterNumber}";

                try
                {
                    ret.Add(new Chapter(manga, chapterName, "0", chapterNumber, url));
                }
                catch (Exception e)
                {
                    Log($"Failed to load chapter {chapterNumber}: {e.Message}");
                }
            }
        }

        // Remove duplicates and sort
        return ret.Distinct().ToList();
    }

    public override HttpStatusCode DownloadChapter(Chapter chapter, ProgressToken? progressToken = null)
    {
        if (progressToken?.cancellationRequested ?? false)
        {
            progressToken.Cancel();
            return HttpStatusCode.RequestTimeout;
        }

        Manga chapterParentManga = chapter.parentManga;
        Log($"Retrieving chapter-info {chapter} {chapterParentManga}");
        string requestUrl = chapter.url;
        RequestResult requestResult = downloadClient.MakeRequest(requestUrl, RequestType.Default);
        if ((int)requestResult.statusCode < 200 || (int)requestResult.statusCode >= 300)
        {
            progressToken?.Cancel();
            return requestResult.statusCode;
        }

        if (requestResult.htmlDocument is null)
        {
            progressToken?.Cancel();
            return HttpStatusCode.InternalServerError;
        }

        string[] imageUrls = ParseImageUrlsFromHtml(requestResult.htmlDocument);
        
        return DownloadChapterImages(imageUrls, chapter, RequestType.MangaImage, "https://handaesungreturnedfromhell.com", progressToken: progressToken);
    }

    private string[] ParseImageUrlsFromHtml(HtmlDocument document)
    {
        List<string> ret = new();

        // Look for all images in the chapter
        var imageNodes = document.DocumentNode.SelectNodes("//img[contains(@src, 'heroco.us/images')]");
        if (imageNodes != null)
        {
            foreach (HtmlNode imageNode in imageNodes)
            {
                string imageUrl = imageNode.GetAttributeValue("src", "");
                if (!string.IsNullOrEmpty(imageUrl) && imageUrl.Contains("heroco.us/images"))
                {
                    ret.Add(imageUrl);
                }
            }
        }

        // Fallback: look for any images that might be chapter content
        if (ret.Count == 0)
        {
            imageNodes = document.DocumentNode.SelectNodes("//img[@src]");
            if (imageNodes != null)
            {
                foreach (HtmlNode imageNode in imageNodes)
                {
                    string imageUrl = imageNode.GetAttributeValue("src", "");
                    if (!string.IsNullOrEmpty(imageUrl) && 
                        (imageUrl.Contains(".jpg") || imageUrl.Contains(".png") || imageUrl.Contains(".webp")) &&
                        !imageUrl.Contains("logo") && !imageUrl.Contains("icon") && !imageUrl.Contains("thumb"))
                    {
                        if (!imageUrl.StartsWith("http"))
                            imageUrl = "https://handaesungreturnedfromhell.com" + imageUrl;
                        ret.Add(imageUrl);
                    }
                }
            }
        }

        return ret.ToArray();
    }
}