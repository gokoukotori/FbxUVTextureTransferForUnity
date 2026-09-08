using System;
using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    // Piecewise affine interpolation of the unchanged control pairs. Construction
    // fails rather than silently moving controls or accepting overlapping images.
    public sealed class FBXUVMakeupExactMap
    {
        private struct Point
        {
            internal double x, y;
            internal Point(double x, double y) { this.x = x; this.y = y; }
            internal Point(Vector2 p) { x = p.x; y = p.y; }
            internal Vector2 Vector => new Vector2((float)x, (float)y);
        }
        private struct Triangle
        {
            internal int a, b, c;
            internal Triangle(int a, int b, int c) { this.a = a; this.b = b; this.c = c; }
            internal int this[int i] => i == 0 ? a : i == 1 ? b : c;
            internal bool Has(int v) => a == v || b == v || c == v;
        }
        private readonly List<Point> target = new List<Point>();
        private readonly List<Point> source = new List<Point>();
        private readonly List<Triangle> triangles = new List<Triangle>();
        private bool boundaryMap;
        public int TriangleCount => triangles.Count;
        private static double Cross(Point a, Point b, Point c) => (b.x-a.x)*(c.y-a.y)-(b.y-a.y)*(c.x-a.x);
        private static double Area(List<Point> p, Triangle t) => Cross(p[t.a],p[t.b],p[t.c]);

        public static FBXUVMakeupExactMap Create(IReadOnlyList<FBXUVMakeupLandmark> controls,
            IReadOnlyList<FBXUVTriangle> region)
        {
            if (!FBXUVMakeupWarp.TryCreate(controls, out _, out var reason)) throw new ArgumentException(reason);
            var map = new FBXUVMakeupExactMap();
            foreach (var p in controls) { map.target.Add(new Point(p.targetUv)); map.source.Add(new Point(p.sourceUv)); }
            map.ExtendAffineBoundary();
            map.Triangulate(); map.Repair(); map.Validate();
            if (region == null || region.Count == 0) throw new ArgumentException("転送先の顔領域がありません。");
            foreach (var t in region)
                for (var i=0;i<3;i++)
                    if (t == null || !map.TryEvaluate(t.GetPoint(i), out _))
                        throw new ArgumentException("対応点が顔領域を囲んでいません。画像四隅の対応点を追加してください。");
            return map;
        }
        // Existing translation/scale transfers may put their control hull inside
        // the face. An affine map has a unique extension; retain that behavior.
        // These support vertices are not serialized or added to the user's pairs.
        private void ExtendAffineBoundary()
        {
            var b=1;var c=2;double best=0;
            for(var i=1;i<target.Count;i++)
                for(var j=i+1;j<target.Count;j++)
                {var area=Math.Abs(Cross(target[0],target[i],target[j]));if(area>best){best=area;b=i;c=j;}}
            if(best<1e-12)return;
            var den=Cross(target[0],target[b],target[c]);
            Func<Point,Point> evaluate=p=>
            {
                var x=Cross(p,target[b],target[c])/den;var y=Cross(target[0],p,target[c])/den;var z=1-x-y;
                return new Point(source[0].x*x+source[b].x*y+source[c].x*z,source[0].y*x+source[b].y*y+source[c].y*z);
            };
            for(var i=0;i<target.Count;i++)
            {var value=evaluate(target[i]);if(Math.Abs(value.x-source[i].x)>1e-7||Math.Abs(value.y-source[i].y)>1e-7)return;}
            foreach(var corner in new[]{new Point(0,0),new Point(1,0),new Point(1,1),new Point(0,1)})
            {
                if(target.Exists(p=>Math.Abs(p.x-corner.x)<1e-9&&Math.Abs(p.y-corner.y)<1e-9))continue;
                var value=evaluate(corner);target.Add(corner);source.Add(value);
            }
        }
        private void Triangulate()
        {
            var count=target.Count;
            target.Add(new Point(-16,-8)); target.Add(new Point(17,-8)); target.Add(new Point(.5,17));
            triangles.Add(new Triangle(count,count+1,count+2));
            for(var p=0;p<count;p++)
            {
                var edges=new List<Tuple<int,int>>();
                for(var i=triangles.Count-1;i>=0;i--)
                {
                    var t=triangles[i]; var a=target[t.a];var b=target[t.b];var c=target[t.c];var v=target[p];
                    var ax=a.x-v.x;var ay=a.y-v.y;var bx=b.x-v.x;var by=b.y-v.y;var cx=c.x-v.x;var cy=c.y-v.y;
                    var det=(ax*ax+ay*ay)*(bx*cy-by*cx)-(bx*bx+by*by)*(ax*cy-ay*cx)+(cx*cx+cy*cy)*(ax*by-ay*bx);
                    if(det* Math.Sign(Cross(a,b,c)) < -1e-14) continue;
                    triangles.RemoveAt(i);
                    for(var e=0;e<3;e++)
                    {
                        var u=t[e];var w=t[(e+1)%3];var match=edges.FindIndex(x=>(x.Item1==u&&x.Item2==w)||(x.Item1==w&&x.Item2==u));
                        if(match>=0)edges.RemoveAt(match);else edges.Add(Tuple.Create(u,w));
                    }
                }
                foreach(var e in edges)
                {
                    var t=new Triangle(e.Item1,e.Item2,p);
                    if(Math.Abs(Area(target,t))>1e-14) triangles.Add(t);
                }
            }
            triangles.RemoveAll(t=>t.a>=count||t.b>=count||t.c>=count);
            target.RemoveRange(count,3);
            if(triangles.Count==0)throw new ArgumentException("対応点を三角形に分割できません。");
            for(var p=0;p<count;p++)if(!triangles.Exists(t=>t.Has(p)))throw new ArgumentException("近接した対応点を分割できません。配置を確認してください。");
        }
        private int Orientation()
        {
            double largest=0;int sign=1;
            foreach(var t in triangles)
            {
                var a=Area(target,t);if(Math.Abs(a)<=largest)continue;
                largest=Math.Abs(a);sign=Math.Sign(a*Area(source,t));
            }
            return sign==0?1:sign;
        }
        private void Repair()
        {
            var sign=Orientation();
            for(var pass=0;pass<triangles.Count*triangles.Count;pass++)
            {
                var changed=false;
                for(var i=0;i<triangles.Count&&!changed;i++)
                {
                    var a=triangles[i];if(Area(target,a)*Area(source,a)*sign>1e-16)continue;
                    for(var j=0;j<triangles.Count;j++)
                    {
                        if(i==j)continue;var b=triangles[j];var shared=new List<int>();var x=-1;var y=-1;
                        for(var k=0;k<3;k++){if(b.Has(a[k]))shared.Add(a[k]);else x=a[k];if(!a.Has(b[k]))y=b[k];}
                        if(shared.Count!=2||x<0||y<0)continue;
                        var u=shared[0];var v=shared[1];
                        if(Cross(target[x],target[y],target[u])*Cross(target[x],target[y],target[v])>=-1e-16)continue;
                        var c=new Triangle(x,y,u);var d=new Triangle(y,x,v);
                        if(Area(target,c)*Area(source,c)*sign<=1e-16||Area(target,d)*Area(source,d)*sign<=1e-16)continue;
                        triangles[i]=c;triangles[j]=d;changed=true;break;
                    }
                }
                if(!changed)break;
            }
        }
        private void Validate()
        {
            var sign=Orientation();
            foreach(var t in triangles)
                if(Area(target,t)*Area(source,t)*sign<=1e-16)
                    throw new ArgumentException("対応点を固定したまま反転を解消できません。左右・上下の対応を確認してください。");
            for(var i=0;i<triangles.Count;i++)
                for(var j=0;j<i;j++)
                    if(IntersectionArea(triangles[i],triangles[j])>1e-12)
                        throw new ArgumentException("対応点の転送先が重なります。対応点の配置を確認してください。");
        }
        private double IntersectionArea(Triangle a, Triangle b)
        {
            var polygon=new List<Point>{source[a.a],source[a.b],source[a.c]};var direction=Math.Sign(Area(source,b));
            for(var edge=0;edge<3&&polygon.Count>=3;edge++)
            {
                var u=source[b[edge]];var v=source[b[(edge+1)%3]];var output=new List<Point>();
                for(var i=0;i<polygon.Count;i++)
                {
                    var p=polygon[i];var q=polygon[(i+1)%polygon.Count];var sp=Cross(u,v,p)*direction;var sq=Cross(u,v,q)*direction;
                    if(sp>=0)output.Add(p);
                    if((sp>=0)!=(sq>=0)){var f=sp/(sp-sq);output.Add(new Point(p.x+(q.x-p.x)*f,p.y+(q.y-p.y)*f));}
                }
                polygon=output;
            }
            double area=0;for(var i=1;i+1<polygon.Count;i++)area+=Cross(polygon[0],polygon[i],polygon[i+1]);
            return Math.Abs(area)*.5;
        }
        public bool TryEvaluate(Vector2 uv, out Vector2 mapped)
        {
            var p=new Point(uv);
            foreach(var t in triangles)
            {
                var a=target[t.a];var b=target[t.b];var c=target[t.c];var den=Cross(a,b,c);
                var x=Cross(p,b,c)/den;var y=Cross(a,p,c)/den;var z=1-x-y;
                if (boundaryMap)
                {
                    // Bound roundoff in UV distance, not barycentric weight on a very thin face.
                    var tolerance = 1e-7 / Math.Abs(den);
                    if (x < -tolerance * Vector2.Distance(b.Vector, c.Vector)
                        || y < -tolerance * Vector2.Distance(a.Vector, c.Vector)
                        || z < -tolerance * Vector2.Distance(a.Vector, b.Vector)) continue;
                }
                else if(x < -1e-7||y < -1e-7||z < -1e-7)continue;
                mapped=new Vector2((float)(source[t.a].x*x+source[t.b].x*y+source[t.c].x*z),(float)(source[t.a].y*x+source[t.b].y*y+source[t.c].y*z));return true;
            }
            mapped=default;return false;
        }
        internal IEnumerable<Vector2[]> TargetEdges()
        {
            var seen = new HashSet<(int, int)>();
            foreach (var t in triangles)
                for (var i = 0; i < 3; i++)
                {
                    var a = t[i]; var b = t[(i + 1) % 3];
                    if (seen.Add(a < b ? (a, b) : (b, a))) yield return new[] { target[a].Vector, target[b].Vector };
                }
        }

        internal static FBXUVMakeupExactMap FromTriangles(IReadOnlyList<Vector2> targetPoints,
            IReadOnlyList<Vector2> sourcePoints, IReadOnlyList<int[]> faces)
        {
            var map = new FBXUVMakeupExactMap { boundaryMap = true };
            for (var i = 0; i < targetPoints.Count; i++)
            {
                if (!FBXUVMakeupWarp.IsUnitUv(targetPoints[i]) || !FBXUVMakeupWarp.IsUnitUv(sourcePoints[i]))
                    throw new ArgumentException("口境界の補助点がUV範囲外です。");
                map.target.Add(new Point(targetPoints[i])); map.source.Add(new Point(sourcePoints[i]));
            }
            foreach (var f in faces)
            {
                var t = new Triangle(f[0], f[1], f[2]);
                // Check the actual float coordinates which will be uploaded to the GPU.
                if (Area(map.target, t) <= 1e-14 || Area(map.source, t) <= 1e-14)
                    throw new ArgumentException("口境界の写像に反転または退化があります。");
                map.triangles.Add(t);
            }
            // Unlike control triangulation, small boundary faces have no area-product cutoff.
            for (var i = 0; i < map.triangles.Count; i++)
                for (var j = 0; j < i; j++)
                    if (map.IntersectionArea(map.triangles[i], map.triangles[j]) > 1e-12)
                        throw new ArgumentException("口境界の写像が重なります。");
            return map;
        }

        internal IDisposable Bind(Material material, bool mouth = false)
        {
            return new Buffers(this,material,mouth);
        }
        private sealed class Buffers : IDisposable
        {
            private ComputeBuffer data,ranges,indices;
            internal Buffers(FBXUVMakeupExactMap map,Material material,bool mouth)
            {
                try
                {
                    var records=new List<Vector4>();var cells=new List<int>[32*32];
                    foreach(var t in map.triangles)
                    {
                        var a=map.target[t.a].Vector;var b=map.target[t.b].Vector;var c=map.target[t.c].Vector;
                        var s=map.source[t.a].Vector;var u=map.source[t.b].Vector;var v=map.source[t.c].Vector;
                        var id=records.Count/3;
                        records.Add(new Vector4(a.x,a.y,b.x,b.y));records.Add(new Vector4(c.x,c.y,s.x,s.y));records.Add(new Vector4(u.x,u.y,v.x,v.y));
                        var min=Vector2.Min(a,Vector2.Min(b,c));var max=Vector2.Max(a,Vector2.Max(b,c));
                        for(var y=Cell(min.y-1e-6f);y<=Cell(max.y+1e-6f);y++)for(var x=Cell(min.x-1e-6f);x<=Cell(max.x+1e-6f);x++)
                        {var cell=y*32+x;if(cells[cell]==null)cells[cell]=new List<int>();cells[cell].Add(id);}
                    }
                    var spans=new Vector2Int[cells.Length];var ids=new List<int>();
                    for(var i=0;i<cells.Length;i++){spans[i]=new Vector2Int(ids.Count,cells[i]?.Count??0);if(cells[i]!=null)ids.AddRange(cells[i]);}
                    data=new ComputeBuffer(records.Count,16);data.SetData(records);
                    ranges=new ComputeBuffer(spans.Length,8);ranges.SetData(spans);
                    indices=new ComputeBuffer(ids.Count,4);indices.SetData(ids);
                    var prefix = mouth ? "_Mouth" : "_Exact";
                    material.SetInt(mouth ? "_UseMouthMapping" : "_UseExactMapping",1);
                    material.SetBuffer(prefix+"Triangles",data);material.SetBuffer(prefix+"Ranges",ranges);material.SetBuffer(prefix+"Indices",indices);
                }
                catch{Dispose();throw;}
            }
            private static int Cell(float x)=>Mathf.Clamp(Mathf.FloorToInt(x*32),0,31);
            public void Dispose(){data?.Release();ranges?.Release();indices?.Release();data=ranges=indices=null;}
        }
    }
}
