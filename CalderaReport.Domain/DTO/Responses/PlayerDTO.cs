using CalderaReport.Domain.DB;

namespace CalderaReport.Domain.DTO.Responses;

public class PlayerDto
{
    public string Id { get; set; } = string.Empty;
    public int MembershipType { get; set; }
    public string? LastPlayedCharacterEmblemPath { get; set; }
    public string? LastPlayedCharacterBackgroundPath { get; set; }
    public string FullDisplayName { get; set; } = string.Empty;

    public PlayerDto()
    {
    }

    public PlayerDto(Player player)
    {
        Id = player.Id.ToString();
        MembershipType = player.MembershipType;
        LastPlayedCharacterEmblemPath = player.LastPlayedCharacterEmblemPath;
        LastPlayedCharacterBackgroundPath = player.LastPlayedCharacterBackgroundPath;
        FullDisplayName = player.FullDisplayName;
    }
}
