# Observable Properties System for Unity

This repository contains a robust and extensible implementation of an **Observable Properties System** designed for use with Unity. It provides a centralized, thread-safe notification system for managing observable properties, enabling efficient updates and communication across various components in your Unity project.

## Features
- **Thread-Safe Operations**: Handles observable properties in a thread-safe manner, ensuring stability in multi-threaded environments.
- **Centralized Notification System**: Utilizes a singleton-based central notification system to batch and process updates efficiently.
- **Customizable Observables**: Includes generic and Unity-specific observable property types (e.g., `ObservableInt`, `ObservableVector3`, `ObservableQuaternion`).
- **Flexible Subscription Mechanism**: Supports multiple subscription methods with safety checks to prevent memory leaks or redundant notifications.
- **Performance Optimizations**: Features dynamic batch size adjustment and resource cleanup for optimal performance.

## Key Components
1. **`IObservable<T>`**  
   Interface defining the observable properties with methods for subscribing, unsubscribing, and managing property updates.
   
2. **`CentralNotificationSystem`**  
   Singleton class that manages action scheduling, batching, and resource optimization during runtime.
   
3. **`ObservableProperty<T>`**  
   Base class for creating observable properties with built-in notification logic.
   
4. **Unity-Specific Observable Types**  
   Predefined observable types for commonly used Unity objects like `Vector3`, `Quaternion`, and more.

## Usage
1. **Define an Observable Property**
   ```csharp
   public ObservableInt playerHealth = new ObservableInt(100);
---

## Usage Examples

### Example 1: Basic Observable Property
Define an observable property, subscribe to it, and update its value.

```csharp
using UnityEngine;

public class Example : MonoBehaviour
{
    private ObservableInt playerHealth = new ObservableInt(100);

    void Start()
    {
        // Subscribe to value changes
        playerHealth.Subscribe(this, newValue =>
        {
            Debug.Log($"Player health updated to: {newValue}");
        });

        // Update the value
        playerHealth.Value = 80; // Automatically notifies subscribers
    }
}
