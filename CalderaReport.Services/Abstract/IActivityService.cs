using API.Models.Responses;
using CalderaReport.Domain.DB;

namespace CalderaReport.Services.Abstract;

public interface IActivityService
{
    public Task<IEnumerable<OpTypeDto>> GetAllActivities();
}