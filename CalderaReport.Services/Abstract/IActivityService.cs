using CalderaReport.Domain.DB;

namespace CalderaReport.Services.Abstract;

public interface IActivityService
{
    public Task<IEnumerable<OpType>> GetAllActivities();
    public Task GroupActivityDuplicates();
}