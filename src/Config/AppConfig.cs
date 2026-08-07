using System;
using System.IO;

namespace Riparr.Config
{
    public static class AppConfig
    {
        public static string DownloadsFolder =>
            Environment.GetEnvironmentVariable("DOWNLOADS_DIR") ??
            Path.GetDirectoryName(Path.GetFullPath(CompletedFolder)) ??
            "/downloads";

        public static string CompletedFolder => 
            Environment.GetEnvironmentVariable("DOWNLOADS_COMPLETED_DIR") ?? "/downloads/completed";

        public static string IncompleteFolder => 
            Environment.GetEnvironmentVariable("DOWNLOADS_INCOMPLETE_DIR") ?? "/downloads/incomplete";

        public static string DbPath => 
            Environment.GetEnvironmentVariable("DATABASE_PATH") ?? 
            (Directory.Exists("/downloads") ? "/downloads/downloads.db" : "downloads.db");

        public static string ToolsFolder => 
            Environment.GetEnvironmentVariable("DOWNLOADS_TOOLS_DIR") ?? "/downloads/tools";

        public static string? ApiKey => 
            Environment.GetEnvironmentVariable("API_KEY");

        public static string Port => 
            Environment.GetEnvironmentVariable("PORT") ?? "8080";

        public static void EnsureDirectoriesExist()
        {
            Directory.CreateDirectory(CompletedFolder);
            Directory.CreateDirectory(IncompleteFolder);
            Directory.CreateDirectory(ToolsFolder);
            
            string[] defaultCategories = new[] { "tv", "anime", "movies", "sonarr", "radarr" };
            foreach (var cat in defaultCategories)
            {
                Directory.CreateDirectory(Path.Combine(CompletedFolder, cat));
            }

            var dbDir = Path.GetDirectoryName(Path.GetFullPath(DbPath));
            if (!string.IsNullOrEmpty(dbDir))
            {
                Directory.CreateDirectory(dbDir);
            }
        }
    }
}
