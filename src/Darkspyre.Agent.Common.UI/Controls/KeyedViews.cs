using System.Collections;
using Avalonia.Controls;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>
/// Keeps one control per key in a panel's children or an items control's items. A key that appears is created, a key
/// that disappears is removed, a key whose shown item changed is updated in place, and every other control is left
/// alone: not updated, not detached, not moved unless its position changed.
/// </summary>
/// <typeparam name="TKey">The item's stable identity.</typeparam>
/// <typeparam name="TItem">What one control shows: a record of displayed fields.</typeparam>
/// <typeparam name="TControl">The control kept for one key.</typeparam>
/// <remarks>
/// A control that leaves the visual tree and comes back detaches and re-attaches, so it drops and remakes its
/// subscriptions and composition target registrations. A view that rebuilds its list on every refresh therefore pays
/// for every item, not only the changed ones.
/// </remarks>
/// <gotcha>Use it on the UI thread only. One instance owns one list: two lists need two instances.</gotcha>
/// <gotcha>
/// <c>update</c> runs only when the comparer finds the new item different from the last one shown for its key. An item
/// whose collections are compared by reference (an array, a <see cref="List{T}"/>) is never equal to a freshly read
/// one; use <see cref="ShownList{T}"/> for collections inside a shown item.
/// </gotcha>
public sealed class KeyedViews<TKey, TItem, TControl> where TKey : notnull where TControl : Control
{
    private readonly Func<TItem, TKey> key;
    private readonly Func<TItem, TControl> create;
    private readonly Action<TControl, TItem> update;
    private readonly IEqualityComparer<TItem> shown;
    private readonly Dictionary<TKey, Kept> kept = [];

    /// <summary>Creates an empty set of kept controls.</summary>
    /// <param name="key">The item's stable identity.</param>
    /// <param name="create">Builds the control for a new key, already showing the item.</param>
    /// <param name="update">Changes a kept control to show a changed item. Called only when the item changed.</param>
    /// <param name="shown">Decides whether two items look the same; default <see cref="EqualityComparer{T}.Default"/>.</param>
    public KeyedViews(Func<TItem, TKey> key, Func<TItem, TControl> create, Action<TControl, TItem> update,
        IEqualityComparer<TItem>? shown = null)
    {
        this.key = key;
        this.create = create;
        this.update = update;
        this.shown = shown ?? EqualityComparer<TItem>.Default;
    }

    /// <summary>Brings <paramref name="list"/> to <paramref name="items"/>, in order. Returns the number of controls created, updated, removed and moved.</summary>
    /// <param name="list">A panel's <c>Children</c> or an items control's <c>Items</c>.</param>
    /// <param name="items">The items to show, in order. Each key appears at most once.</param>
    /// <exception cref="ArgumentException">Two items have the same key.</exception>
    public KeyedChange Apply(IList list, IReadOnlyList<TItem> items)
    {
        int created = 0;
        int updated = 0;
        HashSet<TKey> seen = [];
        List<TControl> desired = new(items.Count);
        foreach (TItem item in items)
        {
            TKey identity = this.key(item);
            if (!seen.Add(identity)) throw new ArgumentException($"Two items have the key {identity}.", nameof(items));
            if (this.kept.TryGetValue(identity, out Kept? entry))
            {
                if (!this.shown.Equals(entry.Item, item))
                {
                    this.update(entry.Control, item);
                    entry.Item = item;
                    updated++;
                }
            }
            else
            {
                entry = new(this.create(item), item);
                this.kept[identity] = entry;
                created++;
            }
            desired.Add(entry.Control);
        }
        int removed = 0;
        foreach (TKey gone in this.kept.Keys.Where(identity => !seen.Contains(identity)).ToArray())
        {
            this.kept.Remove(gone);
            removed++;
        }
        int moved = KeyedChildren.Reorder(list, desired);
        return new(created, updated, removed, moved);
    }

    /// <summary>The control currently kept for <paramref name="key"/>, or null.</summary>
    public TControl? Find(TKey key) => this.kept.TryGetValue(key, out Kept? entry) ? entry.Control : null;

    private sealed class Kept(TControl control, TItem item)
    {
        public TControl Control { get; } = control;

        public TItem Item { get; set; } = item;
    }
}

/// <summary>What one <see cref="KeyedViews{TKey, TItem, TControl}.Apply"/> did.</summary>
/// <param name="Created">Controls built for new keys.</param>
/// <param name="Updated">Kept controls changed because their item changed.</param>
/// <param name="Removed">Controls dropped because their key is gone.</param>
/// <param name="Moved">Kept controls moved to a new position.</param>
public readonly record struct KeyedChange(int Created, int Updated, int Removed, int Moved);

/// <summary>Brings a list to a sequence of existing controls in place, touching only entries that moved.</summary>
/// <remarks>
/// Every control that is removed from the visual tree and added back detaches and re-attaches, so it drops and remakes
/// its subscriptions and composition target registrations. This moves, inserts and removes only the entries whose
/// position changed; an entry already in its place stays attached.
/// </remarks>
/// <gotcha>The caller must already hold the controls. To create and update controls by key, use <see cref="KeyedViews{TKey, TItem, TControl}"/>.</gotcha>
public static class KeyedChildren
{
    /// <summary>Makes <paramref name="list"/> hold exactly <paramref name="desired"/>, in order, touching only entries that moved.</summary>
    /// <param name="list">A panel's <c>Children</c> or an items control's <c>Items</c>.</param>
    /// <param name="desired">The entries in the order they must appear. Each entry appears at most once.</param>
    public static void Apply<T>(IList list, IReadOnlyList<T> desired) where T : class => Reorder(list, desired);

    // Returns how many entries already in the list moved to a new position.
    internal static int Reorder<T>(IList list, IReadOnlyList<T> desired) where T : class
    {
        HashSet<T> wanted = new(desired, ReferenceEqualityComparer.Instance);
        for (int index = list.Count - 1; index >= 0; index--)
            if (list[index] is not T entry || !wanted.Contains(entry)) list.RemoveAt(index);
        int moved = 0;
        for (int index = 0; index < desired.Count; index++)
        {
            T entry = desired[index];
            if (index < list.Count && ReferenceEquals(list[index], entry)) continue;
            int at = list.IndexOf(entry);
            if (at >= 0)
            {
                list.RemoveAt(at);
                moved++;
            }
            list.Insert(index, entry);
        }
        return moved;
    }
}
