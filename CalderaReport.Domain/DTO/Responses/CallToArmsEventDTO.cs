using CalderaReport.Domain.DB;

namespace CalderaReport.Domain.DTO.Responses;

public class CallToArmsEventDto
{
    public int Id { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public List<CallToArmsActivityDto> CallToArmsActivities { get; set; } = new();

    public CallToArmsEventDto()
    {
    }

    public CallToArmsEventDto(CallToArmsEvent callToArmsEvent)
    {
        Id = callToArmsEvent.Id;
        StartDate = callToArmsEvent.StartDate;
        EndDate = callToArmsEvent.EndDate;
        CallToArmsActivities = callToArmsEvent.CallToArmsActivities
            .Select(activity => new CallToArmsActivityDto(activity))
            .ToList();
    }
}
