using System;

namespace Islet
{
    // Second-order dynamics follower ("procedural animation" spring).
    //   f: natural frequency in Hz (speed of response)
    //   z: damping (< 1 bounces, 1 settles without overshoot, > 1 is sluggish)
    //   r: initial response (0 eases in, 1 reacts at once, > 1 overshoots, < 0 anticipates)
    // The value y chases the target x by integrating  x + k3*x' = y + k1*y' + k2*y''.
    public sealed class Spring
    {
        private readonly double k1, k2, k3;
        private double xp, y, yd;

        public Spring(double f, double z, double r, double initial)
        {
            double w = 2 * Math.PI * f;
            k1 = z / (Math.PI * f);
            k2 = 1 / (w * w);
            k3 = r * z / w;
            xp = initial;
            y = initial;
        }

        public double Value { get { return y; } }

        public override string ToString()
        {
            return "y=" + y.ToString("0.###") + " target=" + xp.ToString("0.###") + " v=" + yd.ToString("0.###");
        }

        public bool IsSettled
        {
            get { return Math.Abs(xp - y) < 0.05 && Math.Abs(yd) < 0.5; }
        }

        public double Update(double dt, double x)
        {
            if (dt <= 0) return y;
            double xd = (x - xp) / dt;
            xp = x;

            // Long frames are split into short sub-steps, and k2 is clamped, so a
            // stalled frame can never make the explicit integration blow up.
            int steps = Math.Min(8, (int)Math.Ceiling(dt / 0.008));
            double h = dt / steps;
            double k2Stable = Math.Max(k2, Math.Max(h * h / 2 + h * k1 / 2, h * k1));
            for (int i = 0; i < steps; i++)
            {
                y += h * yd;
                yd += h * (x + k3 * xd - y - k1 * yd) / k2Stable;
                xd = 0; // the target only "moved" during the first sub-step
            }
            return y;
        }
    }
}
