using System.Collections;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>An immutable list compared by its items, for records that describe what a view shows.</summary>
/// <typeparam name="T">The item type; a record, so items compare by value.</typeparam>
/// <remarks>
/// A record compares an array or a <see cref="List{T}"/> member by reference, so a record read twice from equal state
/// is never equal and a view that skips equal records redraws anyway. A <see cref="ShownList{T}"/> member compares by
/// its items.
/// </remarks>
/// <gotcha>Items compare with <see cref="EqualityComparer{T}.Default"/>: an item type with reference equality makes every freshly read list unequal.</gotcha>
/// <gotcha><c>default(ShownList&lt;T&gt;)</c> is the empty list and equals <see cref="Empty"/>.</gotcha>
public readonly struct ShownList<T> : IReadOnlyList<T>, IEquatable<ShownList<T>>
{
    private const int HashedItems = 8;
    private readonly T[]? items;

    /// <summary>Creates a list holding a copy of <paramref name="items"/>, in order.</summary>
    public ShownList(IEnumerable<T> items) => this.items = [.. items];

    /// <summary>The empty list.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
        Justification = "A shown record's initializer names its item type anyway (ShownList<Row>.Empty); a non-generic holder type would be a second way to spell the empty list (DSA-1102 Tech Design 5.3).")]
    public static ShownList<T> Empty => default;

    /// <summary>The number of items.</summary>
    public int Count => this.items?.Length ?? 0;

    /// <summary>The item at <paramref name="index"/>.</summary>
    /// <param name="index">The item's position, from 0.</param>
    /// <exception cref="IndexOutOfRangeException">The index is outside the list.</exception>
    public T this[int index] => (this.items ?? [])[index];

    /// <summary>Returns true when both lists hold equal items in the same order.</summary>
    public static bool operator ==(ShownList<T> left, ShownList<T> right) => left.Equals(right);

    /// <summary>Returns true when the lists differ in count or in any item.</summary>
    public static bool operator !=(ShownList<T> left, ShownList<T> right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(ShownList<T> other)
    {
        T[] mine = this.items ?? [];
        T[] theirs = other.items ?? [];
        if (mine.Length != theirs.Length) return false;
        EqualityComparer<T> comparer = EqualityComparer<T>.Default;
        for (int index = 0; index < mine.Length; index++)
            if (!comparer.Equals(mine[index], theirs[index])) return false;
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ShownList<T> other && this.Equals(other);

    /// <summary>Combines the count and the first eight items, so a long list hashes in constant time.</summary>
    public override int GetHashCode()
    {
        T[] mine = this.items ?? [];
        HashCode hash = new();
        hash.Add(mine.Length);
        for (int index = 0; index < Math.Min(HashedItems, mine.Length); index++) hash.Add(mine[index]);
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(this.items ?? [])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
}
