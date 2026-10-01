using System;

namespace AdvancedViewer.Markdown;

/// <summary>
/// Prefix sums over item extents (gap + height) as a Fenwick tree: O(log n) update when an item
/// is measured, O(log n) "y of item i" and "item at y". Lets the view mix measured heights with
/// estimates for a document of any size and still scroll and paint by pixel.
/// </summary>
internal sealed class HeightIndex
{
    private double[] _tree = new double[1];
    private int _n;
    private int _topBit;

    public int Count => _n;

    /// <summary>Rebuilds from <paramref name="extents"/>[0..count) in O(n). Reuses the array when large enough.</summary>
    public void Build(ReadOnlySpan<LayoutItem> items)
    {
        _n = items.Length;
        if (_tree.Length < _n + 1 || _tree.Length > 2 * (_n + 1) + 1024) _tree = new double[_n + 1];
        else Array.Clear(_tree, 0, _n + 1);
        for (int i = 1; i <= _n; i++)
        {
            _tree[i] += items[i - 1].Extent;
            int j = i + (i & -i);
            if (j <= _n) _tree[j] += _tree[i];
        }
        _topBit = 1;
        while (_topBit * 2 <= _n) _topBit *= 2;
    }

    /// <summary>Adds one item at the end in O(log n) (incremental parsing appends items).</summary>
    public void Append(double extent)
    {
        int i = _n + 1;
        if (_tree.Length <= i) Array.Resize(ref _tree, Math.Max(16, _tree.Length * 2));
        // Node i covers items (i - lowbit(i), i]: its own extent plus the already complete range below it.
        int low = i - (i & -i);
        _tree[i] = extent + Prefix(i - 1) - Prefix(low);
        _n = i;
        if (_topBit == 0) _topBit = 1;
        while (_topBit * 2 <= _n) _topBit *= 2;
    }

    public void Clear()
    {
        _n = 0;
        _topBit = 0;
        _tree = new double[1];
    }

    public void Add(int index, double delta)
    {
        if (delta == 0) return;
        for (int i = index + 1; i <= _n; i += i & -i) _tree[i] += delta;
    }

    /// <summary>Sum of the extents of items [0, count).</summary>
    public double Prefix(int count)
    {
        double s = 0;
        for (int i = Math.Min(count, _n); i > 0; i -= i & -i) s += _tree[i];
        return s;
    }

    public double Total => Prefix(_n);

    /// <summary>Index of the item containing offset <paramref name="y"/> (clamped to 0..Count-1; 0 when empty).</summary>
    public int Find(double y)
    {
        if (_n == 0 || y <= 0) return 0;
        int pos = 0;
        double rem = y;
        for (int step = _topBit; step > 0; step >>= 1)
        {
            int next = pos + step;
            if (next <= _n && _tree[next] <= rem)
            {
                pos = next;
                rem -= _tree[next];
            }
        }
        return Math.Min(pos, _n - 1);
    }
}
