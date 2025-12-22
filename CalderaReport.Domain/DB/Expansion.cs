namespace CalderaReport.Domain.DB;

public class Expansion
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public DateTime ReleaseDate { get; set; }
    public DateTime? EndDate { get; set; }

    public ICollection<ConquestMapping> ConquestMappings { get; set; } = [];
}
