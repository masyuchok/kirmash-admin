using backend.Models;

namespace backend.Services;

public static class VatReportLockGuard
{
    public const string LockedMessage =
        "Справаздача за гэты перыяд заблакавана. Разблакуйце яе, каб уносіць змяненні.";

    public static void EnsureNotLocked( VatReport report )
    {
        if (report.IsLocked)
        {
            throw new InvalidOperationException( LockedMessage );
        }
    }
}
