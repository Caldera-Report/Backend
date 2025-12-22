namespace CalderaReport.Domain.DB;

public class Activity
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public required string ImageURL { get; set; }
    public int Index { get; set; }
    public int OpTypeId { get; set; }
    public bool Enabled { get; set; }

    public ICollection<ActivityReport> ActivityReports { get; set; } = [];
    public required OpType OpType { get; set; }
    public ICollection<ConquestMapping> ConquestMappings { get; set; } = [];
}
