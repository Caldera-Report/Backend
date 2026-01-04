using CalderaReport.Domain.DB;
using CalderaReport.Domain.DTO.Responses;

namespace CalderaReport.Services.Abstract;

public interface IActivityService
{
    public Task<IEnumerable<OpTypeDto>> GetAllActivities();
}