using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Tranga.Jobs;

namespace Tranga.MangaConnectors;

public class Hentai20 : MangaConnector
{
    private const string BaseUrl = "https://hentai20.io";

    public Hentai20(GlobalBase clone) : base(clone, "Hentai20", ["en", "kr", "jp"])
    {
        this.downloadClient = new ChromiumDownloadClient(clone);
    }

    public override Manga[] GetManga(string publicationTitle = "")
    {
        Log($"Searching Publications. Term=\"{publicationTitle}\"");

        // URL encode the search term to properly handle spaces and special characters
        string encodedTitle = Uri.EscapeDataString(publicationTitle.Trim());
        string requestUrl = $"{BaseUrl}/?s={encodedTitle}";

        RequestResult requestResult = downloadClient.MakeRequest(requestUrl, RequestType.Default);
        if ((int)requestResult.statusCode < 200 || (int)requestResult.statusCode >= 300)
            return Array.Empty<Manga>();

        if (requestResult.htmlDocument is null)
            return Array.Empty<Manga>();

        Manga[] publications = ParsePublicationsFromHtml(requestResult.htmlDocument);
        Log($"Retrieved {publications.Length} publications. Term=\"{publicationTitle}\"");
        return publications;
    }

    private Manga[] ParsePublicationsFromHtml(HtmlDocument document)
    {
        List<Manga> results = new();

        // Look for manga entries in search results
        var mangaItems = document.DocumentNode.SelectNodes("//div[contains(@class, 'bs')]/div[contains(@class, 'bsx')]") ??
                         document.DocumentNode.SelectNodes("//div[@class='listupd']/div");

        if (mangaItems == null)
        {
            Log("Could not find manga items in search results");
            return Array.Empty<Manga>();
        }

        foreach (var item in mangaItems)
        {
            var linkNode = item.SelectSingleNode(".//a");
            if (linkNode == null) continue;

            string url = linkNode.GetAttributeValue("href", "");
            if (string.IsNullOrEmpty(url)) continue;

            // Get the title from the title attribute or alt attribute of the image
            string title = linkNode.GetAttributeValue("title", "");
            if (string.IsNullOrEmpty(title))
            {
                var imgNode = linkNode.SelectSingleNode(".//img");
                if (imgNode != null)
                {
                    title = imgNode.GetAttributeValue("alt", "");
                }
            }

            if (string.IsNullOrEmpty(title))
            {
                title = linkNode.InnerText.Trim();
            }

            Log($"Found manga in search results: {title} at URL: {url}");

            Manga? manga = GetMangaFromUrl(url);
            if (manga != null)
            {
                results.Add(manga.Value);
            }
        }

        return results.ToArray();
    }

    public override Manga? GetMangaFromId(string publicationId)
    {
        return GetMangaFromUrl($"{BaseUrl}/manga/{publicationId}");
    }

    public override Manga? GetMangaFromUrl(string url)
    {
        RequestResult requestResult = downloadClient.MakeRequest(url, RequestType.MangaInfo);
        if ((int)requestResult.statusCode < 200 || (int)requestResult.statusCode >= 300)
            return null;

        if (requestResult.htmlDocument is null)
            return null;

        Regex idRex = new(@"https?:\/\/hentai20\.io\/manga\/([^\/]+).*");
        string id = idRex.Match(url).Groups[1].Value;
        
        // If the ID is empty, try to extract it from the manga URL structure (which might not include /manga/)
        if (string.IsNullOrEmpty(id))
        {
            idRex = new(@"https?:\/\/hentai20\.io\/([^\/]+)(?:\/|$).*");
            id = idRex.Match(url).Groups[1].Value;
            
            // Skip common non-manga paths
            if (id == "?" || id == "#" || id.StartsWith("page") || id == "")
            {
                return null;
            }
        }
        
        return ParseSinglePublicationFromHtml(requestResult.htmlDocument, id, url);
    }

    private Manga ParseSinglePublicationFromHtml(HtmlDocument document, string publicationId, string websiteUrl)
    {
        Dictionary<string, string> altTitles = new();
        Dictionary<string, string>? links = null;
        string originalLanguage = "kr"; // Most manhwa are Korean by default
        Manga.ReleaseStatusByte releaseStatus = Manga.ReleaseStatusByte.Unreleased;

        // Get the title from the h1 element
        string sortName = "";
        var titleNode = document.DocumentNode.SelectSingleNode("//div[@class='seriestuheader']/h1[@class='entry-title']") ??
                       document.DocumentNode.SelectSingleNode("//h1[@class='entry-title']");

        if (titleNode != null)
        {
            sortName = titleNode.InnerText.Trim();
        }
        else
        {
            // If we can't find a title, use the publication ID as fallback
            sortName = publicationId.Replace("-", " ");
            // Capitalize first letter of each word
            sortName = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(sortName);
        }

        // Get genres/tags
        HashSet<string> tags = new();
        var genresNodes = document.DocumentNode.SelectNodes("//div[@class='seriestugenre']//a") ??
                         document.DocumentNode.SelectNodes("//span[@class='mgen']/a");

        if (genresNodes != null)
        {
            foreach (var genreNode in genresNodes)
            {
                tags.Add(genreNode.InnerText.Trim());
            }
        }

        // Get authors - look in the infotable
        List<string> authors = new();
        var authorRow = document.DocumentNode.SelectSingleNode("//table[@class='infotable']//tr[td[text()='Posted By' or text()='Author']]/td[2]") ??
                       document.DocumentNode.SelectSingleNode("//span[@itemprop='author']");

        if (authorRow != null)
        {
            string authorText = authorRow.InnerText.Trim();
            
            // Check if author is a placeholder or empty
            if (!string.IsNullOrEmpty(authorText))
            {
                authors.Add(authorText);
            }
            else
            {
                authors.Add("Unknown");
            }
        }
        else
        {
            authors.Add("Unknown");
        }

        // Get status from the infotable
        var statusNode = document.DocumentNode.SelectSingleNode("//table[@class='infotable']//tr[td[text()='Status']]/td[2]");

        if (statusNode != null)
        {
            string statusText = statusNode.InnerText.Trim().ToLower();
            releaseStatus = statusText switch
            {
                "ongoing" => Manga.ReleaseStatusByte.Continuing,
                "completed" => Manga.ReleaseStatusByte.Completed,
                "hiatus" => Manga.ReleaseStatusByte.OnHiatus,
                "cancelled" or "dropped" => Manga.ReleaseStatusByte.Cancelled,
                _ => Manga.ReleaseStatusByte.Unreleased
            };
        }

        // Get poster/cover image URL
        string posterUrl = "";
        var posterNode = document.DocumentNode.SelectSingleNode("//div[@class='thumb' and @itemprop='image']/img") ??
                         document.DocumentNode.SelectSingleNode("//img[@class='wp-post-image']");

        if (posterNode != null)
        {
            posterUrl = posterNode.GetAttributeValue("src", "");

            // Handle relative URLs
            if (!posterUrl.StartsWith("http"))
            {
                posterUrl = posterUrl.StartsWith("/") ? $"{BaseUrl}{posterUrl}" : $"{BaseUrl}/{posterUrl}";
            }

            Log($"Found cover image URL: {posterUrl}");
        }
        else
        {
            Log("Could not find cover image");
        }

        // Save cover image to cache
        string coverFileNameInCache = SaveCoverImageToCache(posterUrl, publicationId.Replace('/', '-'), RequestType.MangaCover, BaseUrl);

        // Get description
        string description = "";
        var descriptionNode = document.DocumentNode.SelectSingleNode("//div[@class='entry-content entry-content-single' and @itemprop='description']") ??
                             document.DocumentNode.SelectSingleNode("//div[@itemprop='description']");

        if (descriptionNode != null)
        {
            description = descriptionNode.InnerText.Trim();
        }

        // Get release year if available
        int? year = null;
        var postedDateNode = document.DocumentNode.SelectSingleNode("//table[@class='infotable']//tr[td[text()='Posted On']]/td[2]/time");

        if (postedDateNode != null)
        {
            string dateText = postedDateNode.InnerText.Trim();
            // Try to extract year from the date (e.g., "January 5, 2025")
            Regex yearRegex = new(@"(\d{4})");
            Match yearMatch = yearRegex.Match(dateText);
            if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out int parsedYear))
            {
                year = parsedYear;
            }
        }

        // Create manga object
        Manga manga = new(sortName, authors.ToList(), description, altTitles, tags.ToArray(), posterUrl, coverFileNameInCache, links,
            year, originalLanguage, publicationId, releaseStatus, websiteUrl: websiteUrl);

        AddMangaToCache(manga);
        return manga;
    }

    public override Chapter[] GetChapters(Manga manga, string language = "en")
    {
        Log($"Getting chapters {manga}");
        string requestUrl = manga.websiteUrl;
        RequestResult requestResult = downloadClient.MakeRequest(requestUrl, RequestType.Default);
        if ((int)requestResult.statusCode < 200 || (int)requestResult.statusCode >= 300)
            return Array.Empty<Chapter>();

        if (requestResult.htmlDocument is null)
            return Array.Empty<Chapter>();

        List<Chapter> chapters = ParseChaptersFromHtml(manga, requestResult.htmlDocument);
        Log($"Got {chapters.Count} chapters. {manga}");
        return chapters.OrderBy(c => c.chapterNumber).ToArray();
    }

    private List<Chapter> ParseChaptersFromHtml(Manga manga, HtmlDocument document)
    {
        List<Chapter> ret = new();

        // Find chapter list
        var chapterNodes = document.DocumentNode.SelectNodes("//div[@id='chapterlist']/ul[@class='clstyle']/li");
        
        if (chapterNodes == null)
        {
            Log("Could not find chapter nodes");
            return ret;
        }

        foreach (var chapterNode in chapterNodes)
        {
            var linkNode = chapterNode.SelectSingleNode(".//div[@class='eph-num']/a");
            if (linkNode == null) continue;

            string url = linkNode.GetAttributeValue("href", "");
            if (string.IsNullOrEmpty(url)) continue;

            string chapterTitle = "";
            var titleNode = linkNode.SelectSingleNode(".//span[@class='chapternum']");

            if (titleNode != null)
            {
                chapterTitle = titleNode.InnerText.Trim();
            }
            else
            {
                chapterTitle = linkNode.InnerText.Trim();
            }

            // Extract chapter number from the title
            Regex chapterRex = new(@"Chapter (\d+(?:\.\d+)?)");
            Match match = chapterRex.Match(chapterTitle);
            
            // Also try to get it from the li's data-num attribute
            string chapterNumber = "0";
            if (match.Success)
            {
                chapterNumber = match.Groups[1].Value;
            }
            else 
            {
                string dataNum = chapterNode.GetAttributeValue("data-num", "");
                if (!string.IsNullOrEmpty(dataNum))
                {
                    chapterNumber = dataNum;
                }
                else
                {
                    // Try to extract from URL
                    Regex urlChapterRex = new(@"chapter-(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                    Match urlMatch = urlChapterRex.Match(url);
                    chapterNumber = urlMatch.Success ? urlMatch.Groups[1].Value : "0";
                }
            }

            // Get chapter ID from URL
            Regex idRex = new(@"https?:\/\/hentai20\.io\/([^\/]+)-chapter-(\d+(?:\.\d+)?)");
            Match idMatch = idRex.Match(url);
            string id = idMatch.Success ? $"{idMatch.Groups[1].Value}-chapter-{idMatch.Groups[2].Value}" : "";

            if (string.IsNullOrEmpty(id))
            {
                // Alternative approach: just use the last part of the URL
                string[] urlParts = url.TrimEnd('/').Split('/');
                id = urlParts[^1]; // Get the last part
            }

            try
            {
                ret.Add(new Chapter(manga, null, null, chapterNumber, url, id));
            }
            catch (Exception e)
            {
                Log($"Failed to load chapter {chapterNumber}: {e.Message}");
            }
        }

        return ret;
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
        if (imageUrls.Length == 0)
        {
            Log("No images found in chapter");
            progressToken?.Cancel();
            return HttpStatusCode.NotFound;
        }

        return DownloadChapterImages(imageUrls, chapter, RequestType.MangaImage, BaseUrl, progressToken);
    }

    private string[] ParseImageUrlsFromHtml(HtmlDocument document)
    {
        List<string> ret = new();

        // Look for images in the reader area
        var imageNodes = document.DocumentNode.SelectNodes("//div[@id='readerarea']/img");

        if (imageNodes != null)
        {
            foreach (var imageNode in imageNodes)
            {
                string imageUrl = imageNode.GetAttributeValue("src", "");
                if (!string.IsNullOrEmpty(imageUrl))
                {
                    // Handle relative URLs
                    if (!imageUrl.StartsWith("http"))
                    {
                        imageUrl = imageUrl.StartsWith("/") ? $"{BaseUrl}{imageUrl}" : $"{BaseUrl}/{imageUrl}";
                    }

                    ret.Add(imageUrl);
                }
            }
        }
        else
        {
            Log("No images found in reader area");
        }

        return ret.ToArray();
    }
} 