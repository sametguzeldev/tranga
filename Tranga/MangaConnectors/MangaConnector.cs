using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Tranga.Jobs;
using static System.IO.UnixFileMode;

namespace Tranga.MangaConnectors;

/// <summary>
/// Base-Class for all Connectors
/// Provides some methods to be used by all Connectors, as well as a DownloadClient
/// </summary>
public abstract class MangaConnector : GlobalBase
{
    internal DownloadClient downloadClient { get; init; } = null!;
    public string[] SupportedLanguages;

    protected MangaConnector(GlobalBase clone, string name, string[] supportedLanguages) : base(clone)
    {
        this.name = name;
        this.SupportedLanguages = supportedLanguages;
        Directory.CreateDirectory(TrangaSettings.coverImageCache);
    }
    
    public string name { get; } //Name of the Connector (e.g. Website)

    /// <summary>
    /// Returns all Publications with the given string.
    /// If the string is empty or null, returns all Publication of the Connector
    /// </summary>
    /// <param name="publicationTitle">Search-Query</param>
    /// <returns>Publications matching the query</returns>
    public abstract Manga[] GetManga(string publicationTitle = "");

    public abstract Manga? GetMangaFromUrl(string url);

    public abstract Manga? GetMangaFromId(string publicationId);
    
    /// <summary>
    /// Returns all Chapters of the publication in the provided language.
    /// If the language is empty or null, returns all Chapters in all Languages.
    /// </summary>
    /// <param name="manga">Publication to get Chapters for</param>
    /// <param name="language">Language of the Chapters</param>
    /// <returns>Array of Chapters matching Publication and Language</returns>
    public abstract Chapter[] GetChapters(Manga manga, string language="en");

    /// <summary>
    /// Updates the available Chapters of a Publication
    /// </summary>
    /// <param name="manga">Publication to check</param>
    /// <param name="language">Language to receive chapters for</param>
    /// <returns>List of Chapters that were previously not in collection</returns>
    public Chapter[] GetNewChapters(Manga manga, string language = "en")
    {
        Log($"Getting new Chapters for {manga}");
        Chapter[] allChapters = this.GetChapters(manga, language);
        if (allChapters.Length < 1)
            return Array.Empty<Chapter>();
        
        Log($"Checking for duplicates {manga}");
        
        // Create a list to store chapters that are truly new
        List<Chapter> newChaptersList = new List<Chapter>();
        
        // Check each chapter one by one to be thorough
        foreach (var chapter in allChapters)
        {
            // Skip chapters that are below ignore threshold
            if (chapter.chapterNumber < manga.ignoreChaptersBelow)
                continue;
                
            // Do a thorough check if the chapter is already downloaded
            bool isAlreadyDownloaded = chapter.CheckChapterIsDownloaded();
            
            if (!isAlreadyDownloaded)
            {
                // Chapter is not downloaded, add it to the list
                newChaptersList.Add(chapter);
            }
        }
        
        Log($"{newChaptersList.Count} new chapters. {manga}");
        try
        {
            Chapter latestChapterAvailable =
                allChapters.Max();
            manga.latestChapterAvailable =
                Convert.ToSingle(latestChapterAvailable.chapterNumber, numberFormatDecimalPoint);
        }
        catch (Exception e)
        {
            Log(e.ToString());
            Log($"Failed getting new Chapters for {manga}");
        }
        
        return newChaptersList.ToArray();
    }
    
    public abstract HttpStatusCode DownloadChapter(Chapter chapter, ProgressToken? progressToken = null);

    /// <summary>
    /// Copies the already downloaded cover from cache to downloadLocation
    /// </summary>
    /// <param name="manga">Publication to retrieve Cover for</param>
    /// <param name="retries">Number of times to retry to copy the cover (or download it first)</param>
    public void CopyCoverFromCacheToDownloadLocation(Manga manga, int? retries = 1)
    {
        Log($"Copy cover {manga}");
        //Check if Publication already has a Folder and cover
        string publicationFolder = manga.CreatePublicationFolder(TrangaSettings.downloadLocation);
        DirectoryInfo dirInfo = new (publicationFolder);
        if (dirInfo.EnumerateFiles().Any(info => info.Name.Contains("cover", StringComparison.InvariantCultureIgnoreCase)))
        {
            Log($"Cover exists {manga}");
            return;
        }

        string? fileInCache = manga.coverFileNameInCache;
        if (fileInCache is null || !File.Exists(fileInCache))
        {
            Log($"Cloning cover failed: File missing {fileInCache}.");
            if (retries > 0 && manga.coverUrl is not null)
            {
                Log($"Trying {retries} more times");
                SaveCoverImageToCache(manga.coverUrl, manga.internalId, 0);
                CopyCoverFromCacheToDownloadLocation(manga, --retries);
            }

            return;
        }
        string newFilePath = Path.Join(publicationFolder, $"cover.{Path.GetFileName(fileInCache).Split('.')[^1]}" );
        Log($"Cloning cover {fileInCache} -> {newFilePath}");
        File.Copy(fileInCache, newFilePath, true);
        if(RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            File.SetUnixFileMode(newFilePath, GroupRead | GroupWrite | UserRead | UserWrite);
    }

    /// <summary>
    /// Downloads Image from URL and saves it to the given path(incl. fileName)
    /// </summary>
    /// <param name="imageUrl"></param>
    /// <param name="fullPath"></param>
    /// <param name="requestType">RequestType for Rate-Limit</param>
    /// <param name="referrer">referrer used in html request header</param>
    /// <param name="chapter">Chapter being downloaded (for notifications)</param>
    /// <param name="imageNumber">Image number in the chapter</param>
    private HttpStatusCode DownloadImage(string imageUrl, string fullPath, RequestType requestType, string? referrer = null, Chapter? chapter = null, int imageNumber = 0)
    {
        const int maxRetries = 20;
        const int minValidSize = 1024; // 1KB minimum - most manga images should be larger

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            Log($"Downloading image from {imageUrl} (attempt {attempt}/{maxRetries})");
            RequestResult requestResult = downloadClient.MakeRequest(imageUrl, requestType, referrer);

            if ((int)requestResult.statusCode < 200 || (int)requestResult.statusCode >= 300)
            {
                Log($"Failed to download image: {requestResult.statusCode} (attempt {attempt}/{maxRetries})");
                if (attempt == maxRetries)
                    return requestResult.statusCode;
                continue;
            }

            if (requestResult.result == Stream.Null)
            {
                Log($"Image stream is null (attempt {attempt}/{maxRetries})");
                if (attempt == maxRetries)
                    return HttpStatusCode.NotFound;
                continue;
            }

            Log($"Writing image to {fullPath}");
            try
            {
                using (FileStream fs = new (fullPath, FileMode.Create))
                {
                    requestResult.result.CopyTo(fs);
                    fs.Flush();
                }

                FileInfo fileInfo = new FileInfo(fullPath);
                Log($"Image written. Size: {fileInfo.Length} bytes");

                // Validate the downloaded file
                if (fileInfo.Length == 0)
                {
                    Log($"WARNING: Downloaded image is 0 bytes! (attempt {attempt}/{maxRetries})");
                    if (attempt < maxRetries)
                    {
                        File.Delete(fullPath); // Clean up the empty file
                        Thread.Sleep(1000); // Wait 1 second before retry
                        continue;
                    }

                    // Send notification for failed image after all retries
                    if (chapter != null)
                    {
                        string title = "📚 Download Failed";
                        string message = $"Image {imageNumber} failed to download after {maxRetries} attempts\n" +
                                       $"Chapter: {chapter.Value.parentManga.sortName} - {chapter.Value.fileName}\n" +
                                       $"Issue: 0-byte file\n" +
                                       $"URL: {imageUrl}";
                        SendNotifications(title, message);
                    }

                    return HttpStatusCode.NoContent;
                }

                if (fileInfo.Length < minValidSize)
                {
                    Log($"WARNING: Downloaded image is suspiciously small ({fileInfo.Length} bytes) (attempt {attempt}/{maxRetries})");
                    if (attempt < maxRetries)
                    {
                        File.Delete(fullPath);
                        Thread.Sleep(1000);
                        continue;
                    }

                    // Send notification for suspiciously small image after all retries
                    if (chapter != null)
                    {
                        string title = "⚠️ Download Warning";
                        string message = $"Image {imageNumber} is suspiciously small ({fileInfo.Length} bytes)\n" +
                                       $"Chapter: {chapter.Value.parentManga.sortName} - {chapter.Value.fileName}\n" +
                                       $"URL: {imageUrl}";
                        SendNotifications(title, message);
                    }

                    // Accept it on final attempt, might be a very small valid image
                }

                Log($"Image download successful. Final size: {fileInfo.Length} bytes");
                return requestResult.statusCode;
            }
            catch (Exception ex)
            {
                Log($"Error writing image file: {ex.Message} (attempt {attempt}/{maxRetries})");
                if (attempt == maxRetries)
                {
                    // Send notification for file write error after all retries
                    if (chapter != null)
                    {
                        string title = "💾 File Write Error";
                        string message = $"Image {imageNumber} failed to write to disk after {maxRetries} attempts\n" +
                                       $"Chapter: {chapter.Value.parentManga.sortName} - {chapter.Value.fileName}\n" +
                                       $"Error: {ex.Message}\n" +
                                       $"URL: {imageUrl}";
                        SendNotifications(title, message);
                    }
                    return HttpStatusCode.InternalServerError;
                }
                Thread.Sleep(1000);
            }
        }

        return HttpStatusCode.InternalServerError;
    }

    protected HttpStatusCode DownloadChapterImages(string[] imageUrls, Chapter chapter, RequestType requestType, string? referrer = null, ProgressToken? progressToken = null)
    {
        string saveArchiveFilePath = chapter.GetArchiveFilePath();
        
        if (progressToken?.cancellationRequested ?? false)
            return HttpStatusCode.RequestTimeout;
        
        Log($"Downloading Images for {saveArchiveFilePath}");
        
        // Additional check to ensure we don't re-download chapters
        if (chapter.CheckChapterIsDownloaded())
        {
            Log($"Chapter already downloaded for {saveArchiveFilePath} (verified by CheckChapterIsDownloaded).");
            progressToken?.Complete();
            return HttpStatusCode.Created;
        }
        
        if (progressToken is not null)
            progressToken.increments += imageUrls.Length;
            
        //Check if Publication Directory already exists
        string directoryPath = Path.GetDirectoryName(saveArchiveFilePath)!;
        if (!Directory.Exists(directoryPath))
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Directory.CreateDirectory(directoryPath,
                    UserRead | UserWrite | UserExecute | GroupRead | GroupWrite | GroupExecute );
            else
                Directory.CreateDirectory(directoryPath);

        if (File.Exists(saveArchiveFilePath)) //Don't download twice.
        {
            Log($"Chapter already downloaded for {saveArchiveFilePath} (file exists check).");
            
            // Make sure to create a chapter marker if it doesn't exist
            // This helps future duplicate detection
            chapter.CreateChapterMarker(saveArchiveFilePath);
            
            progressToken?.Complete();
            return HttpStatusCode.Created;
        }
        
        //Create a temporary folder to store images
        string tempFolder = Directory.CreateTempSubdirectory("trangatemp").FullName;

        int chapterNum = 0;
        //Download all Images to temporary Folder
        if (imageUrls.Length == 0)
        {
            Log("No images found");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                File.SetUnixFileMode(saveArchiveFilePath, UserRead | UserWrite | UserExecute | GroupRead | GroupWrite | GroupExecute);
            Directory.Delete(tempFolder, true);
            progressToken?.Complete();
            return HttpStatusCode.NoContent;
        }
        foreach (string imageUrl in imageUrls)
        {
            string extension = imageUrl.Split('.')[^1].Split('?')[0];
            Log($"Downloading image {chapterNum + 1:000}/{imageUrls.Length:000}");

            string imagePath = Path.Join(tempFolder, $"{chapterNum++}.{extension}");
            HttpStatusCode status = DownloadImage(imageUrl, imagePath, requestType, referrer, chapter, chapterNum);

            Log($"{saveArchiveFilePath} {chapterNum:000}/{imageUrls.Length:000} {status}");

            // Additional validation after download
            if (File.Exists(imagePath))
            {
                FileInfo imageFile = new FileInfo(imagePath);
                if (imageFile.Length == 0)
                {
                    Log($"CRITICAL: Image {chapterNum:000} is still 0 bytes after all retries!");
                    // Don't fail the entire download for one bad image, but log it prominently
                }
            }

            if ((int)status < 200 || (int)status >= 300)
            {
                Log($"Failed to download image {chapterNum:000}, aborting chapter download");
                progressToken?.Complete();
                return status;
            }
            if (progressToken?.cancellationRequested ?? false)
            {
                progressToken.Complete();
                return HttpStatusCode.RequestTimeout;
            }
            progressToken?.Increment();
        }
        
        File.WriteAllText(Path.Join(tempFolder, "ComicInfo.xml"), chapter.GetComicInfoXmlString());
        
        Log($"Creating archive {saveArchiveFilePath}");
        Log($"Temp folder contains {Directory.GetFiles(tempFolder).Length} files");

        //ZIP-it and ship-it
        ZipFile.CreateFromDirectory(tempFolder, saveArchiveFilePath);

        FileInfo archiveInfo = new FileInfo(saveArchiveFilePath);
        Log($"Archive created successfully. Size: {archiveInfo.Length} bytes");

        chapter.CreateChapterMarker(saveArchiveFilePath);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            File.SetUnixFileMode(saveArchiveFilePath, UserRead | UserWrite | UserExecute | GroupRead | GroupWrite | GroupExecute | OtherRead | OtherExecute);
        Directory.Delete(tempFolder, true); //Cleanup
        
        Log("Created archive.");
        progressToken?.Complete();
        Log("Download complete.");
        return HttpStatusCode.OK;
    }
    
    protected string SaveCoverImageToCache(string url, string mangaInternalId, RequestType requestType, string? referrer = null)
    {
        Regex urlRex = new (@"https?:\/\/((?:[a-zA-Z0-9-]+\.)+[a-zA-Z0-9]+)\/(?:.+\/)*(.+\.([a-zA-Z]+))");
        //https?:\/\/[a-zA-Z0-9-]+\.([a-zA-Z0-9-]+\.[a-zA-Z0-9]+)\/(?:.+\/)*(.+\.([a-zA-Z]+)) for only second level domains
        Match match = urlRex.Match(url);
        string filename = $"{match.Groups[1].Value}-{mangaInternalId}.{match.Groups[3].Value}";
        string saveImagePath = Path.Join(TrangaSettings.coverImageCache, filename);

        if (File.Exists(saveImagePath))
            return saveImagePath;
        
        RequestResult coverResult = downloadClient.MakeRequest(url, requestType, referrer);
        using MemoryStream ms = new();
        coverResult.result.CopyTo(ms);
        Directory.CreateDirectory(TrangaSettings.coverImageCache);
        File.WriteAllBytes(saveImagePath, ms.ToArray());
        Log($"Saving cover to {saveImagePath}");
        return saveImagePath;
    }
}