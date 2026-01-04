using CalderaReport.Domain.DB;

namespace CalderaReport.Domain.DTO.Responses;

public class OpTypeDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<ActivityDto> Activities { get; set; } = new();

    public OpTypeDto()
    {
    }

    public OpTypeDto(OpType opType)
    {
        Id = opType.Id;
        Name = opType.Name;
        Activities = opType.Activities == null
            ? new List<ActivityDto>()
            : opType.Activities.Select(activity => new ActivityDto(activity)).ToList();
    }
}
