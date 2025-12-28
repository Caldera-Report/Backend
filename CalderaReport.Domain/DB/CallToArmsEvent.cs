namespace CalderaReport.Domain.DB;

public class CallToArmsEvent
{
    public int Id { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }

    public ICollection<CallToArmsActivity> CallToArmsActivities { get; set; } = new List<CallToArmsActivity>();
}
