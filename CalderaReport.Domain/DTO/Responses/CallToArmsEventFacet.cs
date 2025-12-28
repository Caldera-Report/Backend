using CalderaReport.Domain.DB;
using Facet;

namespace CalderaReport.Domain.DTO.Responses;

[Facet(typeof(CallToArmsEvent), NestedFacets = [typeof(CallToArmsActivityDto)])]
public partial class CallToArmsEventDto
{
}
