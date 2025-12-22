using CalderaReport.Domain.DB;
using Facet;

namespace API.Domain.DTO.Responses
{
    [Flatten(typeof(ActivityReportPlayer),
        exclude: [ nameof(ActivityReportPlayer.Player),
        nameof(ActivityReportPlayer.ActivityReportId),
        nameof(ActivityReportPlayer.Score),
        nameof(ActivityReportPlayer.ActivityId),
        nameof(ActivityReportPlayer.PlayerId)],
        NamingStrategy = FlattenNamingStrategy.SmartLeaf,
        Exclude = ["ActivityReport.NeedsFullCheck", "ActivityReport.ActivityId"],
        MaxDepth = 2
    )]
    public partial class ActivityReportPlayerDto;
}
