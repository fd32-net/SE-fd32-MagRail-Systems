using System;

namespace MagRail
{
    // A flexible batten (the draughtsman's spline) over supports: the line through points 0..n, evenly spaced, both
    // ends held at 0 and level, that bends least (sum of squared second differences) while staying at or over the
    // supports (support[k], m). Between the points it touches it is a cubic, so its curvature changes smoothly: a train
    // feels no jolt where it starts to rise. Pushed up only from below, it never dips under 0.
    // Active set: solve with the touching points held, let go a point that pulls the batten down, take the point it
    // passes furthest under; each solve is a banded (pentadiagonal) Cholesky.
    public static class Batten
    {
        // A point counts as touched when the batten passes this far under it, m.
        const double Slack = 0.005;

        // Bending matrix of the inner points 1..n-1 (index k - 1): diagonal, first and second off-diagonals.
        static double[] ad = new double[0], a1 = new double[0], a2 = new double[0];
        // Held points, the solution, and the compressed system of the free points (band of the Cholesky factor).
        static bool[] held = new bool[0];
        static double[] x = new double[0], rhs = new double[0], l0 = new double[0], l1 = new double[0], l2 = new double[0];
        static int[] free = new int[0];
        // Solves the last call took (statistics).
        public static int Iterations;

        // result[0..n]: the batten's height at every point.
        public static void Solve(double[] support, int n, double[] result)
        {
            int m = n - 1;
            for (int k = 0; k <= n; k++)
                result[k] = 0;
            if (m < 1)
                return;
            Ensure(m);
            Build(m);
            for (int i = 0; i < m; i++)
            {
                held[i] = false;
                x[i] = 0;
            }

            for (Iterations = 1; Iterations <= 4 * m + 20; Iterations++)
            {
                Solve(m, support);
                // Let go the held point that pulls the batten down most (its support would have to pull).
                int worst = -1;
                double force = -1e-9;
                for (int i = 0; i < m; i++)
                {
                    if (!held[i])
                        continue;
                    double f = Product(i, m);
                    if (f < force)
                    {
                        force = f;
                        worst = i;
                    }
                }
                if (worst >= 0)
                {
                    held[worst] = false;
                    continue;
                }
                // Hold the point the batten passes furthest under.
                double gap = Slack;
                for (int i = 0; i < m; i++)
                {
                    double g = support[i + 1] - x[i];
                    if (!held[i] && g > gap)
                    {
                        gap = g;
                        worst = i;
                    }
                }
                if (worst < 0)
                    break;
                held[worst] = true;
            }
            for (int i = 0; i < m; i++)
                result[i + 1] = Math.Max(0, x[i]);
        }

        static void Ensure(int m)
        {
            if (ad.Length >= m)
                return;
            ad = new double[m];
            a1 = new double[m];
            a2 = new double[m];
            held = new bool[m];
            x = new double[m];
            rhs = new double[m];
            l0 = new double[m];
            l1 = new double[m];
            l2 = new double[m];
            free = new int[m];
        }

        // Sum over the second differences d_k = L(k-1) - 2 L(k) + L(k+1), k = 0..n, of d_k d_k^T, with L(0) = L(n) = 0
        // and the ends level (L(-1) = L(1), L(n+1) = L(n-1)): d_0 = 2 L(1), d_n = 2 L(n-1).
        static void Build(int m)
        {
            for (int i = 0; i < m; i++)
                ad[i] = a1[i] = a2[i] = 0;
            AddRow(0, 2, -1, 0, -1, 0, m);
            AddRow(m - 1, 2, -1, 0, -1, 0, m);
            for (int k = 1; k <= m; k++)    // inner point k = unknown k - 1
                AddRow(k - 2, 1, k - 1, -2, k, 1, m);
        }

        // One row: up to three unknowns (index < 0 or >= m: a fixed end, left out) with their coefficients.
        static void AddRow(int i, double ci, int j, double cj, int k, double ck, int m)
        {
            int[] idx = { i, j, k };
            double[] co = { ci, cj, ck };
            for (int a = 0; a < 3; a++)
            {
                if (idx[a] < 0 || idx[a] >= m || co[a] == 0)
                    continue;
                for (int b = a; b < 3; b++)
                {
                    if (idx[b] < 0 || idx[b] >= m || co[b] == 0)
                        continue;
                    int lo = Math.Min(idx[a], idx[b]), d = Math.Abs(idx[a] - idx[b]);
                    double v = co[a] * co[b];
                    if (d == 0)
                        ad[lo] += a == b ? v : 2 * v;
                    else if (d == 1)
                        a1[lo] += v;
                    else
                        a2[lo] += v;
                }
            }
        }

        static double A(int i, int j)
        {
            int lo = Math.Min(i, j), d = Math.Abs(i - j);
            return d == 0 ? ad[lo] : d == 1 ? a1[lo] : d == 2 ? a2[lo] : 0;
        }

        // (A x)_i: the force the batten puts on point i.
        static double Product(int i, int m)
        {
            double sum = 0;
            for (int j = Math.Max(0, i - 2); j <= Math.Min(m - 1, i + 2); j++)
                sum += A(i, j) * x[j];
            return sum;
        }

        // x with the held points at their supports and the free ones bending least.
        static void Solve(int m, double[] support)
        {
            int q = 0;
            for (int i = 0; i < m; i++)
            {
                if (held[i])
                    x[i] = support[i + 1];
                else
                    free[q++] = i;
            }
            if (q == 0)
                return;
            for (int a = 0; a < q; a++)
            {
                int i = free[a];
                double r = 0;
                for (int j = Math.Max(0, i - 2); j <= Math.Min(m - 1, i + 2); j++)
                    if (held[j])
                        r -= A(i, j) * x[j];
                rhs[a] = r;
            }
            // Cholesky of the free points' matrix (band 2): row a has l2 (column a-2), l1 (a-1), l0 (diagonal).
            for (int a = 0; a < q; a++)
            {
                int i = free[a];
                double b2 = a >= 2 ? A(i, free[a - 2]) : 0;
                double b1 = a >= 1 ? A(i, free[a - 1]) : 0;
                l2[a] = a >= 2 ? b2 / l0[a - 2] : 0;
                l1[a] = a >= 1 ? (b1 - (a >= 2 ? l2[a] * l1[a - 1] : 0)) / l0[a - 1] : 0;
                double d = ad[i] - l2[a] * l2[a] - l1[a] * l1[a];
                l0[a] = Math.Sqrt(Math.Max(d, 1e-12));
            }
            for (int a = 0; a < q; a++)
            {
                double r = rhs[a];
                if (a >= 1)
                    r -= l1[a] * rhs[a - 1];
                if (a >= 2)
                    r -= l2[a] * rhs[a - 2];
                rhs[a] = r / l0[a];
            }
            for (int a = q - 1; a >= 0; a--)
            {
                double r = rhs[a];
                if (a + 1 < q)
                    r -= l1[a + 1] * rhs[a + 1];
                if (a + 2 < q)
                    r -= l2[a + 2] * rhs[a + 2];
                rhs[a] = r / l0[a];
                x[free[a]] = rhs[a];
            }
        }
    }
}
