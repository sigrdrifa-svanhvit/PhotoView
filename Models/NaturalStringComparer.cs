namespace PhotoView.Models;

public sealed class NaturalStringComparer : IComparer<string?>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            char cx = x[ix], cy = y[iy];
            if (char.IsDigit(cx) && char.IsDigit(cy))
            {
                int sx = ix, sy = iy;
                while (ix < x.Length && char.IsDigit(x[ix])) ix++;
                while (iy < y.Length && char.IsDigit(y[iy])) iy++;

                var dx = x.Substring(sx, ix - sx).TrimStart('0');
                var dy = y.Substring(sy, iy - sy).TrimStart('0');

                int c = dx.Length.CompareTo(dy.Length);
                if (c != 0) return c;
                c = string.CompareOrdinal(dx, dy);
                if (c != 0) return c;

                // 数値が同じ場合、先頭ゼロが少ない方が小さい
                int zx = (ix - sx) - dx.Length;
                int zy = (iy - sy) - dy.Length;
                if (zx != zy) return zx.CompareTo(zy);
            }
            else
            {
                int c = string.Compare(x, ix, y, iy, 1, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                ix++;
                iy++;
            }
        }
        return (x.Length - ix).CompareTo(y.Length - iy);
    }
}
