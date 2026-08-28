using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SwitchBoard.Services.ApplicationLifecycle;

/// <summary>
/// Owns the process-wide SwitchBoard instance lock and the activation signal used
/// by later launches to ask the primary instance to show its existing window.
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    public const string MutexName = @"Local\SwitchBoard.SingleInstance";
    public const string ActivationEventName = @"Local\SwitchBoard.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly object _callbackGate = new();
    private RegisteredWaitHandle? _registeredWait;
    private Action? _activationCallback;
    private int _disposed;

    private SingleInstanceCoordinator(Mutex mutex, EventWaitHandle activationEvent)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
    }

    /// <summary>
    /// Acquires the application lock without waiting. A null result means that
    /// another SwitchBoard instance already owns it.
    /// </summary>
    public static SingleInstanceCoordinator? TryAcquire() =>
        TryAcquire(MutexName, ActivationEventName);

    /// <summary>
    /// Signals the primary instance. This method intentionally does not create a
    /// missing event: failure to find the primary's signal must never allow the
    /// second process to continue with normal application startup.
    /// </summary>
    public static bool TrySignalExisting() => TrySignalExisting(ActivationEventName);

    /// <summary>
    /// Starts a non-blocking thread-pool wait for activation requests. The
    /// callback is never invoked on the UI thread by this class; callers that
    /// touch UI must dispatch the work themselves.
    /// </summary>
    public void StartListening(Action onActivation)
    {
        ArgumentNullException.ThrowIfNull(onActivation);
        lock (_callbackGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_registeredWait is not null)
                throw new InvalidOperationException("Single-instance activation listening has already started.");

            _activationCallback = onActivation;
            _registeredWait = ThreadPool.RegisterWaitForSingleObject(
                _activationEvent,
                static (state, timedOut) =>
                {
                    if (!timedOut && state is SingleInstanceCoordinator coordinator)
                        coordinator.OnActivationSignaled();
                },
                this,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        RegisteredWaitHandle? registeredWait;
        lock (_callbackGate)
        {
            registeredWait = _registeredWait;
            _registeredWait = null;
            _activationCallback = null;
        }

        ReleaseMutex();

        if (registeredWait is null)
        {
            _activationEvent.Dispose();
            return;
        }

        // Unregister can report that a callback is currently running. Do not
        // wait on the UI thread; dispose the event after that callback returns.
        var unregisterCompleted = new ManualResetEvent(false);
        if (registeredWait.Unregister(unregisterCompleted))
        {
            unregisterCompleted.Dispose();
            _activationEvent.Dispose();
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                unregisterCompleted.WaitOne();
            }
            finally
            {
                unregisterCompleted.Dispose();
                _activationEvent.Dispose();
            }
        });
    }

    internal static SingleInstanceCoordinator? TryAcquire(string mutexName, string activationEventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(activationEventName);

        Mutex? mutex = null;
        var ownsMutex = false;
        try
        {
            mutex = new Mutex(false, mutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return null;
            }

            try
            {
                ownsMutex = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // The newly created mutex should not be abandoned, but treating
                // this as ownership keeps recovery safe if Windows reports it.
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                mutex.Dispose();
                return null;
            }

            var activationEvent = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: activationEventName,
                createdNew: out _);
            var coordinator = new SingleInstanceCoordinator(mutex, activationEvent);
            mutex = null;
            return coordinator;
        }
        catch
        {
            if (ownsMutex)
            {
                try { mutex?.ReleaseMutex(); }
                catch (ApplicationException) { }
                catch (ObjectDisposedException) { }
            }

            mutex?.Dispose();
            throw;
        }
    }

    internal static bool TrySignalExisting(string activationEventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activationEventName);
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(activationEventName);
            return activationEvent.Set();
        }
        catch (Exception exception) when (exception is WaitHandleCannotBeOpenedException
            or UnauthorizedAccessException
            or IOException
            or InvalidOperationException)
        {
            // There is no logger before the single-instance gate on purpose. A
            // trace entry is enough for diagnostics and keeps duplicate launches
            // from touching the user's application data.
            Debug.WriteLine($"SwitchBoard activation signal failed: {exception}");
            return false;
        }
    }

    private void OnActivationSignaled()
    {
        if (IsDisposed) return;
        Action? callback;
        lock (_callbackGate) callback = _activationCallback;
        if (callback is null || IsDisposed) return;

        try { callback(); }
        catch (Exception exception)
        {
            Debug.WriteLine($"SwitchBoard activation callback failed: {exception}");
        }
    }

    private void ReleaseMutex()
    {
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        catch (ObjectDisposedException) { }
        finally { _mutex.Dispose(); }
    }
}
