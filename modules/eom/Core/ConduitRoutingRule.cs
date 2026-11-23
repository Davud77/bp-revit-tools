namespace BotPlus.Eom.Core;

internal static class ConduitRoutingRule
{
    internal const double AngleStep = 15;
    internal static double SnapHeading(double angle) => Math.Round(angle / AngleStep, MidpointRounding.AwayFromZero) * AngleStep;
    internal static readonly double[] BendAngles = [15, 30, 45, 60, 75, 90];
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

    internal static double SnapTurn(double turn, IReadOnlyList<double> allowed)
    {
        var magnitude = Math.Abs(turn);
        var best = 0.0;
        var distance = magnitude;
        foreach (var angle in allowed)
        {
            var difference = Math.Abs(angle - magnitude);
            if (difference >= distance) continue;
            best = angle;
            distance = difference;
        }
        return Math.CopySign(best, turn);
    }
}
