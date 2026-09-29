using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    // Eye-only parameterization. Keep physical pupil coordinates separate from texture coordinates:
    // flattening the iris must not silently stretch the pupil or change its depth test.
    internal static class FBXUVEyeTopology
    {
        internal static void ValidateManualTarget(FBXUVEyeMapping map)
        {
            // Manual inverse sampling needs an unambiguous physical-plane lookup in both modes.
            _ = new Surface(map.Target, map.TargetFrame, "転送先", "Eye手補正");
        }

        internal static void Apply(FBXUVEyeMapping map, FBXUVEyeBinding binding)
        {
            if (map.Iris.Loops.Count > 2)
                throw new ArgumentException("トポロジー型の虹彩は、穴なし、または瞳孔に対応する穴1つの領域を指定してください。");
            if (!map.HasPupil && map.Iris.Loops.Count != 1)
                throw new ArgumentException("画像一体型のトポロジー転送には、穴のない虹彩を指定してください。穴を埋める画像は生成しません。");

            var source = new Surface(map.Iris, map.SourceFrame, "虹彩");
            var target = new Surface(map.Target, map.TargetFrame, "転送先");
            var sourceCenter = -1; var targetCenter = -1;
            if (!map.HasPupil)
            {
                sourceCenter = source.FindRadialCenter();
                targetCenter = target.FindRadialCenter();
                // A painted pupil follows the fan pole, not the bounding-box centre. Only
                // establish a landmark when both surfaces have an unambiguous pole.
                if (sourceCenter < 0 || targetCenter < 0) sourceCenter = targetCenter = -1;
                else
                {
                    map.HasTopologyPupilCenter = true;
                    map.SourcePupilCenter = source.Plane[sourceCenter];
                    map.TargetPupilCenter = target.Plane[targetCenter];
                    map.MeasuredCenter = map.SourcePupilCenter * .5f + Vector2.one * .5f;
                }
            }
            // Both poles share parameter zero. The boundary and metric weights still
            // control the surrounding deformation; original UVs and depth stay intact.
            var sourceCoordinates = source.Solve(center: sourceCenter);
            var targetCoordinates = target.Solve(center: targetCenter);
            if (map.HasPupil)
            {
                // A hole has a fixed physical inner boundary; its virtual centre uses that frame.
                var centre = map.Iris.Loops.Count == 2 ? map.SourcePupilCenter
                    : source.Evaluate(map.SourcePupilCenter, source.Plane, sourceCoordinates);
                var targetCentre = map.SourceFrame.MapTo(map.TargetFrame, centre);
                map.TargetPupilCenter = target.Evaluate(targetCentre, targetCoordinates, target.Plane) + binding.pupilOffset * 2;
                var pupil = map.Pupil.Loops[0].Select(p => (Vector2)map.SourceFrame.Project(p)).ToArray();
                var placedPupil = pupil.Select(p => map.TargetPupilCenter + Vector2.Scale(p - map.SourcePupilCenter, map.PupilScale)).ToArray();
                foreach (var p in placedPupil) target.Evaluate(p, target.Plane, targetCoordinates);
                ValidateInsideBoundary(placedPupil, map.TargetFrame.Contour);

                if (map.Iris.Loops.Count == 2)
                {
                    var inner = map.Iris.Loops[1].Select(p => (Vector2)map.SourceFrame.Project(p)).ToArray();
                    ValidateStar(inner, map.SourcePupilCenter, "虹彩の内周");
                    ValidateStar(pupil, map.SourcePupilCenter, "瞳孔の外周");
                    sourceCoordinates = source.Solve(p =>
                    {
                        var direction = p - map.SourcePupilCenter;
                        var onPupil = map.SourcePupilCenter + direction.normalized * RayRadius(pupil, map.SourcePupilCenter, direction);
                        var physical = map.TargetPupilCenter + Vector2.Scale(onPupil - map.SourcePupilCenter, map.PupilScale);
                        var parameter = target.Evaluate(physical, target.Plane, targetCoordinates);
                        return map.TargetFrame.MapTo(map.SourceFrame, parameter);
                    });
                }
            }
            map.IrisCoordinates = source.Expand(sourceCoordinates);
            map.TargetCoordinates = target.Expand(targetCoordinates);
        }

        private static void ValidateStar(Vector2[] contour, Vector2 center, string label)
        {
            var sign = 0;
            for (var i = 0; i < contour.Length; i++)
            {
                var cross = Cross(contour[i] - center, contour[(i + 1) % contour.Length] - center);
                if (Math.Abs(cross) < 1e-10 || (sign != 0 && Math.Sign(cross) != sign))
                    throw new ArgumentException("トポロジー型: " + label + "を瞳孔中心から対応付けできません。");
                sign = Math.Sign(cross);
            }
        }

        private static float RayRadius(Vector2[] contour, Vector2 center, Vector2 direction)
        {
            direction = direction.normalized;
            var distance = double.PositiveInfinity;
            for (var i = 0; i < contour.Length; i++)
            {
                var a = contour[i] - center; var edge = contour[(i + 1) % contour.Length] - contour[i];
                var denominator = Cross(direction, edge);
                if (Math.Abs(denominator) < 1e-12) continue;
                var t = Cross(a, edge) / denominator; var u = Cross(a, direction) / denominator;
                if (t > 0 && u >= -1e-6 && u <= 1 + 1e-6) distance = Math.Min(distance, t);
            }
            if (double.IsInfinity(distance)) throw new ArgumentException("トポロジー型: 瞳孔の境界を対応付けできません。");
            return (float)distance;
        }

        private static void ValidateInsideBoundary(Vector2[] pupil, Vector2[] outer)
        {
            for (var i = 0; i < pupil.Length; i++)
                for (var j = 0; j < outer.Length; j++)
                {
                    var a = pupil[i]; var b = pupil[(i + 1) % pupil.Length];
                    var c = outer[j]; var d = outer[(j + 1) % outer.Length];
                    if (Cross(b-a,c-a)*Cross(b-a,d-a) < -1e-16 && Cross(d-c,a-c)*Cross(d-c,b-c) < -1e-16)
                        throw new ArgumentException("トポロジー型: 補正した瞳孔が目の外周を越えています。位置・大きさを調整してください。");
                }
        }

        private static double Cross(Vector2 a, Vector2 b) => (double)a.x * b.y - (double)a.y * b.x;
        private static Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.RoundToInt(p.x * 1000000), Mathf.RoundToInt(p.y * 1000000), Mathf.RoundToInt(p.z * 1000000));

        private sealed class Surface
        {
            internal readonly Vector2[] Plane;
            private readonly Vector3[] projected;
            private readonly int[] cornerIds;
            private readonly int[] boundary;
            private readonly Dictionary<int, double>[] weights;
            private readonly double[] diagonal;
            private readonly string label;
            private readonly string method;

            internal Surface(FBXUVEyeGeometry geometry, FBXUVEyeFrame frame, string label, string method = "トポロジー型")
            {
                this.label = label;
                this.method = method;
                var ids = new Dictionary<Vector3Int, int>(); var positions = new List<Vector3>();
                cornerIds = new int[geometry.Positions.Length];
                for (var i = 0; i < cornerIds.Length; i++)
                {
                    var p = geometry.Positions[i]; var key = Key(p);
                    if (!ids.TryGetValue(key, out var index)) { index = positions.Count; ids.Add(key, index); positions.Add(p); }
                    cornerIds[i] = index;
                }
                projected = positions.Select(frame.Project).ToArray();
                Plane = projected.Select(p => (Vector2)p).ToArray();
                boundary = Enumerable.Repeat(-1, positions.Count).ToArray();
                for (var loop = 0; loop < geometry.Loops.Count; loop++)
                    foreach (var p in geometry.Loops[loop]) boundary[ids[Key(p)]] = loop;
                weights = Enumerable.Range(0, positions.Count).Select(_ => new Dictionary<int, double>()).ToArray();
                diagonal = new double[positions.Count];
                for (var i = 0; i < cornerIds.Length; i += 3)
                    for (var k = 0; k < 3; k++)
                    {
                        var a = cornerIds[i+k]; var b = cornerIds[i+(k+1)%3]; var c = cornerIds[i+(k+2)%3];
                        var weight = Cotangent(positions[a]-positions[c], positions[b]-positions[c]) * .5;
                        AddWeight(a,b,weight); AddWeight(b,a,weight);
                    }
                foreach (var d in diagonal) if (double.IsNaN(d) || double.IsInfinity(d) || d <= 0)
                    throw Error("三角形の形状が不安定で座標を計算できません。");
                Validate(Plane);
            }

            private void AddWeight(int a, int b, double weight)
            {
                weights[a].TryGetValue(b, out var old); weights[a][b] = old + weight; diagonal[a] += weight;
            }

            private double Cotangent(Vector3 a, Vector3 b)
            {
                var x=(double)a.y*b.z-(double)a.z*b.y; var y=(double)a.z*b.x-(double)a.x*b.z; var z=(double)a.x*b.y-(double)a.y*b.x;
                var area=Math.Sqrt(x*x+y*y+z*z);
                if (area <= 1e-24) throw Error("退化した三角形があります。");
                return ((double)a.x*b.x+(double)a.y*b.y+(double)a.z*b.z)/area;
            }

            internal int FindRadialCenter()
            {
                var interior = Enumerable.Range(0, Plane.Length).Where(i => boundary[i] < 0)
                    .OrderByDescending(i => weights[i].Count).ToArray();
                if (interior.Length == 0) return -1;
                // A single interior fan is explicit even at low resolution. Otherwise
                // require a high-valence pole clearly distinct from regular triangulation
                // (usually 4-8 edges). Never break a tie by vertex order or UV position.
                if (interior.Length == 1) return interior[0];
                var degree = weights[interior[0]].Count;
                return degree >= 8 && degree >= weights[interior[1]].Count * 1.5
                    ? interior[0] : -1;
            }

            internal Vector2[] Solve(Func<Vector2, Vector2> innerBoundary = null, int center = -1)
            {
                var result = (Vector2[])Plane.Clone();
                for (var i=0;i<result.Length;i++)
                    if (boundary[i]>0 && innerBoundary!=null) result[i]=innerBoundary(Plane[i]);
                if (center >= 0) result[center] = Vector2.zero;
                var interior=Enumerable.Range(0,result.Length).Where(i=>boundary[i]<0 && i!=center).ToArray();
                var indices=Enumerable.Repeat(-1,result.Length).ToArray();
                for(var i=0;i<interior.Length;i++) indices[interior[i]]=i;
                for(var axis=0;axis<2;axis++)
                {
                    var rhs=new double[interior.Length]; var x=new double[interior.Length];
                    for(var i=0;i<interior.Length;i++)
                    {
                        var v=interior[i]; x[i]=Plane[v][axis];
                        foreach(var edge in weights[v]) if(indices[edge.Key]<0) rhs[i]+=edge.Value*result[edge.Key][axis];
                    }
                    SolveLinear(interior,indices,rhs,x);
                    for(var i=0;i<interior.Length;i++) result[interior[i]][axis]=(float)x[i];
                }
                Validate(result);
                return result;
            }

            // Cotangent FEM Laplacian is symmetric positive definite with fixed boundaries,
            // even when individual obtuse-angle edge weights are negative. Do not clamp them.
            private void SolveLinear(int[] interior, int[] indices, double[] rhs, double[] x)
            {
                var count=x.Length; if(count==0) return;
                var residual=new double[count]; var direction=new double[count]; var product=new double[count];
                void Multiply(double[] input,double[] output)
                {
                    for(var i=0;i<count;i++)
                    {
                        var v=interior[i]; var sum=diagonal[v]*input[i];
                        foreach(var edge in weights[v]) { var j=indices[edge.Key]; if(j>=0) sum-=edge.Value*input[j]; }
                        output[i]=sum;
                    }
                }
                Multiply(x,product); double rz=0, rhsNorm=0, norm=0;
                for(var i=0;i<count;i++)
                {
                    residual[i]=rhs[i]-product[i]; direction[i]=residual[i]/diagonal[interior[i]];
                    rz+=residual[i]*direction[i]; norm+=residual[i]*residual[i]; rhsNorm+=rhs[i]*rhs[i];
                }
                var tolerance=1e-18*Math.Max(1,rhsNorm);
                for(var iteration=0;iteration<Math.Min(10000,Math.Max(64,count*2));iteration++)
                {
                    if(norm<=tolerance) return;
                    Multiply(direction,product); double denominator=0;
                    for(var i=0;i<count;i++) denominator+=direction[i]*product[i];
                    if(denominator<=0 || double.IsNaN(denominator)) break;
                    var alpha=rz/denominator; double next=0; norm=0;
                    for(var i=0;i<count;i++)
                    {
                        x[i]+=alpha*direction[i]; residual[i]-=alpha*product[i];
                        norm+=residual[i]*residual[i]; next+=residual[i]*residual[i]/diagonal[interior[i]];
                    }
                    if(norm<=tolerance) return;
                    var beta=next/rz;
                    for(var i=0;i<count;i++) direction[i]=residual[i]/diagonal[interior[i]]+beta*direction[i];
                    rz=next;
                }
                throw Error("座標計算が収束しません。領域または補正値を確認してください。");
            }

            internal Vector3[] Expand(Vector2[] coordinates)
            {
                return cornerIds.Select(i=>new Vector3(coordinates[i].x,coordinates[i].y,projected[i].z)).ToArray();
            }

            internal Vector2 Evaluate(Vector2 p, Vector2[] from, Vector2[] to)
            {
                for(var i=0;i<cornerIds.Length;i+=3)
                {
                    var a=cornerIds[i];var b=cornerIds[i+1];var c=cornerIds[i+2];
                    var den=Cross(from[b]-from[a],from[c]-from[a]);
                    var wb=Cross(p-from[a],from[c]-from[a])/den;var wc=Cross(from[b]-from[a],p-from[a])/den;var wa=1-wb-wc;
                    if(Math.Min(wa,Math.Min(wb,wc)) < -1e-5) continue;
                    return to[a]*(float)wa+to[b]*(float)wb+to[c]*(float)wc;
                }
                throw Error("瞳孔または対応点が目の領域外にあります。位置・大きさとアイランドを確認してください。");
            }

            private ArgumentException Error(string message) => new ArgumentException(method+"（"+label+"）: "+message);

            private void Validate(Vector2[] points)
            {
                foreach(var p in points) if(!FBXUVEyeGeometry.Finite(p.x)||!FBXUVEyeGeometry.Finite(p.y)) throw Error("座標が有限値ではありません。");
                var count=cornerIds.Length/3;var min=new Vector2[count];var max=new Vector2[count];
                for(var t=0;t<count;t++)
                {
                    var a=cornerIds[t*3];var b=cornerIds[t*3+1];var c=cornerIds[t*3+2];
                    var old=Cross(Plane[b]-Plane[a],Plane[c]-Plane[a]);var area=Cross(points[b]-points[a],points[c]-points[a]);
                    var ratio=area/old;
                    if(Math.Abs(area)<1e-10 || ratio<1e-6 || ratio>1e6) throw Error("面の反転・退化または極端な伸縮が発生します。補正を弱めてください。");
                    min[t]=Vector2.Min(points[a],Vector2.Min(points[b],points[c])); max[t]=Vector2.Max(points[a],Vector2.Max(points[b],points[c]));
                }
                var ordered=Enumerable.Range(0,count).OrderBy(t=>min[t].x).ToArray();
                for(var i=0;i<count;i++)
                    for(var j=i+1;j<count;j++)
                    {
                        var a=ordered[i];var b=ordered[j]; if(min[b].x>=max[a].x-1e-8f) break;
                        if(min[b].y>=max[a].y-1e-8f||min[a].y>=max[b].y-1e-8f) continue;
                        if(IntersectionArea(points,a,b)>1e-9) throw Error("面が重なります。別の領域または補正値を指定してください。");
                    }
            }

            private struct Point
            {
                internal double X,Y;
                internal Point(Vector2 v) { X=v.x;Y=v.y; }
                internal Point(double x,double y) { X=x;Y=y; }
            }
            private static double Side(Point a,Point b,Point c) => (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
            private double IntersectionArea(Vector2[] points,int first,int second)
            {
                var polygon=new List<Point>(6);var clip=new Point[3];
                for(var k=0;k<3;k++) { polygon.Add(new Point(points[cornerIds[first*3+k]]));clip[k]=new Point(points[cornerIds[second*3+k]]); }
                var orientation=Math.Sign(Side(clip[0],clip[1],clip[2]));
                for(var edge=0;edge<3 && polygon.Count>=3;edge++)
                {
                    var output=new List<Point>(6);var a=clip[edge];var b=clip[(edge+1)%3];
                    for(var i=0;i<polygon.Count;i++)
                    {
                        var p=polygon[i];var q=polygon[(i+1)%polygon.Count];var sp=Side(a,b,p)*orientation;var sq=Side(a,b,q)*orientation;
                        if(sp>=0) output.Add(p);
                        if((sp>=0)!=(sq>=0)) { var t=sp/(sp-sq);output.Add(new Point(p.X+(q.X-p.X)*t,p.Y+(q.Y-p.Y)*t)); }
                    }
                    polygon=output;
                }
                double area=0;for(var i=1;i+1<polygon.Count;i++) area+=Side(polygon[0],polygon[i],polygon[i+1]);
                return Math.Abs(area)*.5;
            }
        }
    }
}
