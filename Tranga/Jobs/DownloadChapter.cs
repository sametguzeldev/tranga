using System.Net;
using Tranga.MangaConnectors;

namespace Tranga.Jobs;

public class DownloadChapter : Job
{
    public Chapter chapter { get; init; }

    public DownloadChapter(GlobalBase clone, MangaConnector connector, Chapter chapter, DateTime lastExecution, string? parentJobId = null) : base(clone, JobType.DownloadChapterJob, connector, lastExecution, parentJobId: parentJobId)
    {
        this.chapter = chapter;
    }
    
    public DownloadChapter(GlobalBase clone, MangaConnector connector, Chapter chapter, string? parentJobId = null) : base(clone, JobType.DownloadChapterJob, connector, parentJobId: parentJobId)
    {
        this.chapter = chapter;
    }
    
    protected override string GetId()
    {
        return $"{GetType()}-{chapter.parentManga.internalId}-{chapter.chapterNumber}";
    }

    public override string ToString()
    {
        return $"{id} Chapter: {chapter}";
    }

    protected override IEnumerable<Job> ExecuteReturnSubTasksInternal(JobBoss jobBoss)
    {
        Task downloadTask = new(delegate
        {
            // First check if chapter is already downloaded to avoid unnecessary processing
            if (chapter.CheckChapterIsDownloaded())
            {
                Log($"Chapter {chapter} is already downloaded. Skipping download.");
                progressToken.Complete();
                return;
            }
            
            mangaConnector.CopyCoverFromCacheToDownloadLocation(chapter.parentManga);
            HttpStatusCode success = mangaConnector.DownloadChapter(chapter, this.progressToken);
            chapter.parentManga.UpdateLatestDownloadedChapter(chapter);
            if (success == HttpStatusCode.OK || success == HttpStatusCode.Created)
            {
                UpdateLibraries();
                SendNotifications("Chapter downloaded", $"{chapter.parentManga.sortName} - {chapter.chapterNumber}", true);
            }
        });
        downloadTask.Start();
        return Array.Empty<Job>();
    }

    public override bool Equals(object? obj)
    {
        if (obj is not DownloadChapter otherJob)
            return false;
        return otherJob.mangaConnector == this.mangaConnector &&
               otherJob.chapter.Equals(this.chapter);
    }
}