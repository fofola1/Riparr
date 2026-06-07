using System;
using System.IO;

namespace Riparr.Config
{
    public static class AppConfig
    {
        public static string CompletedFolder => 
            Environment.GetEnvironmentVariable("DOWNLOADS_COMPLETED_DIR") ?? "/downloads/completed";

        public static string IncompleteFolder => 
            Environment.GetEnvironmentVariable("DOWNLOADS_INCOMPLETE_DIR") ?? "/downloads/incomplete";

        public static string DbPath => 
            Environment.GetEnvironmentVariable("DATABASE_PATH") ?? 
            (Directory.Exists("/downloads") ? "/downloads/downloads.db" : "downloads.db");

        public static string? ApiKey => 
            Environment.GetEnvironmentVariable("API_KEY");

        public static string Port => 
            Environment.GetEnvironmentVariable("PORT") ?? "8080";

        public static void EnsureDirectoriesExist()
        {
            Directory.CreateDirectory(CompletedFolder);
            Directory.CreateDirectory(IncompleteFolder);
            
            var dbDir = Path.GetDirectoryName(Path.GetFullPath(DbPath));
            if (!string.IsNullOrEmpty(dbDir))
            {
                Directory.CreateDirectory(dbDir);
            }
        }
    }
}
