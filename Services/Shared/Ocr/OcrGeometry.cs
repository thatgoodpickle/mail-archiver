namespace MailArchiver.Services.Shared
{
    public readonly record struct OcrPoint(double X, double Y);

    /// <summary>A detected text region: four corners (in the coordinates of the probability map) and its score.</summary>
    public sealed record OcrBox(OcrPoint[] Corners, double Score);

    /// <summary>
    /// Post-processing of the text detector's probability map (the "DB" method of PaddleOCR): threshold, group the
    /// pixels into connected blobs, take each blob's minimum-area rotated rectangle, drop weak or tiny ones, and grow the
    /// rectangle by the unclip distance because the detector shrinks text regions. Pure functions, no I/O.
    /// </summary>
    public static class OcrGeometry
    {
        public static List<OcrBox> BoxesFromProbabilityMap(
            float[] prob, int width, int height, double thresh, double boxThresh, double unclipRatio,
            int maxCandidates = 1000, double minSide = 3, double minUnclippedSide = 5)
        {
            var boxes = new List<OcrBox>();
            var n = width * height;
            if (prob.Length < n) throw new ArgumentException("probability map is smaller than width*height");

            var visited = new bool[n];
            var queue = new int[n];
            var comp = new List<int>();
            var boundary = new List<OcrPoint>();

            for (var start = 0; start < n; start++)
            {
                if (visited[start] || !(prob[start] > thresh)) continue;

                // 8-connected flood fill
                comp.Clear();
                var head = 0; var tail = 0;
                queue[tail++] = start; visited[start] = true;
                double sum = 0;
                while (head < tail)
                {
                    var p = queue[head++];
                    comp.Add(p);
                    sum += prob[p];
                    int x = p % width, y = p / width;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var ny = y + dy; if (ny < 0 || ny >= height) continue;
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            var nx = x + dx; if (nx < 0 || nx >= width) continue;
                            var q = ny * width + nx;
                            if (visited[q] || !(prob[q] > thresh)) continue;
                            visited[q] = true; queue[tail++] = q;
                        }
                    }
                }
                if (comp.Count < 4) continue;

                // boundary pixels (4-neighbourhood leaves the blob or the map)
                boundary.Clear();
                foreach (var p in comp)
                {
                    int x = p % width, y = p / width;
                    if (x == 0 || y == 0 || x == width - 1 || y == height - 1
                        || !(prob[p - 1] > thresh) || !(prob[p + 1] > thresh)
                        || !(prob[p - width] > thresh) || !(prob[p + width] > thresh))
                        boundary.Add(new OcrPoint(x, y));
                }

                var rect = MinAreaRect(ConvexHull(boundary));
                if (Math.Min(rect.W, rect.H) < minSide) continue;

                var score = sum / comp.Count;
                if (score < boxThresh) continue;

                // unclip: grow by area * ratio / perimeter on every side
                var d = rect.W * rect.H * unclipRatio / (2 * (rect.W + rect.H));
                var grown = new RotRect(rect.Cx, rect.Cy, rect.W + 2 * d, rect.H + 2 * d, rect.Ux, rect.Uy);
                if (Math.Min(grown.W, grown.H) < minUnclippedSide) continue;

                boxes.Add(new OcrBox(grown.Corners(), score));
                if (boxes.Count >= maxCandidates) break;
            }
            return boxes;
        }

        /// <summary>A rectangle by centre, side lengths along (Ux,Uy) and its normal, and the unit direction.</summary>
        public readonly record struct RotRect(double Cx, double Cy, double W, double H, double Ux, double Uy)
        {
            public OcrPoint[] Corners()
            {
                double nx = -Uy, ny = Ux, hw = W / 2, hh = H / 2;
                return new[]
                {
                    new OcrPoint(Cx - Ux * hw - nx * hh, Cy - Uy * hw - ny * hh),
                    new OcrPoint(Cx + Ux * hw - nx * hh, Cy + Uy * hw - ny * hh),
                    new OcrPoint(Cx + Ux * hw + nx * hh, Cy + Uy * hw + ny * hh),
                    new OcrPoint(Cx - Ux * hw + nx * hh, Cy - Uy * hw + ny * hh),
                };
            }
        }

        /// <summary>Convex hull (monotone chain), counter-clockwise, no repeated first point.</summary>
        public static List<OcrPoint> ConvexHull(IReadOnlyCollection<OcrPoint> points)
        {
            var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
            if (pts.Count <= 2) return pts;

            static double Cross(OcrPoint o, OcrPoint a, OcrPoint b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

            var hull = new List<OcrPoint>(pts.Count * 2);
            foreach (var p in pts)
            {
                while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            var lower = hull.Count + 1;
            for (var i = pts.Count - 2; i >= 0; i--)
            {
                while (hull.Count >= lower && Cross(hull[^2], hull[^1], pts[i]) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(pts[i]);
            }
            hull.RemoveAt(hull.Count - 1);
            return hull;
        }

        /// <summary>Smallest-area rotated rectangle around a convex hull (one side lies along a hull edge).</summary>
        public static RotRect MinAreaRect(IReadOnlyList<OcrPoint> hull)
        {
            if (hull.Count == 0) return new RotRect(0, 0, 0, 0, 1, 0);
            if (hull.Count == 1) return new RotRect(hull[0].X, hull[0].Y, 0, 0, 1, 0);
            if (hull.Count == 2)
            {
                var dx = hull[1].X - hull[0].X; var dy = hull[1].Y - hull[0].Y;
                var len = Math.Sqrt(dx * dx + dy * dy);
                return new RotRect((hull[0].X + hull[1].X) / 2, (hull[0].Y + hull[1].Y) / 2, len, 0, len == 0 ? 1 : dx / len, len == 0 ? 0 : dy / len);
            }

            var best = default(RotRect); var bestArea = double.MaxValue;
            for (var i = 0; i < hull.Count; i++)
            {
                var a = hull[i]; var b = hull[(i + 1) % hull.Count];
                var ex = b.X - a.X; var ey = b.Y - a.Y;
                var el = Math.Sqrt(ex * ex + ey * ey);
                if (el < 1e-9) continue;
                double ux = ex / el, uy = ey / el, nx = -uy, ny = ux;
                double minU = double.MaxValue, maxU = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
                foreach (var p in hull)
                {
                    var pu = p.X * ux + p.Y * uy; var pn = p.X * nx + p.Y * ny;
                    if (pu < minU) minU = pu; if (pu > maxU) maxU = pu;
                    if (pn < minN) minN = pn; if (pn > maxN) maxN = pn;
                }
                var w = maxU - minU; var h = maxN - minN; var area = w * h;
                if (area < bestArea)
                {
                    bestArea = area;
                    var cu = (minU + maxU) / 2; var cn = (minN + maxN) / 2;
                    best = new RotRect(cu * ux + cn * nx, cu * uy + cn * ny, w, h, ux, uy);
                }
            }
            return best;
        }
    }

    /// <summary>Interleaved 8-bit BGR pixels (the channel order the PaddleOCR models were trained with).</summary>
    public sealed class BgrImage
    {
        public int Width { get; }
        public int Height { get; }
        public byte[] Data { get; }

        public BgrImage(int width, int height, byte[]? data = null)
        {
            Width = width; Height = height; Data = data ?? new byte[checked(width * height * 3)];
        }

        /// <summary>Bilinear resize with half-pixel centres (the same convention as OpenCV's INTER_LINEAR).</summary>
        public BgrImage Resize(int newWidth, int newHeight)
        {
            var dst = new BgrImage(newWidth, newHeight);
            double sx = (double)Width / newWidth, sy = (double)Height / newHeight;
            var x0 = new int[newWidth]; var x1 = new int[newWidth]; var wx = new float[newWidth];
            for (var x = 0; x < newWidth; x++)
            {
                var fx = (x + 0.5) * sx - 0.5; if (fx < 0) fx = 0;
                var ix = (int)Math.Floor(fx); if (ix > Width - 1) ix = Width - 1;
                x0[x] = ix; x1[x] = Math.Min(ix + 1, Width - 1); wx[x] = (float)(fx - ix);
            }
            for (var y = 0; y < newHeight; y++)
            {
                var fy = (y + 0.5) * sy - 0.5; if (fy < 0) fy = 0;
                var iy = (int)Math.Floor(fy); if (iy > Height - 1) iy = Height - 1;
                var iy1 = Math.Min(iy + 1, Height - 1); var wy = (float)(fy - iy);
                for (var x = 0; x < newWidth; x++)
                {
                    for (var c = 0; c < 3; c++)
                    {
                        float p00 = Data[(iy * Width + x0[x]) * 3 + c], p01 = Data[(iy * Width + x1[x]) * 3 + c];
                        float p10 = Data[(iy1 * Width + x0[x]) * 3 + c], p11 = Data[(iy1 * Width + x1[x]) * 3 + c];
                        var top = p00 + (p01 - p00) * wx[x]; var bot = p10 + (p11 - p10) * wx[x];
                        dst.Data[(y * newWidth + x) * 3 + c] = (byte)Math.Clamp((int)Math.Round(top + (bot - top) * wy), 0, 255);
                    }
                }
            }
            return dst;
        }

        /// <summary>
        /// Cuts a (possibly rotated) rectangle out of the image into an upright strip, sampling bilinearly with the edge
        /// pixels repeated outside the image. Corners: top-left, top-right, bottom-right, bottom-left. Null when too small.
        /// </summary>
        public BgrImage? CropQuad(OcrPoint tl, OcrPoint tr, OcrPoint br, OcrPoint bl)
        {
            static double Dist(OcrPoint a, OcrPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
            var w = (int)Math.Max(Dist(tr, tl), Dist(br, bl));
            var h = (int)Math.Max(Dist(bl, tl), Dist(br, tr));
            if (w < 2 || h < 2) return null;

            var dst = new BgrImage(w, h);
            double ax = tr.X - tl.X, ay = tr.Y - tl.Y, bx = bl.X - tl.X, by = bl.Y - tl.Y;
            for (var v = 0; v < h; v++)
            {
                for (var u = 0; u < w; u++)
                {
                    var sx = tl.X + ax * u / w + bx * v / h;
                    var sy = tl.Y + ay * u / w + by * v / h;
                    SampleInto(dst.Data, (v * w + u) * 3, sx, sy);
                }
            }
            return dst;
        }

        private void SampleInto(byte[] dst, int at, double fx, double fy)
        {
            fx = Math.Clamp(fx, 0, Width - 1); fy = Math.Clamp(fy, 0, Height - 1);
            int x0 = (int)fx, y0 = (int)fy; var x1 = Math.Min(x0 + 1, Width - 1); var y1 = Math.Min(y0 + 1, Height - 1);
            var wx = fx - x0; var wy = fy - y0;
            for (var c = 0; c < 3; c++)
            {
                double p00 = Data[(y0 * Width + x0) * 3 + c], p01 = Data[(y0 * Width + x1) * 3 + c];
                double p10 = Data[(y1 * Width + x0) * 3 + c], p11 = Data[(y1 * Width + x1) * 3 + c];
                var top = p00 + (p01 - p00) * wx; var bot = p10 + (p11 - p10) * wx;
                dst[at + c] = (byte)Math.Clamp((int)Math.Round(top + (bot - top) * wy), 0, 255);
            }
        }

        /// <summary>Turns the image 90 degrees counter-clockwise (used for tall crops, which are vertical text).</summary>
        public BgrImage Rotate90CounterClockwise()
        {
            var dst = new BgrImage(Height, Width);   // new width = old height, new height = old width
            for (var r = 0; r < Width; r++)
                for (var c = 0; c < Height; c++)
                    Array.Copy(Data, (c * Width + (Width - 1 - r)) * 3, dst.Data, (r * Height + c) * 3, 3);
            return dst;
        }
    }
}
