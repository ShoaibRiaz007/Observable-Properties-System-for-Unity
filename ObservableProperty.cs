using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Base interface for all observable objects
/// </summary>
/// <typeparam name="T">Generic type</typeparam>
interface IObservable<T>
{
    /// <summary>
    /// Set/Get the value of the observable property
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is thread safe
    /// </para>
    /// <para>
    /// This will not notify subscribers if the new value is the same as the current value
    /// </para>
    ///  Subscriber will be notified in batch update
    /// </remarks>
    /// <param name="Value">The new value to set</param>
    T Value { get; set; }
    /// <summary>
    /// Subscribe to the observable property
    /// </summary>
    /// <remarks>
    /// This is thread safe
    /// <para>
    /// This will not throw an exception if the observer is already subscribed
    /// </para>
    /// <para>
    /// This will throw an exception if the observer is null
    /// </para>
    /// This will throw an exception if the callback is null
    /// </remarks>
    /// <param name="observer">The observer to subscribe to</param>
    /// <param name="callback">The callback to invoke when the value changes</param>
    void Subscribe(object observer, Action<T> callback);
    /// <summary>
    /// Subscribe to the observable property.
    /// </summary>
    /// <remarks>
    /// This will not throw an exception if the observer is already subscribed.
    /// <para>
    /// This will throw an exception if the callback is null
    /// </para>
    /// <para>
    /// This is low level and should be used sparingly. It will impact performance.
    /// </para>
    /// </remarks>
    /// <param name="callback">The callback to invoke when the value changes</param>
    void Subscribe(UnityAction<T> callback);
    /// <summary>
    /// Unsubscribe from the observable property
    /// </summary>
    /// <param name="observer"></param>
    void Unsubscribe(object observer);
    /// <summary>
    /// Set the value of the observable property
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is thread safe
    /// </para>
    /// <para>
    /// This will not notify subscribers if the new value is the same as the current value
    /// </para>
    /// </remarks>
    /// <param name="newValue">The new value to set</param>
    /// <param name="forceUpdate">
    /// If true, the value will be updated and subscribers will be notified on same frame 
    /// <para>This is not recommended for large numbers of subscribers</para>
    /// <para>If false values will be batched and notified by batch update</para>
    /// </param>
    void SetValue(T newValue, bool forceUpdate = false);
}
/// <summary>
/// Central notification system for all observable properties.
/// This is a singleton and will be destroyed when the scene is unloaded.
/// This is thread-safe.
/// </summary>
public class CentralNotificationSystem : MonoBehaviour
{
    private const int MaxJobHandles = 10000;
    private const int InitialBatchSize = 500;
    public static CentralNotificationSystem Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod]
    private static void LazyInstance()
    {
        if (Instance == null)
        {
            var temp = new GameObject("NotificationSystem");
            Instance = temp.AddComponent<CentralNotificationSystem>();
            DontDestroyOnLoad(temp);
        }
    }

    private class PriorityQueue<TKey, TValue> where TKey : IComparable<TKey>
    {
        private readonly SortedDictionary<TKey, Queue<TValue>> sortedDict = new();

        public bool IsEmpty => sortedDict.Count == 0;
        public int Count { get; private set; }

        public void Enqueue(TKey key, TValue value)
        {
            if (!sortedDict.TryGetValue(key, out var queue))
            {
                queue = new Queue<TValue>();
                sortedDict[key] = queue;
            }
            queue.Enqueue(value);
            Count++;
        }

        public TValue Dequeue()
        {
            if (IsEmpty) throw new InvalidOperationException("Queue is empty.");

            var firstPair = sortedDict.First();
            var value = firstPair.Value.Dequeue();

            if (firstPair.Value.Count == 0)
                sortedDict.Remove(firstPair.Key);
            Count--;
            if (IsEmpty)
                Count = 0;
            return value;
        }

        public void Clear()
        {
            sortedDict.Clear();
            Count = 0;
        }
    }

#if UNITY_EDITOR
    [field: SerializeField] int _ActiveJobs;
#endif
    private readonly PriorityQueue<int, Action> actionQueue = new();
    [field: SerializeField] private int currentBatchSize = InitialBatchSize;

    private void Awake()
    {
        Application.lowMemory += OnLowMemory;
    }

    private void OnLowMemory()
    {
        Debug.LogWarning("Low memory detected. Cleaning up unused resources.");
        GC.Collect();
        ReduceBatchSize();
    }

    private void LateUpdate()
    {
#if UNITY_EDITOR
        _ActiveJobs = actionQueue.Count;
#endif
        if (actionQueue.Count == 0)
            return;
        AdjustBatchSize();
        ScheduleActions();
    }
    private void AdjustBatchSize()
    {
        if (actionQueue.Count > MaxJobHandles)
        {
            currentBatchSize = actionQueue.Count;
        }
        else
        {
            currentBatchSize = Math.Max(currentBatchSize - 50, InitialBatchSize);
        }
    }
    public void EnqueueMainThreadAction(Action action, int priority = 0)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        actionQueue.Enqueue(priority, action);
    }

    private void ScheduleActions()
    {
        if (actionQueue.IsEmpty ) return;

        int processedCount = 0;

        try
        {
            while (!actionQueue.IsEmpty && processedCount < currentBatchSize)
            {
                var action = actionQueue.Dequeue();

                action?.Invoke();
                processedCount++;
            }
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, this);
        }
    }
    private void ReduceBatchSize()
    {
        currentBatchSize = Math.Max(currentBatchSize / 2, 50);
        Debug.LogWarning($"Batch size reduced to {currentBatchSize} due to low memory.");
    }
    private void OnDestroy()
    {
        Application.lowMemory -= OnLowMemory;
        actionQueue.Clear();
        Instance = null;
    }
}



/// <summary>
/// Base Observable property class which handles all observable logic
/// </summary>
/// <typeparam name="T">Generic type</typeparam>
public class ObservableProperty<T> : IObservable<T>, IDisposable
{
    [SerializeField]
    private T value;

    [SerializeField]
    private UnityEvent<T> OnValueChanged = new UnityEvent<T>();

    private readonly ConcurrentDictionary<object, Action<T>> subscribers = new();

    public ObservableProperty() { }

    public ObservableProperty(T initialValue)
    {
        value = initialValue;
    }

    public T Value
    {
        get => value;
        set => SetValue(value);
    }
    public void SetValue(T newValue, bool forceUpdate = false)
    {
        if (EqualityComparer<T>.Default.Equals(value, newValue)) return;

        value = newValue;
        CentralNotificationSystem.Instance.EnqueueMainThreadAction(() => NotifySubscribers(newValue), forceUpdate ? 100 : 0);
    }

    private void NotifySubscribers(T newValue)
    {
        List<object> invalidSubscribers = new();

        foreach (var subscriber in subscribers)
        {
            try
            {
                subscriber.Value?.Invoke(newValue);
            }
            catch (Exception e)
            {
                Debug.LogError($"Exception notifying subscriber: {e}");
                invalidSubscribers.Add(subscriber.Key);
            }
        }

        foreach (var invalid in invalidSubscribers)
        {
            subscribers.TryRemove(invalid, out _);
        }

        OnValueChanged?.Invoke(newValue);
    }

    public void Subscribe(object observer, Action<T> callback)
    {
        if (observer == null) throw new ArgumentNullException(nameof(observer));
        if (callback == null) throw new ArgumentNullException(nameof(callback));

        if (!subscribers.TryAdd(observer, callback))
        {
            Debug.LogWarning($"Observer already subscribed: {observer}");
        }
    }

    public void Subscribe(UnityAction<T> callback)
    {
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        OnValueChanged.AddListener(callback);
    }

    public void Unsubscribe(object observer)
    {
        if (observer == null) throw new ArgumentNullException(nameof(observer));
        if (!subscribers.TryRemove(observer, out _))
        {
            Debug.LogWarning($"Observer not found: {observer}");
        }
    }

    public void Dispose()
    {
        subscribers.Clear();
        OnValueChanged.RemoveAllListeners();
    }

    public static bool operator ==(ObservableProperty<T> a, ObservableProperty<T> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return EqualityComparer<T>.Default.Equals(a.value, b.value);
    }

    public static bool operator !=(ObservableProperty<T> a, ObservableProperty<T> b) => !(a == b);

    public override bool Equals(object obj)
    {
        if (obj is ObservableProperty<T> other)
        {
            return EqualityComparer<T>.Default.Equals(value, other.value);
        }
        return false;
    }

    public override int GetHashCode() => value?.GetHashCode() ?? 0;
}

#region Base Types
[System.Serializable]
public class ObservableInt : ObservableProperty<int>
{
    public ObservableInt() : base() { }
    public ObservableInt(int initialValue) : base(initialValue) { }
}

[System.Serializable]
public class ObservableBool : ObservableProperty<bool>
{
    public ObservableBool() : base() { }
    public ObservableBool(bool initialValue) : base(initialValue) { }
}

[System.Serializable]
public class ObservableFloat : ObservableProperty<float>
{
    public ObservableFloat() : base() { }
    public ObservableFloat(float initialValue) : base(initialValue) { }
}
[System.Serializable]
public class ObservableDouble : ObservableProperty<double>
{
    public ObservableDouble() : base() { }
    public ObservableDouble(double initialValue) : base(initialValue) { }
}
[System.Serializable]
public class ObservableLong : ObservableProperty<long>
{
    public ObservableLong() : base() { }
    public ObservableLong(long initialValue) : base(initialValue) { }
}
[System.Serializable]
public class ObservableString : ObservableProperty<string>
{
    public ObservableString() : base() { }
    public ObservableString(string initialValue) : base(initialValue) { }
}
[System.Serializable]
public class ObservableEnum<T> : ObservableProperty<T> where T : Enum
{
    public ObservableEnum() : base() { }
    public ObservableEnum(T initialValue) : base(initialValue) { }
}
#endregion

#region Unity Specific Types
[System.Serializable]
public class ObservableVector3 : ObservableProperty<Vector3>
{
    public ObservableVector3() : base() { }
    public ObservableVector3(Vector3 initialValue) : base(initialValue) { }
}
[System.Serializable]
public class ObservableVector2 : ObservableProperty<Vector2>
{
    public ObservableVector2() : base() { }
    public ObservableVector2(Vector2 initialValue) : base(initialValue) { }
}
[System.Serializable]
public class ObservableVector3Int : ObservableProperty<Vector3Int>
{
    public ObservableVector3Int() : base() { }
    public ObservableVector3Int(Vector3Int initialValue) : base(initialValue) { }
}
[System.Serializable]
public class ObservableVector2Int : ObservableProperty<Vector2Int>
{
    public ObservableVector2Int() : base() { }
    public ObservableVector2Int(Vector2Int initialValue) : base(initialValue) { }
}
[System.Serializable]
public class ObservableQuaternion : ObservableProperty<Quaternion>
{
    public ObservableQuaternion() : base() { }
    public ObservableQuaternion(Quaternion initialValue) : base(initialValue) { }
}
#endregion
