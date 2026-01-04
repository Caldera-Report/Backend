using CalderaReport.Domain.DB;

namespace CalderaReport.Domain.DTO.Responses;

public class PlayerSearchDto
{
    public string Id { get; set; } = string.Empty;
    public int MembershipType { get; set; }
    public string? LastPlayedCharacterEmblemPath { get; set; }
    public string FullDisplayName { get; set; } = string.Empty;

    public PlayerSearchDto()
    {
    }

    public PlayerSearchDto(Player player)
    {
        Id = player.Id.ToString();
        MembershipType = player.MembershipType;
        LastPlayedCharacterEmblemPath = player.LastPlayedCharacterEmblemPath;
        FullDisplayName = player.FullDisplayName;
    }
}
