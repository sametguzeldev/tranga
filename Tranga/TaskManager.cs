﻿using System.Text.Json;

namespace Tranga;

public class TaskManager
{
    private readonly Dictionary<Publication, List<Chapter>> _chapterCollection;
    private readonly HashSet<TrangaTask> _allTasks;
    private bool _continueRunning = true;
    
    public TaskManager()
    {
        _chapterCollection = new();
        _allTasks = ImportTasks(Directory.GetCurrentDirectory());
        Thread taskChecker = new(TaskCheckerThread);
        taskChecker.Start();
    }

    private void TaskCheckerThread()
    {
        while (_continueRunning)
        {
            foreach (TrangaTask task in _allTasks.Where(trangaTask => trangaTask.ShouldExecute(true)))
            {
                TaskExecutor.Execute(task, this._chapterCollection);
            }
            Thread.Sleep(1000);
        }
    }

    public void Shutdown()
    {
        _continueRunning = false;
        ExportTasks(Directory.GetCurrentDirectory());
    }

    public HashSet<TrangaTask> ImportTasks(string importFolderPath)
    {
        string filePath = Path.Join(importFolderPath, "tasks.json");
        if (!File.Exists(filePath))
            return new HashSet<TrangaTask>();
        
        FileStream file = new FileStream(filePath, FileMode.Open);

        TrangaTask[] importTasks = JsonSerializer.Deserialize<TrangaTask[]>(file, JsonSerializerOptions.Default)!;
        return importTasks.ToHashSet();
    }

    public void ExportTasks(string exportFolderPath)
    {
        FileStream file = new FileStream(Path.Join(exportFolderPath, "tasks.json"), FileMode.CreateNew);
        JsonSerializer.Serialize(file, _allTasks.ToArray(), JsonSerializerOptions.Default);
        file.Close();
        file.Dispose();
    }
}