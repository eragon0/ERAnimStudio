// ERAnimStudio: the left-hand clearance check's math, in double precision (the measure's crossings are judged to the millimetre).
// Quaternions are Havok's (x, y, z, w); a rotation turns a vector by q v q*.
using System;

namespace DSAnimStudio.LeftClear
{
    public struct D3
    {
        public double X, Y, Z;

        public D3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly D3 Zero = new D3(0, 0, 0);

        public static D3 operator +(D3 a, D3 b) => new D3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static D3 operator -(D3 a, D3 b) => new D3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static D3 operator -(D3 a) => new D3(-a.X, -a.Y, -a.Z);
        public static D3 operator *(D3 a, double s) => new D3(a.X * s, a.Y * s, a.Z * s);
        public static D3 operator *(double s, D3 a) => new D3(a.X * s, a.Y * s, a.Z * s);
        public static D3 operator /(D3 a, double s) => new D3(a.X / s, a.Y / s, a.Z / s);

        public double this[int i] => i == 0 ? X : i == 1 ? Y : Z;

        public static double Dot(D3 a, D3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static D3 Cross(D3 a, D3 b) => new D3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z);
        public double LengthSquared() => X * X + Y * Y + Z * Z;
        public static double Distance(D3 a, D3 b) => (a - b).Length();

        /// <summary>The unit vector (a zero vector stays zero, as numpy's v / max(|v|, 1e-12)).</summary>
        public D3 Unit()
        {
            double l = Math.Max(Length(), 1e-12);
            return new D3(X / l, Y / l, Z / l);
        }

        public static D3 From(System.Numerics.Vector3 v) => new D3(v.X, v.Y, v.Z);
        public System.Numerics.Vector3 ToFloat() => new System.Numerics.Vector3((float)X, (float)Y, (float)Z);

        public override string ToString() => $"({X:0.####}, {Y:0.####}, {Z:0.####})";
    }

    public struct DQ
    {
        public double X, Y, Z, W;

        public DQ(double x, double y, double z, double w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public static readonly DQ Identity = new DQ(0, 0, 0, 1);

        /// <summary>a * b: b first, then a.</summary>
        public static DQ operator *(DQ a, DQ b) => new DQ(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        public DQ Conj() => new DQ(-X, -Y, -Z, W);
        public DQ Negate() => new DQ(-X, -Y, -Z, -W);
        public static double Dot(DQ a, DQ b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

        public DQ Normalized()
        {
            double l = Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
            return l > 0 ? new DQ(X / l, Y / l, Z / l, W / l) : Identity;
        }

        /// <summary>The vector turned by this rotation (havok_anim.qrot's formula: v + w t + u x t, t = 2 u x v).</summary>
        public D3 Rotate(D3 v)
        {
            double tx = 2 * (Y * v.Z - Z * v.Y), ty = 2 * (Z * v.X - X * v.Z), tz = 2 * (X * v.Y - Y * v.X);
            return new D3(v.X + W * tx + (Y * tz - Z * ty), v.Y + W * ty + (Z * tx - X * tz), v.Z + W * tz + (X * ty - Y * tx));
        }

        public static DQ AxisAngle(D3 unitAxis, double degrees)
        {
            double h = degrees * Math.PI / 360.0;
            double s = Math.Sin(h);
            return new DQ(unitAxis.X * s, unitAxis.Y * s, unitAxis.Z * s, Math.Cos(h));
        }

        /// <summary>The angle between two rotations, degrees.</summary>
        public static double AngleBetween(DQ a, DQ b) => 2 * Math.Acos(Math.Min(1.0, Math.Abs(Dot(a.Normalized(), b.Normalized())))) * 180.0 / Math.PI;

        /// <summary>The rotation as a matrix whose columns are the turned x, y, z axes.</summary>
        public M3 ToM3()
        {
            double x = X, y = Y, z = Z, w = W;
            return new M3(
                1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
                2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
                2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y));
        }

        public static DQ From(System.Numerics.Quaternion q) => new DQ(q.X, q.Y, q.Z, q.W);
        public System.Numerics.Quaternion ToFloat() => new System.Numerics.Quaternion((float)X, (float)Y, (float)Z, (float)W);
    }

    /// <summary>A 3 x 3 matrix, row-major (M[r, c]); applied to column vectors.</summary>
    public struct M3
    {
        public double M00, M01, M02, M10, M11, M12, M20, M21, M22;

        public M3(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
        {
            M00 = m00; M01 = m01; M02 = m02;
            M10 = m10; M11 = m11; M12 = m12;
            M20 = m20; M21 = m21; M22 = m22;
        }

        public static readonly M3 Identity = new M3(1, 0, 0, 0, 1, 0, 0, 0, 1);

        public static M3 FromColumns(D3 c0, D3 c1, D3 c2) => new M3(c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z);
        public static M3 FromRows(D3 r0, D3 r1, D3 r2) => new M3(r0.X, r0.Y, r0.Z, r1.X, r1.Y, r1.Z, r2.X, r2.Y, r2.Z);

        public D3 Column(int c) => c == 0 ? new D3(M00, M10, M20) : c == 1 ? new D3(M01, M11, M21) : new D3(M02, M12, M22);
        public D3 Row(int r) => r == 0 ? new D3(M00, M01, M02) : r == 1 ? new D3(M10, M11, M12) : new D3(M20, M21, M22);

        public static D3 operator *(M3 m, D3 v) => new D3(
            m.M00 * v.X + m.M01 * v.Y + m.M02 * v.Z,
            m.M10 * v.X + m.M11 * v.Y + m.M12 * v.Z,
            m.M20 * v.X + m.M21 * v.Y + m.M22 * v.Z);

        public static M3 operator *(M3 a, M3 b) => FromColumns(a * b.Column(0), a * b.Column(1), a * b.Column(2));

        public M3 Transpose() => new M3(M00, M10, M20, M01, M11, M21, M02, M12, M22);

        /// <summary>v^T M: the vector's coordinates along the matrix's columns (numpy's v @ M).</summary>
        public D3 TransposeTimes(D3 v) => new D3(
            M00 * v.X + M10 * v.Y + M20 * v.Z,
            M01 * v.X + M11 * v.Y + M21 * v.Z,
            M02 * v.X + M12 * v.Y + M22 * v.Z);
    }

    public static class LcLinear
    {
        /// <summary>
        /// The principal axes of points about their mean, the most spread first (what numpy's SVD of the centred points gives as rows of
        /// vt): the eigenvectors of their scatter matrix by Jacobi's method. The sign of each axis is free; it is fixed here so the axis'
        /// largest component (by size) is positive.
        /// </summary>
        public static D3[] PrincipalAxes(D3[] pts, D3 centre)
        {
            double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0;
            foreach (var p in pts)
            {
                var d = p - centre;
                a00 += d.X * d.X; a01 += d.X * d.Y; a02 += d.X * d.Z;
                a11 += d.Y * d.Y; a12 += d.Y * d.Z; a22 += d.Z * d.Z;
            }
            var (vals, vecs) = SymmetricEigen(new double[,] { { a00, a01, a02 }, { a01, a11, a12 }, { a02, a12, a22 } });
            var order = new[] { 0, 1, 2 };
            Array.Sort(order, (i, j) => vals[j].CompareTo(vals[i]));
            var axes = new D3[3];
            for (int k = 0; k < 3; k++)
            {
                int c = order[k];
                var v = new D3(vecs[0, c], vecs[1, c], vecs[2, c]).Unit();
                double big = Math.Abs(v.X) >= Math.Abs(v.Y) && Math.Abs(v.X) >= Math.Abs(v.Z) ? v.X : Math.Abs(v.Y) >= Math.Abs(v.Z) ? v.Y : v.Z;
                axes[k] = big < 0 ? -v : v;
            }
            return axes;
        }

        /// <summary>Eigenvalues and eigenvectors (columns) of a symmetric 3 x 3 matrix, by cyclic Jacobi rotations.</summary>
        public static (double[] values, double[,] vectors) SymmetricEigen(double[,] a)
        {
            var m = (double[,])a.Clone();
            var v = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 64; sweep++)
            {
                double off = Math.Abs(m[0, 1]) + Math.Abs(m[0, 2]) + Math.Abs(m[1, 2]);
                double scale = Math.Abs(m[0, 0]) + Math.Abs(m[1, 1]) + Math.Abs(m[2, 2]);
                if (off <= 1e-15 * Math.Max(scale, 1e-300))
                    break;
                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(m[p, q]) < 1e-300)
                            continue;
                        double theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0)
                            t = 1;
                        double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < 3; k++)
                        {
                            double mkp = m[k, p], mkq = m[k, q];
                            m[k, p] = c * mkp - s * mkq;
                            m[k, q] = s * mkp + c * mkq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double mpk = m[p, k], mqk = m[q, k];
                            m[p, k] = c * mpk - s * mqk;
                            m[q, k] = s * mpk + c * mqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq;
                            v[k, q] = s * vkp + c * vkq;
                        }
                    }
                }
            }
            return (new[] { m[0, 0], m[1, 1], m[2, 2] }, v);
        }
    }
}
