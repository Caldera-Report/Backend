using System.ComponentModel.DataAnnotations.Schema;

namespace CalderaReport.Domain.DB;

public class Player
{
    public required long Id { get; set; }
    public required int MembershipType { get; set; }
    public required string DisplayName { get; set; }
    public required int DisplayNameCode { get; set; }
    public required string FullDisplayName { get; set; } = null!;
    public string? LastPlayedCharacterEmblemPath { get; set; }
    public string? LastPlayedCharacterBackgroundPath { get; set; }
    public DateTime? LastCrawlStarted { get; set; }
    public DateTime? LastCrawlCompleted { get; set; }
    public bool NeedsFullCheck { get; set; }

    public ICollection<ActivityReportPlayer> ActivityReportPlayers { get; set; }
}
