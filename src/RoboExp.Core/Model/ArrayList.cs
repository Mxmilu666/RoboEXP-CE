using System.Collections;

namespace RoboExp.Core.Model;

/// <summary>Untyped access used by the .rcu reader/writer.</summary>
internal interface ISerializedArrayList
{
    Type ElementType { get; }
    int Count { get; }
    int InternalVersion { get; set; }

    /// <summary>The backing array exactly as <c>System.Collections.ArrayList._items</c> (length = capacity).</summary>
    object?[] GetItems();

    void Load(object?[] items, int size, int version);

    /// <summary>
    /// Identity of a zero-length <c>_items</c> array. .NET Framework lists created by <c>new ArrayList()</c> share the
    /// static <c>ArrayList.emptyArray</c>, which BinaryFormatter writes once and then references; lists read from a file
    /// keep the sharing found there. Lists created in memory use <see cref="ArrayListShared.EmptyItems"/>.
    /// </summary>
    object EmptyItemsIdentity { get; set; }
}

internal static class ArrayListShared
{
    /// <summary>Stands for the static <c>ArrayList.emptyArray</c> of the original runtime.</summary>
    public static readonly object EmptyItems = new();
}

/// <summary>Untyped access to <see cref="EventArrayList{T}"/> fields for the .rcu reader/writer.</summary>
internal interface ISerializedEventArrayList : ISerializedArrayList
{
    int Version { get; set; }
    int OldVersion { get; set; }
    SerializedDelegate? Changed { get; set; }
}

/// <summary>
/// Typed replacement for <c>System.Collections.ArrayList</c> that keeps the state ArrayList serializes:
/// the backing array length (<c>_items</c>, i.e. <see cref="Capacity"/>) and the modification counter
/// (<c>_version</c>, <see cref="InternalVersion"/>). Growth and version bumps follow the .NET Framework ArrayList
/// so edits produce the same on-disk shape as the original application.
/// </summary>
public class ArrayList<T> : IList<T>, IReadOnlyList<T>, ISerializedArrayList
{
    private const int DefaultCapacity = 4;
    private T[] _items;
    private int _size;

    public ArrayList() => _items = [];

    public ArrayList(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _items = capacity == 0 ? [] : new T[capacity];
    }

    public ArrayList(IEnumerable<T> items) : this()
    {
        foreach (var item in items) Add(item);
    }

    public int Count => _size;

    private object _emptyItemsIdentity = ArrayListShared.EmptyItems;

    object ISerializedArrayList.EmptyItemsIdentity
    {
        get => _emptyItemsIdentity;
        set => _emptyItemsIdentity = value;
    }

    /// <summary>Serialized <c>ArrayList._version</c>: incremented by every mutation.</summary>
    public int InternalVersion { get; set; }

    /// <summary>Length of the backing array (<c>ArrayList.Capacity</c>).</summary>
    public int Capacity
    {
        get => _items.Length;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, _size);
            if (value == _items.Length) return;
            // ArrayList.Capacity = 0 allocates the default capacity.
            var items = new T[value > 0 ? value : DefaultCapacity];
            Array.Copy(_items, items, _size);
            _items = items;
        }
    }

    public bool IsReadOnly => false;

    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_size, nameof(index));
            return _items[index];
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_size, nameof(index));
            _items[index] = value;
            OnChanged();
        }
    }

    public void Add(T item)
    {
        if (_size == _items.Length) EnsureCapacity(_size + 1);
        _items[_size++] = item;
        OnChanged();
    }

    public void AddRange(IEnumerable<T> items) => InsertRange(_size, items);

    public void Insert(int index, T item)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)_size, nameof(index));
        if (_size == _items.Length) EnsureCapacity(_size + 1);
        if (index < _size) Array.Copy(_items, index, _items, index + 1, _size - index);
        _items[index] = item;
        _size++;
        OnChanged();
    }

    public void InsertRange(int index, IEnumerable<T> items)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)_size, nameof(index));
        var add = items as ICollection<T> ?? items.ToArray();
        var count = add.Count;
        if (count == 0) return;
        EnsureCapacity(_size + count);
        if (index < _size) Array.Copy(_items, index, _items, index + count, _size - index);
        add.CopyTo(_items, index);
        _size += count;
        OnChanged();
    }

    public bool Remove(T item)
    {
        var index = IndexOf(item);
        if (index < 0) return false;
        RemoveAt(index);
        return true;
    }

    public void RemoveAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_size, nameof(index));
        _size--;
        if (index < _size) Array.Copy(_items, index + 1, _items, index, _size - index);
        _items[_size] = default!;
        OnChanged();
    }

    public void Clear()
    {
        if (_size > 0)
        {
            Array.Clear(_items, 0, _size);
            _size = 0;
        }
        OnChanged();
    }

    public int IndexOf(T item) => Array.IndexOf(_items, item, 0, _size);

    public bool Contains(T item) => IndexOf(item) >= 0;

    public void CopyTo(T[] array, int arrayIndex) => Array.Copy(_items, 0, array, arrayIndex, _size);

    public IEnumerator<T> GetEnumerator()
    {
        var version = InternalVersion;
        for (var i = 0; i < _size; i++)
        {
            if (version != InternalVersion) throw new InvalidOperationException("Collection was modified during enumeration.");
            yield return _items[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Hook for every mutation; bumps <see cref="InternalVersion"/> like ArrayList.</summary>
    protected virtual void OnChanged() => InternalVersion++;

    private void EnsureCapacity(int min)
    {
        if (_items.Length >= min) return;
        var capacity = _items.Length == 0 ? DefaultCapacity : _items.Length * 2;
        if (capacity < min) capacity = min;
        Capacity = capacity;
    }

    Type ISerializedArrayList.ElementType => typeof(T);

    object?[] ISerializedArrayList.GetItems()
    {
        var items = new object?[_items.Length];
        for (var i = 0; i < _size; i++) items[i] = _items[i];
        return items;
    }

    void ISerializedArrayList.Load(object?[] items, int size, int version)
    {
        if (size < 0 || size > items.Length) throw new InvalidDataException($"ArrayList _size {size} outside _items length {items.Length}");
        _items = new T[items.Length];
        for (var i = 0; i < size; i++)
        {
            _items[i] = items[i] switch
            {
                T t => t,
                null when default(T) is null => default!,
                var other => throw new InvalidDataException(
                    $"ArrayList element {i} is {other?.GetType().Name ?? "null"}, expected {typeof(T).Name}"),
            };
        }
        _size = size;
        InternalVersion = version;
    }
}

/// <summary>
/// Port of <c>RoboExp.GraphicObjects.EventArrayList</c> (an ArrayList subclass serialized field-by-field:
/// <c>m_Version, m_OldVersion, Changed, ArrayList+_items, ArrayList+_size, ArrayList+_version</c>).
/// </summary>
public class EventArrayList<T> : ArrayList<T>, ISerializedEventArrayList
{
    /// <summary>Serialized <c>m_Version</c>.</summary>
    public int Version { get; set; } = 100;

    /// <summary>Serialized <c>m_OldVersion</c>.</summary>
    public int OldVersion { get; set; } = 100;

    /// <summary>
    /// Serialized backing field of the <c>Changed</c> event. The original persisted its subscribers
    /// (<c>ProjectData.*ChangedHandle</c>, re-added on every load); kept opaque so files round-trip. Null is valid:
    /// the original re-subscribes in <c>ProjectData.OnDeserialization</c>.
    /// </summary>
    public SerializedDelegate? Changed { get; set; }
}
