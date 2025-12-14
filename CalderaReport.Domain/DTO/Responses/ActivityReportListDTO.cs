using API.Domain.DTO.Responses;
using API.Models.Responses;

namespace CalderaReport.Domain.DTO.Responses
{
    public class ActivityReportListDto
    {
        public List<ActivityReportPlayerDto> Reports { get; set; } = new();
        public TimeSpan Average { get; set; }
        public ActivityReportPlayerDto? Best { get; set; }
        public ActivityReportPlayerDto? Recent { get; set; }
        public int CountCompleted { get; set; }
    }
}
