using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static System.IO.UnixFileMode;

namespace Tranga;

/// <summary>
/// Has to be Part of a publication
/// Includes the Chapter-Name, -VolumeNumber, -ChapterNumber, the location of the chapter on the internet and the saveName of the local file.
/// </summary>
public readonly struct Chapter : IComparable
{
    // ReSharper disable once MemberCanBePrivate.Global
    public Manga parentManga { get; }
    public string? name { get; }
    public float volumeNumber { get; }
    public float chapterNumber { get; }
    public string url { get; }
    // ReSharper disable once MemberCanBePrivate.Global
    public string fileName { get; }
    public string? id { get; }
    
    private static readonly Regex LegalCharacters = new (@"([A-z]*[0-9]* *\.*-*,*\]*\[*'*\'*\)*\(*~*!*)*");
    private static readonly Regex IllegalStrings = new(@"(Vol(ume)?|Ch(apter)?)\.?", RegexOptions.IgnoreCase);

    public Chapter(Manga parentManga, string? name, string? volumeNumber, string chapterNumber, string url, string? id = null)
        : this(parentManga, name, float.Parse(volumeNumber??"0", GlobalBase.numberFormatDecimalPoint),
            float.Parse(chapterNumber, GlobalBase.numberFormatDecimalPoint), url, id)
    {
    }
    
    public Chapter(Manga parentManga, string? name, float? volumeNumber, float chapterNumber, string url, string? id = null)
    {
        this.parentManga = parentManga;
        this.name = name;
        this.volumeNumber = volumeNumber??0;
        this.chapterNumber = chapterNumber;
        this.url = url;
        this.id = id;
        
        string chapterVolNumStr = $"Vol.{this.volumeNumber} Ch.{chapterNumber}";

        if (name is not null && name.Length > 0)
        {
            string chapterName = IllegalStrings.Replace(string.Concat(LegalCharacters.Matches(name)), "");
            this.fileName = chapterName.Length > 0 ? $"{chapterVolNumStr} - {chapterName}" : chapterVolNumStr;
        }
        else
            this.fileName = chapterVolNumStr;
    }

    public override string ToString()
    {
        return $"Chapter {parentManga.sortName} {parentManga.internalId} {chapterNumber} {name}";
    }

    public override bool Equals(object? obj)
    {
        if (obj is not Chapter)
            return false;
        return CompareTo(obj) == 0;
    }

    public int CompareTo(object? obj)
    {
        if(obj is not Chapter otherChapter)
            throw new ArgumentException($"{obj} can not be compared to {this}");
        return volumeNumber.CompareTo(otherChapter.volumeNumber) switch
        {
            <0 => -1,
            >0 => 1,
            _ => chapterNumber.CompareTo(otherChapter.chapterNumber)
        };
    }

    /// <summary>
    /// Checks if a chapter-archive is already present
    /// </summary>
    /// <returns>true if chapter is present</returns>
    internal bool CheckChapterIsDownloaded()
    {
        string mangaDirectory = Path.Join(TrangaSettings.downloadLocation, parentManga.folderName);
        if (!Directory.Exists(mangaDirectory))
            return false;
            
        string fullExpectedPath = Path.Join(mangaDirectory, $"{parentManga.folderName} - {this.fileName}.cbz");
        
        // Direct path check - fastest method
        if (File.Exists(fullExpectedPath))
        {
            return true;
        }
        
        FileInfo? mangaArchive = null;
        string markerPath = Path.Join(mangaDirectory, $".{id}");
        if (this.id is not null && File.Exists(markerPath))
        {
            if(File.Exists(File.ReadAllText(markerPath)))
            {
                mangaArchive = new FileInfo(File.ReadAllText(markerPath));
            }
            else
            {
                File.Delete(markerPath);
            }
        }
        
        // If we couldn't find it by marker, search by filename pattern
        if(mangaArchive is null)
        {
            FileInfo[] archives = new DirectoryInfo(mangaDirectory).GetFiles("*.cbz");
            
            // Check for duplicated chapter by volume and chapter number first - STRICT MATCHING ONLY
            foreach (FileInfo archive in archives)
            {
                // First, try to find files with exact volume and chapter numbers
                // This handles cases where the name might be completely different or truncated
                string chapterNumStr = GetFormattedChapterNumber();
                string volNumStr = GetFormattedVolumeNumber();
                
                // Look for the chapter and volume numbers in the filename - STRICT MATCHING
                // We need to make sure we match the exact chapter, not just a substring
                // For example, "Ch.2" should not match "Ch.23"
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(archive.Name);
                
                // Format the patterns we're looking for
                string chapterPattern = $"Ch.{chapterNumStr}";
                
                // Add markers to ensure we match exact numbers with proper boundaries
                if ((fileNameWithoutExtension.Contains($"Vol.{volNumStr} {chapterPattern} ") || 
                     fileNameWithoutExtension.Contains($"Vol.{volNumStr} {chapterPattern}-") ||
                     fileNameWithoutExtension.Contains($"Vol.{volNumStr} {chapterPattern}.") ||
                     fileNameWithoutExtension.EndsWith($"Vol.{volNumStr} {chapterPattern}")) &&
                    // Additional check to prevent Ch.X.Y matching as Ch.X
                    !(chapterNumStr.IndexOf('.') == -1 && 
                      Regex.IsMatch(fileNameWithoutExtension, $@"Ch\.{chapterNumStr}\.[0-9]+")))
                {
                    // Found a matching file by volume and chapter number
                    mangaArchive = archive;
                    break;
                }
            }
            
            // If still not found, use the more detailed regex approach
            if (mangaArchive == null)
            {
                // More flexible regex to match different filename patterns
                // 1. Standard format with manga name: MangaName - Vol.X Ch.Y[ - ChapterName].cbz
                // 2. Direct format: Vol.X Ch.Y[ - ChapterName].cbz
                // This pattern carefully handles decimal chapter numbers like Ch.75.5
                Regex volChRex = new(@".*(Vol(?:ume)?\.([0-9]+)\D*Ch(?:apter)?\.([0-9]+(?:\.[0-9]+)?)(?: - (.*))?)\.cbz");

                Chapter t = this;
                
                // Get current chapter's expected filename (without path)
                string expectedFilename = $"{parentManga.folderName} - {this.fileName}.cbz";
                
                // Try to find a matching file
                mangaArchive = archives.FirstOrDefault(archive => 
                {
                    // First try exact filename match (fastest)
                    if (Path.GetFileName(archive.FullName).Equals(expectedFilename, StringComparison.OrdinalIgnoreCase))
                        return true;
                    
                    // If no exact match, try regex pattern matching
                    Match m = volChRex.Match(archive.Name);
                    if (!m.Success) 
                        return false;
                    
                    // Extract values from regex match
                    string fileVolumeNum = m.Groups[2].Value;
                    string fileChapterNum = m.Groups[3].Value;
                    string? fileChapterName = m.Groups[4].Success ? m.Groups[4].Value : null;
                    
                    // Check volume match
                    bool volumeMatches = string.IsNullOrEmpty(fileVolumeNum) || 
                                         fileVolumeNum == t.volumeNumber.ToString(GlobalBase.numberFormatDecimalPoint);
                    
                    // Check chapter match - must be exact, not partial
                    bool chapterMatches = fileChapterNum == t.chapterNumber.ToString(GlobalBase.numberFormatDecimalPoint);
                    
                    // Additional safety: If there's a mismatch, check if it's due to parsing decimal vs. integer
                    if (!chapterMatches && 
                        float.TryParse(fileChapterNum, GlobalBase.numberFormatDecimalPoint, out float fileChNum) && 
                        fileChNum == t.chapterNumber)
                    {
                        chapterMatches = true;
                    }
                    
                    // Name matching can be more flexible
                    bool nameMatches = (fileChapterName == null && string.IsNullOrEmpty(t.name)) || 
                                       (fileChapterName != null && t.name != null && (
                                           // Exact match
                                           fileChapterName == t.name || 
                                           // Prefix match (handles truncation at the end)
                                           t.name.StartsWith(fileChapterName) ||
                                           // Reversed prefix match (filename truncated)
                                           fileChapterName.StartsWith(t.name) ||
                                           // First few words match (handles partial word truncation)
                                           (t.name.Split(' ').Length > 0 && fileChapterName.Split(' ').Length > 0 &&
                                            t.name.Split(' ')[0] == fileChapterName.Split(' ')[0])
                                       ));
                    
                    return volumeMatches && chapterMatches && nameMatches;
                });
            }
        }
        
        // IMPORTANT: DO NOT move files here, it causes incorrect file renaming!
        // Just return whether we found a matching archive
        return (mangaArchive is not null);
    }
    
    public void CreateChapterMarker(string? actualFilePath = null)
    {
        if (this.id is null)
            return;
            
        string markerPath = Path.Join(TrangaSettings.downloadLocation, parentManga.folderName, $".{id}");
        
        // If an actual file path is provided, use that
        // Otherwise use the default expected path
        string pathToWrite = actualFilePath ?? GetArchiveFilePath();
        
        File.WriteAllText(markerPath, pathToWrite);
        File.SetAttributes(markerPath, FileAttributes.Hidden);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))  
            File.SetUnixFileMode(markerPath, UserRead | UserWrite | UserExecute | GroupRead | GroupWrite | GroupExecute | OtherRead | OtherExecute);
    }
    
    /// <summary>
    /// Creates full file path of chapter-archive
    /// </summary>
    /// <returns>Filepath</returns>
    internal string GetArchiveFilePath()
    {
        return Path.Join(TrangaSettings.downloadLocation, parentManga.folderName, $"{parentManga.folderName} - {this.fileName}.cbz");
    }

    /// <summary>
    /// Creates a string containing XML of publication and chapter.
    /// See ComicInfo.xml
    /// </summary>
    /// <returns>XML-string</returns>
    internal string GetComicInfoXmlString()
    {
        XElement comicInfo = new XElement("ComicInfo",
            new XElement("Tags", string.Join(',', parentManga.tags)),
            new XElement("LanguageISO", parentManga.originalLanguage),
            new XElement("Title", this.name),
            new XElement("Writer", string.Join(',', parentManga.authors)),
            new XElement("Volume", this.volumeNumber),
            new XElement("Number", this.chapterNumber)
        );
        return comicInfo.ToString();
    }
    
    /// <summary>
    /// Returns the chapter number as a string formatted for display
    /// </summary>
    /// <returns>Formatted chapter number</returns>
    internal string GetFormattedChapterNumber()
    {
        return chapterNumber.ToString(GlobalBase.numberFormatDecimalPoint);
    }
    
    /// <summary>
    /// Returns the volume number as a string formatted for display
    /// </summary>
    /// <returns>Formatted volume number</returns>
    internal string GetFormattedVolumeNumber()
    {
        return volumeNumber.ToString(GlobalBase.numberFormatDecimalPoint);
    }
}