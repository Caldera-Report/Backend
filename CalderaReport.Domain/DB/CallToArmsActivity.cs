namespace CalderaReport.Domain.DB;

public class CallToArmsActivity
{
    public int Id { get; set; }
    public long ActivityId { get; set; }
    public int EventId { get; set; }

    public Activity Activity { get; set; }
    public CallToArmsEvent Event { get; set; }
}
