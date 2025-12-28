using CalderaReport.Domain.DB;
using Facet;

namespace CalderaReport.Domain.DTO.Responses;

[Facet(typeof(CallToArmsActivity), exclude: nameof(CallToArmsActivity.Event))]
public partial class CallToArmsActivityDto
{
}
