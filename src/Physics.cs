using System;

namespace WhaleCompanion
{
    internal sealed class Spring
    {
        public double Value, Velocity;
        public void Step(double target, double dt)
        {
            // Semi-implicit spring with bounded dt; stable at common desktop refresh rates.
            int count = Math.Max(1, (int)Math.Ceiling(dt / 0.008));
            double h = dt / count;
            for (int i = 0; i < count; i++)
            {
                Velocity += ((target - Value) * 175 - Velocity * 15) * h;
                Value += Velocity * h;
            }
        }
    }
    internal sealed class Glide
    {
        public double X, Y, Vx, Vy;
        public bool Moving { get { return Math.Abs(Vx) + Math.Abs(Vy) > 2; } }
        public void Release(double vx, double vy) { Vx = Clamp(vx, -650, 650) * 0.32; Vy = Clamp(vy, -650, 650) * 0.32; }
        public static double Clamp(double v, double min, double max) { return Math.Max(min, Math.Min(max, v)); }
        public bool Step(double dt, double left, double top, double right, double bottom)
        {
            double decay = Math.Exp(-9.5 * dt);
            X += Vx * (1 - decay) / 9.5; Y += Vy * (1 - decay) / 9.5;
            Vx *= decay; Vy *= decay;
            bool bounce = false;
            if (X < left || X > right) { X = Clamp(X, left, right); Vx *= -0.28; bounce = true; }
            if (Y < top || Y > bottom) { Y = Clamp(Y, top, bottom); Vy *= -0.28; bounce = true; }
            if (!Moving) Vx = Vy = 0;
            return bounce;
        }
    }
}
