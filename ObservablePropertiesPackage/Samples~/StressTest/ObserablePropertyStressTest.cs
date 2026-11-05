using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Stress test benchmark component for validating ObservableProperty performance, thread-safety,
/// and verifying 0 B GC.Alloc in the Unity Profiler.
/// </summary>
public class ObserablePropertyStressTest : MonoBehaviour
{
    [SerializeField, Range(10, 10000)] private int Length = 100;
    [SerializeField] private Text amount;
    [SerializeField] private Text nnValueChangeText;

    [Header("Use BG Thread"), SerializeField] private bool RunInBGThread = false;

    [Header("GC Benchmark"), Tooltip("When enabled, uses pre-cached strings so UI Text updates generate exactly 0 B GC heap allocations.")]
    [SerializeField] private bool UsePreCachedStrings = true;

    [Header("Debug"), SerializeField] private int DebugValueAtIndex = 0;
    [SerializeField] private ObservableBool debugValues;

    // Pre-allocated static string tables to ensure UI updates allocate 0 B GC in the Unity Profiler
    private static string[] s_trueStrings = Array.Empty<string>();
    private static string[] s_falseStrings = Array.Empty<string>();

    [Serializable]
    private class Obserables : IDisposable
    {
        public ObservableBool ObservableBool;
        private Text text;
        private int index;
        private readonly ObserablePropertyStressTest owner;

        public Obserables(Text templateText, int index, ObserablePropertyStressTest owner)
        {
            this.index = index;
            this.owner = owner;
            ObservableBool = new ObservableBool(true);

            if (templateText != null)
            {
                this.text = Instantiate(templateText, templateText.transform.parent);
                this.text.gameObject.SetActive(true);
            }

            ObservableBool.Subscribe(this, OnValueChange);
        }

        private void OnValueChange(bool obj)
        {
            if (text == null) return;

            // Zero-GC UI update: references pre-allocated string table instead of string.Format/$""
            if (owner != null && owner.UsePreCachedStrings && index < s_trueStrings.Length)
            {
                text.text = obj ? s_trueStrings[index] : s_falseStrings[index];
            }
            else
            {
                text.text = $"{index}:{obj}";
            }
        }

        public void Dispose()
        {
            if (ObservableBool != null)
            {
                ObservableBool.Unsubscribe(this);
                ObservableBool.Dispose();
                ObservableBool = null;
            }

            if (text != null && text.gameObject != null)
            {
                Destroy(text.gameObject);
                text = null;
            }
        }
    }

    [SerializeField]
    private List<Obserables> AllObservables = new List<Obserables>();

    private Task _bgTask;
    private int _isUpdating; // Interlocked reentrancy guard
    private int cacheDebugValue = -1;

    public float CurrentLength
    {
        get => AllObservables.Count;
        set
        {
            int newLength = Mathf.Clamp((int)value, 0, int.MaxValue);
            if (newLength == AllObservables.Count) return;

            Length = newLength;
            EnsureStringTableCapacity(Length);

            if (amount != null)
                amount.text = Length.ToString();

            if (AllObservables.Count > Length)
            {
                for (int i = Length; i < AllObservables.Count; i++)
                {
                    AllObservables[i]?.Dispose();
                }
                AllObservables.RemoveRange(Length, AllObservables.Count - Length);
            }
            else
            {
                for (int i = AllObservables.Count; i < Length; i++)
                {
                    AllObservables.Add(new Obserables(nnValueChangeText, i, this));
                }
            }
        }
    }

    private static void EnsureStringTableCapacity(int capacity)
    {
        if (s_trueStrings.Length >= capacity) return;

        int oldLen = s_trueStrings.Length;
        Array.Resize(ref s_trueStrings, capacity);
        Array.Resize(ref s_falseStrings, capacity);

        for (int i = oldLen; i < capacity; i++)
        {
            s_trueStrings[i] = $"{i}:True";
            s_falseStrings[i] = $"{i}:False";
        }
    }

    private void OnValidate()
    {
        if (Length < 0) Length = 0;

        if (DebugValueAtIndex < 0) DebugValueAtIndex = 0;
        else if (DebugValueAtIndex >= Length) DebugValueAtIndex = Mathf.Max(0, Length - 1);

        if (cacheDebugValue != DebugValueAtIndex && AllObservables != null && DebugValueAtIndex < AllObservables.Count)
        {
            cacheDebugValue = DebugValueAtIndex;
            debugValues = AllObservables[DebugValueAtIndex]?.ObservableBool;
        }
    }

    private void Awake()
    {
        CurrentLength = Length;
    }

    private void Start()
    {
        if (AllObservables.Count > 0)
        {
            debugValues = AllObservables[0].ObservableBool;
        }
    }

    private void Update()
    {
        if (RunInBGThread)
        {
            // Prevent runaway Task.Run storming: only spawn a new task when previous completes
            if (_bgTask == null || _bgTask.IsCompleted)
            {
                _bgTask = Task.Run(UpdateValues);
            }
        }
        else
        {
            UpdateValues();
        }
    }

    private void UpdateValues()
    {
        // Reentrancy guard across frames/threads
        if (Interlocked.Exchange(ref _isUpdating, 1) != 0)
            return;

        try
        {
            int count = AllObservables.Count;
            for (int i = 0; i < count; i++)
            {
                var obs = AllObservables[i];
                if (obs != null && obs.ObservableBool != null)
                {
                    obs.ObservableBool.Value = !obs.ObservableBool.Value;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _isUpdating, 0);
        }
    }

    private void OnDestroy()
    {
        for (int i = 0; i < AllObservables.Count; i++)
        {
            AllObservables[i]?.Dispose();
        }
        AllObservables.Clear();
    }
}
