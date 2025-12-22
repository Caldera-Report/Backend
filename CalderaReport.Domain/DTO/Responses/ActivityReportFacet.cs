using CalderaReport.Domain.DB;
using Facet;

namespace API.Models.Responses
{
    [Facet(typeof(ActivityReport), exclude: [nameof(ActivityReport.Activity), nameof(ActivityReport.Players)])]
    public partial class ActivityReportDto;
}
