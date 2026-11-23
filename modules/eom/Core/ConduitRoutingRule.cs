namespace BotPlus.Eom.Core;

internal static class ConduitRoutingRule
{
    internal static bool OfferAlignment(double offset, double tolerance, double coincidence) =>
        offset > coincidence && offset <= tolerance;

    internal static double ParallelLead(double forward, double offset, double coincidence, double minimum)
    {
        if (forward <= minimum)
            throw new InvalidOperationException("Второй прибор находится слишком близко или позади выбранного выхода.");
        if (offset <= coincidence) return 0;
        if (offset <= minimum || forward / 2 <= minimum)
            throw new InvalidOperationException("Для двух отводов не хватает длины продольных или поперечного участков.");
        return forward / 2;
    }
}
