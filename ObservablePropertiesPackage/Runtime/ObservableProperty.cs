using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Non-generic base class enabling zero-allocation enqueuing and state coalescing.
/// </summary>
public abstract class ObservableBase
{
    private int _isEnqueued;     // 0 = not enqueued, 1 = enqueued
    private int _isDispatching;  // 0 = idle, 1 = dispatching (reentrancy guard)

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal bool TryMarkEnqueued() => Interlocked.Exchange(ref _isEnqueued, 1) == 0;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void ClearEnqueued() => Volatile.Write(ref _isEnqueued, 0);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal bool TryBeginDispatch() => Interlocked.Exchange(ref _isDispatching, 1) == 0;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void EndDispatch() => Volatile.Write(ref _isDispatching, 0);

    internal abstract void DispatchNotifications();
}

/// <summary>
/// Base interface for all observable objects.
/// </summary>
/// <typeparam name="T">Generic type</typeparam>
public interface IObservable<T>
{
    /// <summary>
    /// Set/Get the value of the observable property (Thread-safe).
    /// </summary>
    T Value { get; set; }

    /// <summary>
    /// Subscribe to value changes using an observer identity.
    /// Thread-safe. Replaces any existing subscription for the same observer.
    /// </summary>
    void Subscribe(object observer, Action<T> callback);

    /// <summary>
    /// Subscribe using an Action callback, returning a disposable subscription handle.
    /// </summary>
    IDisposable Subscribe(Action<T> callback);

    /// <summary>
    /// Subscribe using a UnityAction callback.
    /// </summary>
    void Subscribe(UnityAction<T> callback);

    /// <summary>
    /// Unsubscribe an observer identity.
    /// </summary>
    void Unsubscribe(object observer);

    /// <summary>
    /// Unsubscribe a UnityAction callback.
    /// </summary>
    void Unsubscribe(UnityAction<T> callback);

    /// <summary>
    /// Set value with optional immediate notification.
    /// </summary>
    /// <param name="newValue">The new value to set</param>
    /// <param name="forceUpdate">If true and on main thread, updates synchronously without queueing</param>
    void SetValue(T newValue, bool forceUpdate = false);
}

/// <summary>
/// Central notification system supporting Action delegates and lambdas.
/// Uses pre-allocated circular ring buffers to guarantee 0 B GC.Alloc when running cached delegates.
/// </summary>
public class CentralNotificationSystem : MonoBehaviour
{
    private const int BufferCapacity = 16384;
    private const int BufferMask = BufferCapacity - 1;
    private const int MaxBatchPerFrame = 4096;

    public static CentralNotificationSystem Instance { get; private set; }
    private static int _mainThreadId;

    // Pre-allocated Action arrays for zero-GC queue operations
    private readonly Action[] _normalRing = new Action[BufferCapacity];
    private readonly Action[] _highRing = new Action[BufferCapacity];

    private int _normalHead;
    private int _normalTail;
    private readonly object _normalLock = new();

    private int _highHead;
    private int _highTail;
    private readonly object _highLock = new();

    public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void LazyInstance()
    {
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        if (Instance == null)
        {
            var temp = new GameObject("[CentralNotificationSystem]");
            Instance = temp.AddComponent<CentralNotificationSystem>();
            DontDestroyOnLoad(temp);
        }
    }

    private void Awake()
    {
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    /// <summary>
    /// Enqueue an Action or lambda to execute on the Unity main thread.
    /// When passing a cached delegate (e.g. from ObservableProperty), this allocates exactly 0 B heap memory.
    /// </summary>
    /// <param name="action">Action callback to execute</param>
    /// <param name="priority">Priority level (>= 100 for high priority, 0 for normal)</param>
    public void EnqueueMainThreadAction(Action action, int priority = 0)
    {
        if (action == null) return;

        if (priority >= 100)
        {
            lock (_highLock)
            {
                _highRing[_highTail & BufferMask] = action;
                _highTail++;
            }
        }
        else
        {
            lock (_normalLock)
            {
                _normalRing[_normalTail & BufferMask] = action;
                _normalTail++;
            }
        }
    }

    private void LateUpdate()
    {
        int processedCount = 0;

        // Drain high-priority actions first
        while (processedCount < MaxBatchPerFrame)
        {
            Action action = null;
            lock (_highLock)
            {
                if (_highHead != _highTail)
                {
                    int index = _highHead & BufferMask;
                    action = _highRing[index];
                    _highRing[index] = null; // Clear reference
                    _highHead++;
                }
            }

            if (action == null) break;

            try
            {
                action.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, this);
            }
            processedCount++;
        }

        // Drain normal-priority actions
        while (processedCount < MaxBatchPerFrame)
        {
            Action action = null;
            lock (_normalLock)
            {
                if (_normalHead != _normalTail)
                {
                    int index = _normalHead & BufferMask;
                    action = _normalRing[index];
                    _normalRing[index] = null;
                    _normalHead++;
                }
            }

            if (action == null) break;

            try
            {
                action.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, this);
            }
            processedCount++;
        }
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }
}

/// <summary>
/// Thread-safe, Zero-Heap Observable Property.
/// Keeps the Action/lambda design pattern by caching its dispatch delegate upon creation.
/// </summary>
/// <typeparam name="T">Property type</typeparam>
[Serializable]
public class ObservableProperty<T> : ObservableBase, IObservable<T>, IDisposable
{
    private struct SubscriberEntry
    {
        public object Observer;
        public Action<T> Callback;
    }

    [SerializeField]
    private T value;

    private readonly object _lock = new();

    // Pre-allocated Action delegate: satisfies the lambda/Action design with 0 B GC per update!
    private readonly Action _cachedDispatchAction;

    // Pre-allocated array for subscribers (avoids Dictionary IEnumerator allocations)
    private SubscriberEntry[] _subscribers = new SubscriberEntry[4];
    private int _subscriberCount;
    private UnityAction<T> _unityEventCallbacks;

    public ObservableProperty()
    {
        value = default;
        _cachedDispatchAction = DispatchNotifications;
    }

    public ObservableProperty(T initialValue)
    {
        value = initialValue;
        _cachedDispatchAction = DispatchNotifications;
    }

    public T Value
    {
        get
        {
            lock (_lock)
            {
                return value;
            }
        }
        set => SetValue(value, false);
    }

    public void SetValue(T newValue, bool forceUpdate = false)
    {
        lock (_lock)
        {
            if (EqualityComparer<T>.Default.Equals(value, newValue))
                return;

            value = newValue;
        }

        // Immediate synchronous dispatch if requested and already on the Unity main thread
        if (forceUpdate && CentralNotificationSystem.IsMainThread)
        {
            ClearEnqueued();
            DispatchNotifications();
            return;
        }

        // Zero-GC Action enqueuing: uses the cached Action delegate with the central dispatcher
        if (TryMarkEnqueued())
        {
            var system = CentralNotificationSystem.Instance;
            if (system != null)
            {
                system.EnqueueMainThreadAction(_cachedDispatchAction, forceUpdate ? 100 : 0);
            }
            else if (CentralNotificationSystem.IsMainThread)
            {
                ClearEnqueued();
                DispatchNotifications();
            }
        }
    }

    internal override void DispatchNotifications()
    {
        ClearEnqueued();

        // System-level reentrancy guard: prevents recursive subscriber feedback loops
        if (!TryBeginDispatch())
            return;

        try
        {
            T currentValue;
            SubscriberEntry[] currentSubs;
            int count;

            lock (_lock)
            {
                currentValue = value;
                currentSubs = _subscribers;
                count = _subscriberCount;
            }

            // Direct index iteration without IEnumerator allocations
            for (int i = 0; i < count; i++)
            {
                var entry = currentSubs[i];
                if (entry.Observer == null) continue;

                if (entry.Observer is UnityEngine.Object unityObj && unityObj == null)
                {
                    Unsubscribe(entry.Observer);
                    continue;
                }

                try
                {
                    entry.Callback?.Invoke(currentValue);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    Unsubscribe(entry.Observer);
                }
            }

            try
            {
                _unityEventCallbacks?.Invoke(currentValue);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
        finally
        {
            EndDispatch();
        }
    }

    public void Subscribe(object observer, Action<T> callback)
    {
        if (observer == null) throw new ArgumentNullException(nameof(observer));
        if (callback == null) throw new ArgumentNullException(nameof(callback));

        lock (_lock)
        {
            for (int i = 0; i < _subscriberCount; i++)
            {
                if (ReferenceEquals(_subscribers[i].Observer, observer))
                {
                    _subscribers[i].Callback = callback;
                    return;
                }
            }

            if (_subscriberCount >= _subscribers.Length)
            {
                Array.Resize(ref _subscribers, _subscribers.Length * 2);
            }

            _subscribers[_subscriberCount++] = new SubscriberEntry
            {
                Observer = observer,
                Callback = callback
            };
        }
    }

    public IDisposable Subscribe(Action<T> callback)
    {
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        var token = new SubscriptionToken(this, callback);
        Subscribe(token, callback);
        return token;
    }

    public void Subscribe(UnityAction<T> callback)
    {
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        lock (_lock)
        {
            _unityEventCallbacks += callback;
        }
    }

    public void Unsubscribe(object observer)
    {
        if (observer == null) return;

        lock (_lock)
        {
            for (int i = 0; i < _subscriberCount; i++)
            {
                if (ReferenceEquals(_subscribers[i].Observer, observer))
                {
                    for (int j = i; j < _subscriberCount - 1; j++)
                    {
                        _subscribers[j] = _subscribers[j + 1];
                    }
                    _subscriberCount--;
                    _subscribers[_subscriberCount] = default;
                    return;
                }
            }
        }
    }

    public void Unsubscribe(UnityAction<T> callback)
    {
        if (callback == null) return;
        lock (_lock)
        {
            _unityEventCallbacks -= callback;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Array.Clear(_subscribers, 0, _subscriberCount);
            _subscriberCount = 0;
            _unityEventCallbacks = null;
        }
        ClearEnqueued();
    }

    private sealed class SubscriptionToken : IDisposable
    {
        private ObservableProperty<T> _property;
        private Action<T> _callback;

        public SubscriptionToken(ObservableProperty<T> property, Action<T> callback)
        {
            _property = property;
            _callback = callback;
        }

        public void Dispose()
        {
            if (_property != null)
            {
                _property.Unsubscribe(this);
                _property = null;
                _callback = null;
            }
        }
    }
}

#region Base Types
[Serializable]
public class ObservableInt : ObservableProperty<int>
{
    public ObservableInt() : base() { }
    public ObservableInt(int initialValue) : base(initialValue) { }
}

[Serializable]
public class ObservableBool : ObservableProperty<bool>
{
    public ObservableBool() : base() { }
    public ObservableBool(bool initialValue) : base(initialValue) { }
}

[Serializable]
public class ObservableFloat : ObservableProperty<float>
{
    public ObservableFloat() : base() { }
    public ObservableFloat(float initialValue) : base(initialValue) { }
}

[Serializable]
public class ObservableDouble : ObservableProperty<double>
{
    public ObservableDouble() : base() { }
    public ObservableDouble(double initialValue) : base(initialValue) { }
}

[Serializable]
public class ObservableLong : ObservableProperty<long>
{
    public ObservableLong() : base() { }
    public ObservableLong(long initialValue) : base(initialValue) { }
}

[Serializable]
public class ObservableString : ObservableProperty<string>
{
    public ObservableString() : base() { }
    public ObservableString(string initialValue) : base(initialValue) { }
}

[Serializable]
public class ObservableEnum<T> : ObservableProperty<T> where T : Enum
{
    public ObservableEnum() : base() { }
    public ObservableEnum(T initialValue) : base(initialValue) { }
}
#endregion

#region Unity Specific Types
[Serializable]
public class ObservableVector3 : ObservableProperty<Vector3>
{
    public ObservableVector3() : base() { }
    public ObservableVector3(Vector3 initialValue) : base(initialValue) { }
}

[Serializable]
public class ObservableVector2 : ObservableProperty<Vector2>
{
    public ObservableVector2() : base() { }
    public ObservableVector2(Vector2 initialValue) : base(initialValue) { }
}

[Serializable]
public class ObservableVector3Int : ObservableProperty<Vector3Int>
{
    public ObservableVector3Int() : base() { }
    public ObservableVector3Int(Vector3Int initialValue) : base(initialValue) { }
}

[Serializable]
public class ObservableVector2Int : ObservableProperty<Vector2Int>
{
    public ObservableVector2Int() : base() { }
    public ObservableVector2Int(Vector2Int initialValue) : base(initialValue) { }
}

[Serializable]
public class ObservableQuaternion : ObservableProperty<Quaternion>
{
    public ObservableQuaternion() : base() { }
    public ObservableQuaternion(Quaternion initialValue) : base(initialValue) { }
}
#endregion
