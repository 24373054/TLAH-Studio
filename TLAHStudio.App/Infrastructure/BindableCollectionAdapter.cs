using System.Collections;
using System.Collections.Specialized;

namespace TLAHStudio.App.Infrastructure;

/// <summary>
/// Exposes a generic observable collection to WinUI through the non-generic
/// binding interfaces. This prevents C#/WinRT from generating a runtime
/// projection for IReadOnlyList&lt;T&gt; when an ItemsRepeater receives CLR data.
/// </summary>
internal sealed class BindableCollectionAdapter<T> : IList, INotifyCollectionChanged, IDisposable
{
    private readonly IList<T> _source;
    private readonly INotifyCollectionChanged? _observableSource;

    public BindableCollectionAdapter(IList<T> source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _observableSource = source as INotifyCollectionChanged;
        if (_observableSource != null)
            _observableSource.CollectionChanged += OnSourceCollectionChanged;
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count => _source.Count;
    public bool IsReadOnly => true;
    public bool IsFixedSize => false;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public object? this[int index]
    {
        get => _source[index];
        set => throw new NotSupportedException("The message timeline is read-only.");
    }

    public int Add(object? value) => throw new NotSupportedException("The message timeline is read-only.");
    public void Clear() => throw new NotSupportedException("The message timeline is read-only.");
    public bool Contains(object? value) => value is T item && _source.Contains(item);
    public int IndexOf(object? value) => value is T item ? _source.IndexOf(item) : -1;
    public void Insert(int index, object? value) => throw new NotSupportedException("The message timeline is read-only.");
    public void Remove(object? value) => throw new NotSupportedException("The message timeline is read-only.");
    public void RemoveAt(int index) => throw new NotSupportedException("The message timeline is read-only.");
    public void CopyTo(Array array, int index)
    {
        for (var sourceIndex = 0; sourceIndex < _source.Count; sourceIndex++)
            array.SetValue(_source[sourceIndex], index + sourceIndex);
    }
    public IEnumerator GetEnumerator() => ((IEnumerable)_source).GetEnumerator();

    public void Dispose()
    {
        if (_observableSource != null)
            _observableSource.CollectionChanged -= OnSourceCollectionChanged;
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        CollectionChanged?.Invoke(this, e);
}
