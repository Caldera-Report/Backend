using CalderaReport.Domain.DB;

namespace CalderaReport.Domain.DTO.Responses;

public class ActivityReportPlayerDto
{
    public string Id { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public bool Completed { get; set; }
    public TimeSpan Duration { get; set; }

    public ActivityReportPlayerDto()
    {
    }

    public ActivityReportPlayerDto(ActivityReportPlayer report)
    {
        Id = report.ActivityReportId.ToString();
        Date = report.ActivityReport.Date;
        Completed = report.Completed;
        Duration = report.Duration;
    }
}
