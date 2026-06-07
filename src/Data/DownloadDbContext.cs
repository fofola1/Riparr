using Microsoft.EntityFrameworkCore;
using Riparr.Models;
using Riparr.Config;

namespace Riparr.Data
{
    public class DownloadDbContext : DbContext
    {
        public DbSet<DownloadJob> Downloads { get; set; } = null!;

        public DownloadDbContext()
        {
        }

        public DownloadDbContext(DbContextOptions<DownloadDbContext> options) : base(options)
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder.UseSqlite($"Data Source={AppConfig.DbPath}");
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<DownloadJob>().HasKey(x => x.Id);
        }
    }
}
