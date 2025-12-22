namespace CalderaReport.Domain.DB;

public class ConquestMapping
{
    public int Id { get; set; }
    public long BungieActivityId { get; set; }
    public long ActivityId { get; set; }
    public int ExpansionId { get; set; }

    public Activity Activity { get; set; } = null!;
    public Expansion Expansion { get; set; } = null!;
}
