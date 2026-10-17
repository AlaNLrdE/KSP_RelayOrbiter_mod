namespace RelayOrbiter.Core
{
    // (ISP) Plans maneuvers from a vessel's actual orbit; no KSP types.
    public interface IDeploymentPlanner
    {
        // Plan from an orbit given by its apoapsis radius and semi-major axis,
        // with the burn at the apoapsis passage at apoapsisUT.
        DeploymentPlan Plan(double mu, double apoapsisRadius, double semiMajorAxis, double apoapsisUT);

        // Prograde Δv at apoapsis that makes the orbit circular at the apoapsis radius.
        double CircularizeAtApoapsis(double mu, double apoapsisRadius, double semiMajorAxis);

        // UT of the next apoapsis at least minLead seconds away, so there is time to prepare the burn.
        double NextApoapsisUT(double now, double timeToApoapsis, double period, double minLead);
    }
}
