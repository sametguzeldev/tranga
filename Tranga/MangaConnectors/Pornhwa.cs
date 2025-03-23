using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Tranga.Jobs;

namespace Tranga.MangaConnectors;

public class Pornhwa : MangaConnector
{
    private const string BaseUrl = "https://pornhwa.me";

    public Pornhwa(GlobalBase clone) : base(clone, "Shadyhwa", ["en"])
    {
        this.downloadClient = new ChromiumDownloadClient(clone);
    }

    public override Manga[] GetManga(string publicationTitle = "")
    {
        Log($"Searching Publications. Term=\"{publicationTitle}\"");

        // URL encode the search term to properly handle spaces and special characters
        string encodedTitle = Uri.EscapeDataString(publicationTitle.Trim());
        string requestUrl = $"{BaseUrl}/search/{encodedTitle}";

        Log($"Search URL: {requestUrl}");

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

        // Primary container for search results - using exact class string
        var resultsContainer = document.DocumentNode.SelectSingleNode("//div[@class='grid sm:grid-cols-5 grid-cols-3 sm:gap-4 gap-2']");

        // Alternative fallback in case the exact class format changes
        if (resultsContainer == null)
        {
            resultsContainer = document.DocumentNode.SelectSingleNode("//div[contains(@class, 'grid sm:grid-cols-5 grid-cols-3')]");
        }

        if (resultsContainer == null)
        {
            Log("Could not find the main grid container for search results");
            return Array.Empty<Manga>();
        }

        // Find all manga items using the itemListElement attribute
        var mangaItems = resultsContainer.SelectNodes(".//div[@itemprop='itemListElement']");

        foreach (var item in mangaItems)
        {
            // Look for the title container
            var titleContainer = item.SelectSingleNode(".//div[contains(@class, 'h-27 grid gap-2 text-sm')]");

            // Get the link - either from the anchor in the title container or the item itself if it's an anchor
            HtmlNode linkNode = titleContainer.SelectSingleNode(".//a[contains(@href, '/read/')]");

            string url = linkNode.GetAttributeValue("href", "");
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }

            // Handle relative URLs
            if (!url.StartsWith("http"))
            {
                url = url.StartsWith("/") ? $"{BaseUrl}{url}" : $"{BaseUrl}/{url}";
            }

            // Extract title from the link's title attribute or inner text
            string title = linkNode.GetAttributeValue("title", "");

            // If title attribute is empty, try to get it from the link's inner text
            if (string.IsNullOrEmpty(title))
            {
                title = linkNode.InnerText.Trim();
            }

            // Try to clean up the title if it starts with "Comic "
            if (title.StartsWith("Comic ", StringComparison.OrdinalIgnoreCase))
            {
                title = title.Substring(6).Trim();
            }

            if (!string.IsNullOrEmpty(title))
            {
                Log($"Found manga in search results: {title} at URL: {url}");
            }

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
        return GetMangaFromUrl($"{BaseUrl}/read/{publicationId}");
    }

    public override Manga? GetMangaFromUrl(string url)
    {
        RequestResult requestResult = downloadClient.MakeRequest(url, RequestType.MangaInfo);
        if ((int)requestResult.statusCode < 200 || (int)requestResult.statusCode >= 300)
            return null;

        if (requestResult.htmlDocument is null)
            return null;

        Regex idRex = new(@"https?:\/\/pornhwa\.me\/read\/([^\/]+).*");
        string id = idRex.Match(url).Groups[1].Value;
        return ParseSinglePublicationFromHtml(requestResult.htmlDocument, id, url);
    }

    private Manga ParseSinglePublicationFromHtml(HtmlDocument document, string publicationId, string websiteUrl)
    {
        Dictionary<string, string> altTitles = new();
        Dictionary<string, string>? links = null;
        string originalLanguage = "kr"; // Most pornhwa are Korean
        Manga.ReleaseStatusByte releaseStatus = Manga.ReleaseStatusByte.Unreleased;

        // Try multiple selectors for the title
        string sortName = "";
        var titleNode = document.DocumentNode.SelectSingleNode("//div[contains(@class, 'md:pl-4')]//h1") ??
                        document.DocumentNode.SelectSingleNode("//h1[contains(@class, 'font-bold')]") ??
                        document.DocumentNode.SelectSingleNode("//h1");

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

        // Get alternative titles using the exact HTML structure
        var altTitlesNode = document.DocumentNode.SelectSingleNode("//span[contains(@class, 'rounded-bl-2xl')]/b[contains(text(), 'Alternative Title')]/following-sibling::text()") ??
                          document.DocumentNode.SelectSingleNode("//span[contains(@class, 'rounded-bl-2xl')]/b[contains(text(), 'Alternative Title')]/parent::span");

        if (altTitlesNode != null)
        {
            string altText = altTitlesNode.InnerText;
            
            // If we got the parent span, we need to remove the "Alternative Title : " part
            if (altText.Contains("Alternative Title"))
            {
                int colonIndex = altText.IndexOf(':');
                if (colonIndex > 0)
                {
                    altText = altText.Substring(colonIndex + 1).Trim();
                }
            }
            
            if (!string.IsNullOrEmpty(altText))
            {
                altTitles.Add("0", altText.Trim());
            }
        }

        // Get genres/tags
        HashSet<string> tags = new();
        var genresNodes = document.DocumentNode.SelectNodes("//div[contains(text(), 'Genre') or contains(text(), 'Genres')]/following-sibling::div//span") ??
                         document.DocumentNode.SelectNodes("//span[contains(text(), 'Genre') or contains(text(), 'Genres')]/following-sibling::span//a");

        if (genresNodes != null)
        {
            foreach (var genreNode in genresNodes)
            {
                tags.Add(genreNode.InnerText.Trim());
            }
        }

        // Get authors from the exact HTML structure
        List<string> authors = new();
        var authorNode = document.DocumentNode.SelectSingleNode("//span[contains(@class, 'rounded-bl-2xl')]/b[contains(text(), 'Author')]/following-sibling::text()") ??
                        document.DocumentNode.SelectSingleNode("//span[contains(@class, 'rounded-bl-2xl')]/b[contains(text(), 'Author')]/parent::span");

        if (authorNode != null)
        {
            string authorText = authorNode.InnerText;
            
            // If we got the parent span, we need to remove the "Author(s) : " part
            if (authorText.Contains("Author"))
            {
                int colonIndex = authorText.IndexOf(':');
                if (colonIndex > 0)
                {
                    authorText = authorText.Substring(colonIndex + 1).Trim();
                }
            }
            
            // Check if author is a placeholder like "-" or empty
            if (!string.IsNullOrEmpty(authorText) && authorText != "-")
            {
                authors.Add(authorText.Trim());
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

        // Get status from the exact HTML structure
        var statusNode = document.DocumentNode.SelectSingleNode("//span[contains(@class, 'rounded-bl-2xl')]/b[contains(text(), 'Status')]/following-sibling::text()") ??
                       document.DocumentNode.SelectSingleNode("//span[contains(@class, 'rounded-bl-2xl')]/b[contains(text(), 'Status')]/parent::span");

        if (statusNode != null)
        {
            string statusText = statusNode.InnerText;
            
            // If we got the parent span, we need to remove the "Status : " part
            if (statusText.Contains("Status"))
            {
                int colonIndex = statusText.IndexOf(':');
                if (colonIndex > 0)
                {
                    statusText = statusText.Substring(colonIndex + 1).Trim();
                }
            }
            
            statusText = statusText.ToLower().Trim();
            releaseStatus = statusText switch
            {
                "ongoing" or "on-going" => Manga.ReleaseStatusByte.Continuing,
                "completed" => Manga.ReleaseStatusByte.Completed,
                "hiatus" => Manga.ReleaseStatusByte.OnHiatus,
                "cancelled" => Manga.ReleaseStatusByte.Cancelled,
                _ => Manga.ReleaseStatusByte.Unreleased
            };
        }

        // Get poster/cover image URL
        string posterUrl = "";
        var posterNode = document.DocumentNode.SelectSingleNode("//div[@itemprop='image' and @itemtype='https://schema.org/ImageObject']/img") ??
                         document.DocumentNode.SelectSingleNode("//div[@itemprop='image']/img") ??
                         document.DocumentNode.SelectSingleNode("//img[contains(@alt, 'Comic') and contains(@class, 'mx-auto')]") ??
                         document.DocumentNode.SelectSingleNode("//img[contains(@alt, 'Comic')]");

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

        // Get description from the exact HTML structure
        string description = "";
        var descriptionNode = document.DocumentNode.SelectSingleNode("//div[contains(@class, 'border-l-4') and contains(@class, 'border-neutral-400')]//p") ??
                             document.DocumentNode.SelectSingleNode("//div[contains(@class, 'border-l-4') and contains(@class, 'border-teal-400')]//p");

        if (descriptionNode != null)
        {
            description = descriptionNode.InnerText.Trim();
        }

        // Get release year if available
        int? year = null;
        var yearNode = document.DocumentNode.SelectSingleNode("//div[contains(text(), 'Released')]/following-sibling::div") ??
                      document.DocumentNode.SelectSingleNode("//span[contains(text(), 'Released')]/following-sibling::span");

        if (yearNode != null)
        {
            string yearText = yearNode.InnerText.Trim();
            if (int.TryParse(yearText, out int parsedYear))
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
        var chapterNodes = document.DocumentNode.SelectNodes("//ul[@id='chapter-list']/li") ??
                          document.DocumentNode.SelectNodes("//div[@id='chapterlist']/ul/li");

        if (chapterNodes == null)
        {
            // Try a more general selector if specific ones failed
            chapterNodes = document.DocumentNode.SelectNodes("//li//a[contains(@href, '/chapter-')]");
        }

        if (chapterNodes == null)
            return ret;

        Regex chapterRex = new(@"Chapter (\d+(?:\.\d+)?)");

        foreach (var chapterNode in chapterNodes)
        {
            var linkNode = chapterNode.Name == "a" ? chapterNode : chapterNode.SelectSingleNode(".//a");
            if (linkNode == null) continue;

            string url = linkNode.GetAttributeValue("href", "");
            if (string.IsNullOrEmpty(url)) continue;

            // Handle relative URLs
            if (!url.StartsWith("http"))
            {
                url = url.StartsWith("/") ? $"{BaseUrl}{url}" : $"{BaseUrl}/{url}";
            }

            string chapterTitle = "";
            var titleNode = linkNode.SelectSingleNode(".//span[contains(text(), 'Chapter')]") ??
                           linkNode.SelectSingleNode(".//span");

            if (titleNode != null)
            {
                chapterTitle = titleNode.InnerText.Trim();
            }
            else
            {
                chapterTitle = linkNode.InnerText.Trim();
            }

            // Extract chapter number - try first from the chapter title
            Match match = chapterRex.Match(chapterTitle);
            string chapterNumber;

            if (match.Success)
            {
                chapterNumber = match.Groups[1].Value;
            }
            else
            {
                // Try to extract from URL if not found in title
                Regex urlChapterRex = new(@"chapter-(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                Match urlMatch = urlChapterRex.Match(url);
                chapterNumber = urlMatch.Success ? urlMatch.Groups[1].Value : "0";
            }

            // Get chapter ID from URL
            Regex idRex = new(@"https?:\/\/pornhwa\.me\/read\/[^\/]+\/([^\/]+)");
            Match idMatch = idRex.Match(url);
            string id = idMatch.Success ? idMatch.Groups[1].Value : "";

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

        return DownloadChapterImages(imageUrls, chapter, RequestType.MangaImage, BaseUrl, progressToken);
    }

    private string[] ParseImageUrlsFromHtml(HtmlDocument document)
    {
        List<string> ret = new();

        // Look for images in different possible containers
        var imageNodes = document.DocumentNode.SelectNodes("//div[@id='bacaArea']/img") ??
                        document.DocumentNode.SelectNodes("//div[@id='readerarea']/img") ??
                        document.DocumentNode.SelectNodes("//div[contains(@class, 'min-h-screen')]/img[@class='mx-auto']");

        if (imageNodes == null)
        {
            // Try a more general selector as fallback
            imageNodes = document.DocumentNode.SelectNodes("//img[contains(@alt, 'Chapter') and contains(@class, 'mx-auto')]");
        }

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

        return ret.ToArray();
    }
}