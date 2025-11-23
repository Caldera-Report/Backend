using System.Diagnostics;

namespace API.Telemetry;

internal static class APITelemetry
{
    public const string ActivitySourceName = "Caldera.API";
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static Activity? StartActivity(string name) => ActivitySource.StartActivity(name, ActivityKind.Internal);
}
