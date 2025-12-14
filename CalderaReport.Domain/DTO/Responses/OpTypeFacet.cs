using CalderaReport.Domain.DB;
using Facet;

namespace API.Models.Responses
{
    [Facet(typeof(OpType), NestedFacets = [typeof(ActivityDto)])]
    public partial class OpTypeDto;
}
