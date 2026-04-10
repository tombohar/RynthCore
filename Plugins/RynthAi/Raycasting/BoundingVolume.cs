using System;

namespace NexSuite.Plugins.RynthAi.Raycasting
{
    /// <summary>
    /// Bounding volume for collision detection
    /// Supports ray-volume intersection testing for raycasting
    /// </summary>
    public class BoundingVolume
    {
        public enum VolumeType
        {
            Sphere = 1,
            Cylinder = 2,
            Ellipsoid = 3,
            Polygon = 4,
            AxisAlignedBox = 5,
            Torus = 6
        }

        public VolumeType Type { get; set; }
        public Vector3 Center { get; set; } // X, Y, Z
        public Vector3 Dimensions { get; set; } // Radius, Height, etc
        public Vector3[] Vertices { get; set; } // For polygons
        
        // Additional properties for geometry loading
        public Vector3 Min { get; set; } // Minimum bounds
        public Vector3 Max { get; set; } // Maximum bounds
        public bool IsDoor { get; set; } // Whether this is a door (passable)

        public BoundingVolume()
        {
            Center = new Vector3(0, 0, 0);
            Dimensions = new Vector3(0, 0, 0);
            Min = new Vector3(0, 0, 0);
            Max = new Vector3(0, 0, 0);
            IsDoor = false;
        }

        /// <summary>
        /// Test if a ray intersects this volume
        /// Returns true if collision detected
        /// </summary>
        public bool RayIntersect(Vector3 rayStart, Vector3 rayDir, float maxDist, out float hitDist)
        {
            hitDist = float.MaxValue;

            try
            {
                switch (Type)
                {
                    case VolumeType.Sphere:
                        return RayIntersectSphere(rayStart, rayDir, maxDist, out hitDist);
                    case VolumeType.Cylinder:
                        return RayIntersectCylinder(rayStart, rayDir, maxDist, out hitDist);
                    case VolumeType.AxisAlignedBox:
                        return RayIntersectAABB(rayStart, rayDir, maxDist, out hitDist);
                    case VolumeType.Polygon:
                        return RayIntersectPolygon(rayStart, rayDir, maxDist, out hitDist);
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Overload to accept float arrays for backwards compatibility
        /// </summary>
        public bool RayIntersect(float[] rayStart, float[] rayDir, float maxDist, out float hitDist)
        {
            return RayIntersect(
                Vector3.FromArray(rayStart),
                Vector3.FromArray(rayDir),
                maxDist,
                out hitDist
            );
        }

        /// <summary>
        /// Ray-Sphere intersection
        /// </summary>
        private bool RayIntersectSphere(Vector3 rayStart, Vector3 rayDir, float maxDist, out float hitDist)
        {
            hitDist = float.MaxValue;

            Vector3 oc = rayStart - Center;

            float a = Vector3.Dot(rayDir, rayDir);
            float b = 2.0f * Vector3.Dot(oc, rayDir);
            float c = Vector3.Dot(oc, oc) - (Dimensions.X * Dimensions.X);

            float discriminant = b * b - 4 * a * c;
            if (discriminant < 0)
                return false;

            float sqrt_disc = (float)Math.Sqrt(discriminant);
            float t1 = (-b - sqrt_disc) / (2 * a);
            float t2 = (-b + sqrt_disc) / (2 * a);

            if (t1 > 0 && t1 < maxDist)
            {
                hitDist = t1;
                return true;
            }

            // Ray starts inside sphere (t1 < 0, t2 > 0)
            if (t1 <= 0 && t2 > 0 && t2 < maxDist)
            {
                hitDist = 0;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Ray-Cylinder intersection
        /// </summary>
        private bool RayIntersectCylinder(Vector3 rayStart, Vector3 rayDir, float maxDist, out float hitDist)
        {
            hitDist = float.MaxValue;
            return false;
        }

        /// <summary>
        /// Ray-AABB intersection
        /// </summary>
        private bool RayIntersectAABB(Vector3 rayStart, Vector3 rayDir, float maxDist, out float hitDist)
        {
            hitDist = float.MaxValue;

            float tmin = 0, tmax = maxDist;

            for (int i = 0; i < 3; i++)
            {
                float rayStartCoord = i == 0 ? rayStart.X : (i == 1 ? rayStart.Y : rayStart.Z);
                float rayDirCoord = i == 0 ? rayDir.X : (i == 1 ? rayDir.Y : rayDir.Z);
                float minCoord = i == 0 ? Min.X : (i == 1 ? Min.Y : Min.Z);
                float maxCoord = i == 0 ? Max.X : (i == 1 ? Max.Y : Max.Z);

                if (Math.Abs(rayDirCoord) < 0.00001f)
                {
                    if (rayStartCoord < minCoord || rayStartCoord > maxCoord)
                        return false;
                }
                else
                {
                    float t1 = (minCoord - rayStartCoord) / rayDirCoord;
                    float t2 = (maxCoord - rayStartCoord) / rayDirCoord;

                    if (t1 > t2)
                    {
                        float temp = t1;
                        t1 = t2;
                        t2 = temp;
                    }

                    tmin = Math.Max(tmin, t1);
                    tmax = Math.Min(tmax, t2);

                    if (tmin > tmax)
                        return false;
                }
            }

            if (tmin > 0 && tmin < maxDist)
            {
                hitDist = tmin;
                return true;
            }

            // Ray starts inside the box (tmin <= 0 but tmax > 0)
            // This means the player is inside/overlapping the obstacle
            if (tmin <= 0 && tmax > 0 && tmax < maxDist)
            {
                hitDist = 0;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Ray-Polygon intersection
        /// </summary>
        private bool RayIntersectPolygon(Vector3 rayStart, Vector3 rayDir, float maxDist, out float hitDist)
        {
            hitDist = float.MaxValue;
            if (Vertices == null || Vertices.Length < 3)
                return false;

            for (int i = 0; i < Vertices.Length - 2; i++)
            {
                if (RayIntersectTriangle(rayStart, rayDir, Vertices[0], Vertices[i + 1], Vertices[i + 2], out float t))
                {
                    if (t >= 0 && t < maxDist && t < hitDist)
                    {
                        hitDist = t;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Ray-Triangle intersection (Möller-Trumbore algorithm)
        /// </summary>
        private bool RayIntersectTriangle(Vector3 rayStart, Vector3 rayDir, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
        {
            t = float.MaxValue;
            const float EPSILON = 0.0000001f;

            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;

            Vector3 h = Vector3.Cross(rayDir, edge2);
            float a = Vector3.Dot(edge1, h);

            if (a > -EPSILON && a < EPSILON)
                return false;

            float f = 1.0f / a;
            Vector3 s = rayStart - v0;

            float u = f * Vector3.Dot(s, h);
            if (u < 0 || u > 1)
                return false;

            Vector3 q = Vector3.Cross(s, edge1);
            float v = f * Vector3.Dot(rayDir, q);

            if (v < 0 || u + v > 1)
                return false;

            t = f * Vector3.Dot(edge2, q);
            return t >= 0;
        }
    }
}
