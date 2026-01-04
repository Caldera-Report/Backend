using CalderaReport.Domain.DB;

namespace CalderaReport.Domain.DTO.Responses;

public class ActivityDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ImageURL { get; set; } = string.Empty;
    public int Index { get; set; }
    public int OpTypeId { get; set; }

    public ActivityDto()
    {
    }

    public ActivityDto(Activity activity)
    {
        Id = activity.Id.ToString();
        Name = activity.Name.Contains(": Customize") ? activity.Name[..^11] : activity.Name;
        ImageURL = activity.ImageURL;
        Index = activity.Index;
        OpTypeId = activity.OpTypeId;
    }
}
