namespace RelayOrbiter.Core
{
    // (ISP) Plans maneuvers from a vessel's actual orbit; no KSP types.
    public interface IDeploymentPlanner
    {
        // Plan from an orbit given by its apoapsis radius and semi-major axis,
        // with the burn at the apoapsis passage at apoapsisUT.
        // For an outer parking orbit, the release point is the periapsis instead.
        DeploymentPlan Plan(double mu, double apoapsisRadius, double semiMajorAxis, double apoapsisUT,
                            int satellites = 3, ParkingOrbit parking = ParkingOrbit.Inner);

        // Prograde Δv at an apsis of radius r that makes the orbit circular there
        // (positive at apoapsis, negative at periapsis).
        double CircularizeAt(double mu, double r, double semiMajorAxis);

        // UT of the next passage through a point of the orbit (apoapsis or periapsis)
        // at least minLead seconds away, so there is time to prepare the burn.
        double NextPassUT(double now, double timeToPoint, double period, double minLead);
    }
}
