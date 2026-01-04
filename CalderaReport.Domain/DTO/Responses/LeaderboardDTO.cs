namespace CalderaReport.Domain.DTO.Responses;

public class LeaderboardDTO
{
    public PlayerDto Player { get; set; } = new PlayerDto();
    public int Rank { get; set; }
    public string Data { get; set; } = string.Empty;
}
