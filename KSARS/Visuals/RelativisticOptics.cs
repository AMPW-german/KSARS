namespace KSARS.Visuals
{
    internal readonly struct OpticalVector
    {
        public OpticalVector(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        public static OpticalVector Zero => new(0.0, 0.0, 0.0);

        public static OpticalVector operator +(OpticalVector a, OpticalVector b) =>
            new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static OpticalVector operator -(OpticalVector a, OpticalVector b) =>
            new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static OpticalVector operator *(OpticalVector v, double scalar) =>
            new(v.X * scalar, v.Y * scalar, v.Z * scalar);

        public static OpticalVector operator /(OpticalVector v, double scalar) =>
            new(v.X / scalar, v.Y / scalar, v.Z / scalar);

        public double LengthSquared() => X * X + Y * Y + Z * Z;

        public double Dot(OpticalVector other) =>
            X * other.X + Y * other.Y + Z * other.Z;

        public OpticalVector Cross(OpticalVector other) =>
            new(
                Y * other.Z - Z * other.Y,
                Z * other.X - X * other.Z,
                X * other.Y - Y * other.X);
    }

    internal readonly struct PresentationVelocity
    {
        internal PresentationVelocity(double speed, double q, OpticalVector direction, bool wasConstrained)
        {
            Speed = speed;
            Q = q;
            Direction = direction;
            WasConstrained = wasConstrained;
        }

        public double Speed { get; }
        public double Q { get; }
        public OpticalVector Direction { get; }
        public bool WasConstrained { get; }

        public static PresentationVelocity Rest => new(0.0, 1.0, OpticalVector.Zero, false);

        public double Gamma => (Q + 1.0 / Q) / 2.0;
        public double ForwardD => 1.0 / Q;
        public double RearD => Q;
    }

    internal readonly struct OpticalRay
    {
        internal OpticalRay(OpticalVector direction, double dopplerFactor)
        {
            Direction = direction;
            DopplerFactor = dopplerFactor;
        }

        public OpticalVector Direction { get; }
        public double DopplerFactor { get; }
    }

    internal readonly struct ObservedSphericalCap
    {
        internal ObservedSphericalCap(OpticalVector axis, double angularRadius, double cosAngularRadius, double sinAngularRadius)
        {
            Axis = axis;
            AngularRadius = angularRadius;
            CosAngularRadius = cosAngularRadius;
            SinAngularRadius = sinAngularRadius;
        }

        public OpticalVector Axis { get; }
        public double AngularRadius { get; }
        public double CosAngularRadius { get; }
        public double SinAngularRadius { get; }

        internal bool IntersectsViewCone(OpticalVector coneAxis, double coneAngularRadius)
        {
            double combined = AngularRadius + coneAngularRadius;
            return combined >= Math.PI || Axis.Dot(coneAxis) >= Math.Cos(combined);
        }
    }

    internal static class RelativisticOptics
    {
        public static bool TryCreateVelocity(OpticalVector velocityEcl, out PresentationVelocity observer)
        {
            observer = PresentationVelocity.Rest;
            if (!IsFinite(velocityEcl))
            {
                return false;
            }

            double max = Math.Max(Math.Abs(velocityEcl.X), Math.Max(Math.Abs(velocityEcl.Y), Math.Abs(velocityEcl.Z)));
            if (max == 0.0)
            {
                return true;
            }

            OpticalVector scaled = velocityEcl / max;
            double norm = Math.Sqrt(scaled.LengthSquared());
            OpticalVector direction = scaled / norm;
            double rawSpeed = max * norm;

            double c = PhysicsConstants.SpeedOfLight;
            double maxSpeed = Math.BitDecrement(c);
            double speed = Math.Min(rawSpeed, maxSpeed);
            bool wasConstrained = rawSpeed > maxSpeed;
            double q = Math.Sqrt((c - speed) / (c + speed));

            if (!double.IsFinite(speed) || !double.IsFinite(q) || !IsFinite(direction))
            {
                return false;
            }

            observer = new PresentationVelocity(speed, q, direction, wasConstrained);
            return true;
        }

        public static bool TryForward(OpticalVector restDirection, in PresentationVelocity observer, out OpticalRay ray)
        {
            ray = new OpticalRay(OpticalVector.Zero, 1.0);
            if (!IsValid(observer) || !TryNormalize(restDirection, out OpticalVector n))
            {
                return false;
            }

            if (observer.Q == 1.0)
            {
                ray = new OpticalRay(n, 1.0);
                return true;
            }

            OpticalVector u = observer.Direction;
            double q = observer.Q;
            double qSquared = q * q;
            double a = 0.5 * (n + u).LengthSquared();
            double b = 0.5 * (n - u).LengthSquared();
            OpticalVector perpendicular = u.Cross(n.Cross(u));

            double denominator = a + qSquared * b;
            OpticalVector direction = (u * (a - qSquared * b) + perpendicular * (2.0 * q)) / denominator;
            double dopplerFactor = denominator / (2.0 * q);

            if (!TryNormalize(direction, out OpticalVector unit) || !double.IsFinite(dopplerFactor))
            {
                return false;
            }

            ray = new OpticalRay(unit, dopplerFactor);
            return true;
        }

        public static bool TryInverse(OpticalVector observedDirection, in PresentationVelocity observer, out OpticalRay ray)
        {
            ray = new OpticalRay(OpticalVector.Zero, 1.0);
            if (!IsValid(observer) || !TryNormalize(observedDirection, out OpticalVector n))
            {
                return false;
            }

            if (observer.Q == 1.0)
            {
                ray = new OpticalRay(n, 1.0);
                return true;
            }

            OpticalVector u = observer.Direction;
            double q = observer.Q;
            double qSquared = q * q;
            double a = 0.5 * (n + u).LengthSquared();
            double b = 0.5 * (n - u).LengthSquared();
            OpticalVector perpendicular = u.Cross(n.Cross(u));

            double denominator = b + qSquared * a;
            OpticalVector direction = (u * (qSquared * a - b) + perpendicular * (2.0 * q)) / denominator;
            double dopplerFactor = 2.0 * q / denominator;

            if (!TryNormalize(direction, out OpticalVector unit) || !double.IsFinite(dopplerFactor))
            {
                return false;
            }

            ray = new OpticalRay(unit, dopplerFactor);
            return true;
        }

        public static bool TryTransformSphericalCap(OpticalVector restCenterDirection, double restAngularRadius, in PresentationVelocity observer, out ObservedSphericalCap cap)
        {
            cap = default;
            if (!IsValid(observer) || !TryNormalize(restCenterDirection, out OpticalVector a))
            {
                return false;
            }

            if (!double.IsFinite(restAngularRadius) || restAngularRadius < 0.0 || restAngularRadius > Math.PI / 2.0)
            {
                return false;
            }

            if (observer.Q == 1.0)
            {
                cap = new ObservedSphericalCap(a, restAngularRadius, Math.Cos(restAngularRadius), Math.Sin(restAngularRadius));
                return true;
            }

            OpticalVector u = observer.Direction;
            double q = observer.Q;
            double qSquared = q * q;
            double h = Math.Cos(restAngularRadius);
            double s = Math.Sin(restAngularRadius);
            double delta = s * s / (1.0 + h);
            double plusMu = 0.5 * (a + u).LengthSquared();
            double minusMu = 0.5 * (a - u).LengthSquared();
            OpticalVector perpendicular = u.Cross(a.Cross(u));

            double p = plusMu - delta;
            double m = minusMu - delta;
            OpticalVector axisNumerator = perpendicular * (2.0 * q) + u * (p - qSquared * m);
            double offset = p + qSquared * m;
            double sine = 2.0 * q * s;

            double scale = Math.Max(Math.Abs(offset), Math.Abs(sine));
            double norm = scale == 0.0
                ? 0.0
                : scale * Math.Sqrt((offset / scale) * (offset / scale) + (sine / scale) * (sine / scale));

            if (!TryNormalize(axisNumerator, out OpticalVector axis) || !(norm > 0.0))
            {
                return false;
            }

            cap = new ObservedSphericalCap(axis, Math.Atan2(sine, offset), offset / norm, sine / norm);
            return true;
        }

        private static bool IsValid(in PresentationVelocity observer)
        {
            if (!double.IsFinite(observer.Speed) || observer.Speed < 0.0 || observer.Speed >= PhysicsConstants.SpeedOfLight)
            {
                return false;
            }

            if (!double.IsFinite(observer.Q) || observer.Q <= 0.0 || observer.Q > 1.0)
            {
                return false;
            }

            if (observer.Speed == 0.0)
            {
                return observer.Q == 1.0 && observer.Direction.X == 0.0 && observer.Direction.Y == 0.0 && observer.Direction.Z == 0.0;
            }

            double c = PhysicsConstants.SpeedOfLight;
            double expectedQ = Math.Sqrt((c - observer.Speed) / (c + observer.Speed));
            if (Math.Abs(observer.Q - expectedQ) > expectedQ * 1e-14)
            {
                return false;
            }

            return IsNormalizedNonZero(observer.Direction);
        }

        private static bool IsNormalizedNonZero(OpticalVector v)
        {
            return IsFinite(v) && Math.Abs(v.LengthSquared() - 1.0) <= 1e-9;
        }

        internal static bool TryNormalize(OpticalVector v, out OpticalVector unit)
        {
            unit = OpticalVector.Zero;
            if (!IsFinite(v))
            {
                return false;
            }

            double max = Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z)));
            if (max == 0.0)
            {
                return false;
            }

            OpticalVector scaled = v / max;
            double norm = Math.Sqrt(scaled.LengthSquared());
            unit = scaled / norm;
            return IsFinite(unit);
        }

        private static bool IsFinite(OpticalVector v) =>
            double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
    }
}
