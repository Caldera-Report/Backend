using CalderaReport.Domain.DB;

namespace CalderaReport.Domain.DTO.Responses;

public class ActivityReportDto
{
    public string Id { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string ActivityId { get; set; } = string.Empty;
    public ActivityDto? Activity { get; set; }
    public List<ActivityReportPlayerDto> Players { get; set; } = new();

    public ActivityReportDto()
    {
    }

    public ActivityReportDto(ActivityReport report)
    {
        Id = report.Id.ToString();
        Date = report.Date;
        ActivityId = report.ActivityId.ToString();
        Activity = report.Activity == null ? null : new ActivityDto(report.Activity);
        Players = report.Players.Select(p => new ActivityReportPlayerDto(p)).ToList();
    }
}
