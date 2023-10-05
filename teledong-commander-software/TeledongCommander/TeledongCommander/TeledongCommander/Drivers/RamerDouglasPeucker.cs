/*
https://github.com/BobLd/RamerDouglasPeuckerNetV2/tree/master

MIT License

Copyright (c) 2019 BobLd

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Collections.Generic;

namespace RamerDouglasPeuckerNetV2
{
    /***************************************************************************
      * Ramer–Douglas–Peucker algorithm
      * The purpose of the algorithm is, given a curve composed of line segments 
      * (which is also called a Polyline in some contexts), to find a similar 
      * curve with fewer points. The algorithm defines 'dissimilar' based on the 
      * maximum distance between the original curve and the simplified curve 
      * (i.e., the Hausdorff distance between the curves). The simplified curve 
      * consists of a subset of the points that defined the original curve.
      * [https://en.wikipedia.org/wiki/Ramer%E2%80%93Douglas%E2%80%93Peucker_algorithm]
      * 
      * The pseudo-code is available on the wikipedia page.
      * In this implementation, we use the perpendicular distance. In order to 
      * make the algorithm faster, we consider the squared distance (and epsilon) 
      * so that we avoid using the 'abs' and 'sqrt' in the distance computation.
      * We also split the computation of the distance so that we put in the 'for 
      * loop' only what is needed.
      * 
      * The non-parametric version follows 'A novel framework for making dominant 
      * point detection methods non-parametric' by Prasad, Leung, Quek, and Cho.
      **************************************************************************/

    /// <summary>
    /// The purpose of the algorithm is, given a curve composed of line segments
    /// to find a similar curve with fewer points.
    /// </summary>
    public static class RamerDouglasPeucker
    {
        /// <summary>
        /// Uses the Ramer Douglas Peucker algorithm to reduce the number of points.
        /// </summary>
        /// <param name="points">The points.</param>
        /// <param name="epsilon">The tolerance.</param>
        /// <returns></returns>
        public static List<TeledongCommander.ViewModels.StrokerPoint> Reduce(List<TeledongCommander.ViewModels.StrokerPoint> points, double epsilon)
        {
            if (points == null || points.Count < 3) 
                return points ?? new List<TeledongCommander.ViewModels.StrokerPoint>();
            if (double.IsInfinity(epsilon) || double.IsNaN(epsilon)) return points;
            epsilon *= epsilon; // we use squared distance
            if (epsilon <= double.Epsilon) 
                return points;

            List<TeledongCommander.ViewModels.StrokerPoint> firsts = new List<TeledongCommander.ViewModels.StrokerPoint>();
            while (points[0].Equals(points[points.Count - 1]))
            {
                firsts.Add(points[0]);
                points.RemoveAt(0);
            }

            firsts.AddRange(reduce(points, epsilon));
            return firsts;
        }

        private static List<TeledongCommander.ViewModels.StrokerPoint> reduce(List<TeledongCommander.ViewModels.StrokerPoint> points, double epsilon)
        {
            double dmax = 0;
            int index = 0;

            TeledongCommander.ViewModels.StrokerPoint point1 = points[0];
            TeledongCommander.ViewModels.StrokerPoint point2 = points[points.Count - 1];

            double distXY = point1.X * point2.Y - point2.X * point1.Y;
            double distX = point2.X - point1.X;
            double distY = point2.Y - point1.Y;
            double denominator = distX * distX + distY * distY;

            for (int i = 1; i < (points.Count - 2); i++) // -2 or -1?
            {
                // Compute perpendicular distance squared
                var current = points[i];
                double numerator = distXY + distX * current.Y - distY * current.X;
                double d = (numerator / denominator) * numerator;

                if (d > dmax)
                {
                    index = i;
                    dmax = d;
                }
            }

            // If max distance is greater than epsilon, recursively simplify
            if (dmax > epsilon)
            {
                // Recursive call
                var recResults1 = reduce(points.GetRange(0, index + 1), epsilon);
                var recResults2 = reduce(points.GetRange(index, points.Count - index), epsilon);

                // Build the result list
                recResults1.RemoveAt(recResults1.Count - 1);
                recResults1.AddRange(recResults2);
                return recResults1;
            }
            else
            {
                return new List<TeledongCommander.ViewModels.StrokerPoint> { point1, point2 };
            }
        }

        /// <summary>
        /// Uses the non-parametric Ramer Douglas Peucker algorithm to reduce the number of points.
        /// </summary>
        /// <param name="points">The points.</param>
        /// <returns></returns>
        public static List<TeledongCommander.ViewModels.StrokerPoint> Reduce(List<TeledongCommander.ViewModels.StrokerPoint> points)
        {
            List<TeledongCommander.ViewModels.StrokerPoint> firsts = new List<TeledongCommander.ViewModels.StrokerPoint>();
            while (points[0].Equals(points[points.Count - 1]))
            {
                firsts.Add(points[0]);
                points.RemoveAt(0);
            }

            firsts.AddRange(reduceNP(points));
            return firsts;
        }

        /// <summary>
        /// Non-parametric Ramer Douglas Peucker algorithm.
        /// </summary>
        /// <param name="points"></param>
        /// <returns></returns>
        private static List<TeledongCommander.ViewModels.StrokerPoint> reduceNP(List<TeledongCommander.ViewModels.StrokerPoint> points)
        {
            double dmax = 0;
            int index = 0;

            TeledongCommander.ViewModels.StrokerPoint point1 = points[0];
            TeledongCommander.ViewModels.StrokerPoint point2 = points[points.Count - 1];

            double distXY = point1.X * point2.Y - point2.X * point1.Y;
            double distX = point2.X - point1.X;
            double distY = point2.Y - point1.Y;
            double denominator = distX * distX + distY * distY;
            double epsilon = ComputeEpsilon(distX, distY);

            for (int i = 1; i < (points.Count - 2); i++) // -2 or -1?
            {
                // Compute perpendicular distance squared
                var current = points[i];
                double numerator = distXY + distX * current.Y - distY * current.X;
                double d = (numerator / denominator) * numerator;

                if (d > dmax)
                {
                    index = i;
                    dmax = d;
                }
            }

            // If max distance is greater than epsilon, recursively simplify
            if (dmax > epsilon)
            {
                // Recursive call
                var recResults1 = reduceNP(points.GetRange(0, index + 1));
                var recResults2 = reduceNP(points.GetRange(index, points.Count - index));

                // Build the result list
                recResults1.RemoveAt(recResults1.Count - 1);
                recResults1.AddRange(recResults2);
                return recResults1;
            }
            else
            {
                return new List<TeledongCommander.ViewModels.StrokerPoint> { point1, point2 };
            }
        }

        /// <summary>
        /// Follows 'A novel framework for making dominant point detection methods non-parametric'
        /// by Prasad, Leung, Quek, and Cho.
        /// </summary>
        /// <param name="distX">point2.X - point1.X</param>
        /// <param name="distY">point2.Y - point1.Y</param>
        /// <returns></returns>
        private static double ComputeEpsilon(double distX, double distY)
        {
            double m = distY / distX;                                                // slope
            double s = System.Math.Sqrt((double)(distX * distX + distY * distY));   // distance
            double invS = 1.0 / s;
            double phi = System.Math.Atan(m);
            double cosPhi = System.Math.Cos(phi);
            double sinPHi = System.Math.Sin(phi);
            double tmax = invS * (System.Math.Abs(cosPhi) + System.Math.Abs(sinPHi));
            double poly = 1 - tmax + tmax * tmax;
            double partialPhi = System.Math.Max(System.Math.Atan(invS * System.Math.Abs(sinPHi + cosPhi) * poly),
                                                System.Math.Atan(invS * System.Math.Abs(sinPHi - cosPhi) * poly));
            double dmax = (double)(s * partialPhi);
            return dmax * dmax; // we use squared distance
        }
    }


    /*public struct TeledongCommander.ViewModels.StrokerPoint
    {
        public TeledongCommander.ViewModels.StrokerPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; set; }
        public double Y { get; set; }

        public override string ToString()
        {
            return X.ToString() + ", " + Y.ToString();
        }

        public override bool Equals(object obj)
        {
            if (obj is TeledongCommander.ViewModels.StrokerPoint point)
            {
                if (this.X != point.X || this.Y != point.Y) return false;
                return true;
            }
            return false;
        }
    }*/
}
