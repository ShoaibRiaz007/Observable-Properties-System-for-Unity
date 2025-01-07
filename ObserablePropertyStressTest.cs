using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class ObserablePropertyStressTest : MonoBehaviour
{
    [SerializeField, Range(10, 10000)] private int Length = 100;
    [SerializeField] private Text amount;
    [SerializeField] private Text nnValueChangeText;


    [Header("Use BG Thread"),SerializeField] bool RunInBGThread = false;

    [Header("Debug"), SerializeField] private int DebugValueAtIndex = 0;
    [SerializeField] private ObservableBool debugValues;
    [Serializable]
    class Obserables:IDisposable
    {
        public ObservableBool ObservableBool;
        Text text;
        int index = 0;
        public Obserables(Text text,int index)
        {
            this.text = Instantiate(text,text.transform.parent);
            ObservableBool = new ObservableBool(true);
            this.index= index;
            ObservableBool.Subscribe(this, OnValueChange);
        }

        private void OnValueChange(bool obj)
        {
            text.text=$"{index}:{obj}";
        }

        public void Dispose()
        {
            Destroy(text.gameObject);
        }
    }
    [SerializeField]
    List<Obserables> AllObservables = new List<Obserables>();

    public float CurrentLength
    {
        set
        {
            int newLength = Mathf.Clamp((int)value, 0, int.MaxValue);
            if (newLength == AllObservables.Count) return;

            Length = newLength;
            amount.text = Length.ToString();
            if (AllObservables.Count > Length)
            {
                for(int i = Length; i < AllObservables.Count; i++)
                {
                    AllObservables[i].Dispose();
                }
                AllObservables.RemoveRange(Length, AllObservables.Count - Length);
            }
            else
            {
                for (int i = AllObservables.Count; i < Length; i++)
                {
                    AllObservables.Add(new Obserables(nnValueChangeText,i));
                }
            }
        }
        get => AllObservables.Count;
    }
    private int cacheDebugValue = 0;

    private void OnValidate()
    {

        if (DebugValueAtIndex < 0) DebugValueAtIndex = 0;
        else if (DebugValueAtIndex >= Length) DebugValueAtIndex = Length - 1;

        if (cacheDebugValue != DebugValueAtIndex)
        {
            debugValues = AllObservables[DebugValueAtIndex].ObservableBool;
        }
    }

    private void Awake()
    {
        CurrentLength = Length;
    }

    private void Start()
    {
        debugValues = AllObservables[0].ObservableBool;
    }

    private void Update()
    {
        if (RunInBGThread)
            System.Threading.Tasks.Task.Run(UpdateValues);
        else
            UpdateValues();
    }

    void UpdateValues()
    {
        for (int i = 0; i < AllObservables.Count; i++)
        {
            AllObservables[i].ObservableBool.Value = !AllObservables[i].ObservableBool.Value;
        }
    }
}