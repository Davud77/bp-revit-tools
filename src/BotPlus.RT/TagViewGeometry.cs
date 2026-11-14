namespace BotPlus.RT;

internal static class TagViewGeometry
{
    internal static double Distance((double X,double Y,double Z) delta,
        (double X,double Y,double Z) right,(double X,double Y,double Z) up)
    {
        double x=delta.X*right.X+delta.Y*right.Y+delta.Z*right.Z;
        double y=delta.X*up.X+delta.Y*up.Y+delta.Z*up.Z;
        return Math.Sqrt(x*x+y*y);
    }
}
