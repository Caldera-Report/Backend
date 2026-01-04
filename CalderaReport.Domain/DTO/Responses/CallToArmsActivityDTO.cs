using CalderaReport.Domain.DB;

namespace CalderaReport.Domain.DTO.Responses;

public class CallToArmsActivityDto
{
    public int Id { get; set; }
    public long ActivityId { get; set; }
    public int EventId { get; set; }
    public ActivityDto? Activity { get; set; }

    public CallToArmsActivityDto()
    {
    }

    public CallToArmsActivityDto(CallToArmsActivity activity)
    {
        Id = activity.Id;
        ActivityId = activity.ActivityId;
        EventId = activity.EventId;
        Activity = activity.Activity == null ? null : new ActivityDto(activity.Activity);
    }
}
