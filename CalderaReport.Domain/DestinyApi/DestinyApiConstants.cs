namespace CalderaReport.Domain.DestinyApi;

public static class DestinyApiConstants
{
    public static ISet<int> NonRetryableErrorCodes { get; } = new HashSet<int>
    {
        (int)BungieErrorCodes.AccountNotFound,
        (int)BungieErrorCodes.PrivateAccount
    };
}
public enum BungieErrorCodes
{
    AccountNotFound = 1601,
    PrivateAccount = 1665
};
