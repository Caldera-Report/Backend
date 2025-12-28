using CalderaReport.Domain.DB;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CalderaReport.Domain.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }
        public DbSet<Player> Players { get; set; }
        public DbSet<OpType> OpTypes { get; set; }
        public DbSet<Activity> Activities { get; set; }
        public DbSet<ActivityReport> ActivityReports { get; set; }
        public DbSet<ActivityReportPlayer> ActivityReportPlayers { get; set; }
        public DbSet<PlayerCrawlQueue> PlayerCrawlQueue { get; set; }
        public DbSet<PlayerLeaderboard> PlayerLeaderboards { get; set; }
        public DbSet<ConquestMapping> ConquestMappings { get; set; }
        public DbSet<Expansion> Expansions { get; set; }
        public DbSet<CallToArmsEvent> CallToArmsEvents { get; set; }
        public DbSet<CallToArmsActivity> CallToArmsActivities { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Player>()
                .Property(p => p.Id)
                .ValueGeneratedNever();

            modelBuilder.Entity<Activity>()
                .Property(a => a.Id)
                .ValueGeneratedNever();

            modelBuilder.Entity<ActivityReport>(entity =>
            {
                entity.Property(ar => ar.Id).ValueGeneratedNever();
                entity.HasIndex(ar => new { ar.ActivityId, ar.Date });
                entity.HasIndex(ar => ar.ActivityId);
            });

            modelBuilder.Entity<ActivityReportPlayer>(entity =>
            {
                entity.HasKey(arp => new { arp.ActivityReportId, arp.PlayerId, arp.SessionId });

                entity.HasIndex(arp => arp.PlayerId);
            });

            modelBuilder.Entity<PlayerLeaderboard>(entity =>
            {
                entity.HasKey(pl => new { pl.PlayerId, pl.ActivityId, pl.LeaderboardType });

                entity.HasIndex(pl => new { pl.ActivityId, pl.LeaderboardType, pl.Data });
            });

            modelBuilder.Entity<PlayerCrawlQueue>()
                .HasIndex(pcq => pcq.PlayerId)
                .IsUnique();

        }
    }
}
