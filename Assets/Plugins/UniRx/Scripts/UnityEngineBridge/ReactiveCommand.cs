﻿using System;

namespace UniRx
{
    public interface IReactiveCommand<T> : IObservable<T>
    {
        IReadOnlyReactiveProperty<bool> CanExecute { get; }
        bool Execute(T parameter);
    }

    public interface IAsyncReactiveCommand<T>
    {
        IReadOnlyReactiveProperty<bool> CanExecute { get; }
        IDisposable Execute(T parameter);
        IDisposable Subscribe(Func<T, IObservable<Unit>> asyncAction);
    }

    /// <summary>
    /// Represents ReactiveCommand&lt;Unit&gt;
    /// </summary>
    public class ReactiveCommand : ReactiveCommand<Unit>
    {
        /// <summary>
        /// CanExecute is always true.
        /// </summary>
        public ReactiveCommand()
            : base()
        { }

        /// <summary>
        /// CanExecute is changed from canExecute sequence.
        /// </summary>
        public ReactiveCommand(IObservable<bool> canExecuteSource, bool initialValue = true)
            : base(canExecuteSource, initialValue)
        {
        }

        /// <summary>Push null to subscribers.</summary>
        public bool Execute()
        {
            return Execute(Unit.Default);
        }

        /// <summary>Force push parameter to subscribers.</summary>
        public void ForceExecute()
        {
            ForceExecute(Unit.Default);
        }
    }

    public class ReactiveCommand<T> : IReactiveCommand<T>, IDisposable
    {
        readonly Subject<T> trigger = new Subject<T>();
        readonly IDisposable canExecuteSubscription;

        ReactiveProperty<bool> canExecute;
        public IReadOnlyReactiveProperty<bool> CanExecute
        {
            get
            {
                return canExecute;
            }
        }

        public bool IsDisposed { get; private set; }

        /// <summary>
        /// CanExecute is always true.
        /// </summary>
        public ReactiveCommand()
        {
            this.canExecute = new ReactiveProperty<bool>(true);
            this.canExecuteSubscription = Disposable.Empty;
        }

        /// <summary>
        /// CanExecute is changed from canExecute sequence.
        /// </summary>
        public ReactiveCommand(IObservable<bool> canExecuteSource, bool initialValue = true)
        {
            this.canExecute = new ReactiveProperty<bool>(initialValue);
            this.canExecuteSubscription = canExecuteSource
                .DistinctUntilChanged()
                .SubscribeWithState(canExecute, (b, c) => c.Value = b);
        }

        /// <summary>Push parameter to subscribers when CanExecute.</summary>
        public bool Execute(T parameter)
        {
            if (canExecute.Value)
            {
                trigger.OnNext(parameter);
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>Force push parameter to subscribers.</summary>
        public void ForceExecute(T parameter)
        {
            trigger.OnNext(parameter);
        }

        /// <summary>Subscribe execute.</summary>
        public IDisposable Subscribe(IObserver<T> observer)
        {
            return trigger.Subscribe(observer);
        }

        /// <summary>
        /// Stop all subscription and lock CanExecute is false.
        /// </summary>
        public void Dispose()
        {
            if (IsDisposed) return;

            IsDisposed = true;
            canExecute.Dispose();
            trigger.OnCompleted();
            trigger.Dispose();
            canExecuteSubscription.Dispose();
        }
    }

    /// <summary>
    /// Variation of ReactiveCommand, when executing command then CanExecute = false after CanExecute = true.
    /// </summary>
    public class AsyncReactiveCommand : AsyncReactiveCommand<Unit>, IDisposable
    {
        public AsyncReactiveCommand(IReactiveProperty<bool> sharedCanExecuteSource)
            : base(sharedCanExecuteSource)
        {
        }
        public AsyncReactiveCommand(IReactiveProperty<bool> sharedCanExecuteSource, int throttleFrameCount)
            : base(sharedCanExecuteSource, throttleFrameCount)
        {

        }
    }

    /// <summary>
    /// Variation of ReactiveCommand, canExecute is changed when executing command then CanExecute = false after CanExecute = true.
    /// </summary>
    public class AsyncReactiveCommand<T> : IAsyncReactiveCommand<T>
    {
        readonly object gate = new object();
        UniRx.InternalUtil.ImmutableList<Func<T, IObservable<Unit>>> asyncActions = UniRx.InternalUtil.ImmutableList<Func<T, IObservable<Unit>>>.Empty;

        readonly IDisposable canExecuteSubscription;
        readonly int? throttleFrameCount;

        IReactiveProperty<bool> canExecute;
        public IReadOnlyReactiveProperty<bool> CanExecute
        {
            get
            {
                return canExecute;
            }
        }

        public bool IsDisposed { get; private set; }

        /// <summary>
        /// CanExecute is changed from canExecute sequence and when executing changed to false.
        /// </summary>
        public AsyncReactiveCommand(IReactiveProperty<bool> sharedCanExecuteSource)
        {
            this.canExecute = sharedCanExecuteSource;
            this.canExecuteSubscription = sharedCanExecuteSource.SubscribeWithState(canExecute, (b, c) => c.Value = b);
            this.throttleFrameCount = null;
        }

        /// <summary>
        /// CanExecute is changed from canExecute sequence and when executing changed to false and delay after throttleFrameCount, to true.
        /// </summary>
        public AsyncReactiveCommand(IReactiveProperty<bool> sharedCanExecuteSource, int throttleFrameCount)
        {
            this.canExecute = sharedCanExecuteSource;
            this.canExecuteSubscription = sharedCanExecuteSource.SubscribeWithState(canExecute, (b, c) => c.Value = b);
            this.throttleFrameCount = throttleFrameCount;
        }

        /// <summary>Push parameter to subscribers when CanExecute.</summary>
        public IDisposable Execute(T parameter)
        {
            if (canExecute.Value)
            {
                canExecute.Value = false;
                var a = asyncActions.Data;
                if (a.Length == 1)
                {
                    try
                    {
                        var asyncState = a[0].Invoke(parameter) ?? Observable.ReturnUnit();

                        if (this.throttleFrameCount == null)
                        {
                            return asyncState.Finally(() => canExecute.Value = true).Subscribe();
                        }
                        else
                        {
                            return asyncState.DelayFrame(throttleFrameCount.Value).Finally(() => canExecute.Value = true).Subscribe();
                        }
                    }
                    catch
                    {
                        canExecute.Value = true;
                        throw;
                    }
                }
                else
                {
                    var xs = new IObservable<Unit>[a.Length];
                    try
                    {
                        for (int i = 0; i < a.Length; i++)
                        {
                            xs[i] = a[i].Invoke(parameter) ?? Observable.ReturnUnit();
                        }
                    }
                    catch
                    {
                        canExecute.Value = true;
                        throw;
                    }

                    if (this.throttleFrameCount == null)
                    {
                        return Observable.WhenAll(xs).Finally(() => canExecute.Value = true).Subscribe();
                    }
                    else
                    {
                        return Observable.WhenAll(xs).DelayFrame(throttleFrameCount.Value).Finally(() => canExecute.Value = true).Subscribe();
                    }
                }
            }
            else
            {
                return Disposable.Empty;
            }
        }

        /// <summary>Subscribe execute.</summary>
        public IDisposable Subscribe(Func<T, IObservable<Unit>> asyncAction)
        {
            lock (gate)
            {
                asyncActions = asyncActions.Add(asyncAction);
            }

            return new Subscription(this, asyncAction);
        }

        /// <summary>
        /// Stop source subscription.
        /// </summary>
        public void Dispose()
        {
            if (IsDisposed) return;

            IsDisposed = true;
            canExecuteSubscription.Dispose();
        }

        class Subscription : IDisposable
        {
            readonly AsyncReactiveCommand<T> parent;
            readonly Func<T, IObservable<Unit>> asyncAction;

            public Subscription(AsyncReactiveCommand<T> parent, Func<T, IObservable<Unit>> asyncAction)
            {
                this.parent = parent;
                this.asyncAction = asyncAction;
            }

            public void Dispose()
            {
                lock (parent.gate)
                {
                    parent.asyncActions = parent.asyncActions.Remove(asyncAction);
                }
            }
        }
    }

    public static class ReactiveCommandExtensions
    {
        /// <summary>
        /// Create non parameter commands. CanExecute is changed from canExecute sequence.
        /// </summary>
        public static ReactiveCommand ToReactiveCommand(this IObservable<bool> canExecuteSource, bool initialValue = true)
        {
            return new ReactiveCommand(canExecuteSource, initialValue);
        }

        /// <summary>
        /// Create parametered comamnds. CanExecute is changed from canExecute sequence.
        /// </summary>
        public static ReactiveCommand<T> ToReactiveCommand<T>(this IObservable<bool> canExecuteSource, bool initialValue = true)
        {
            return new ReactiveCommand<T>(canExecuteSource, initialValue);
        }

#if !UniRxLibrary

        // for uGUI(from 4.6)
#if !(UNITY_4_0 || UNITY_4_1 || UNITY_4_2 || UNITY_4_3 || UNITY_4_4 || UNITY_4_5)

        /// <summary>
        /// Bind RaectiveCommand to button's interactable and onClick.
        /// </summary>
        public static IDisposable BindTo(this ReactiveCommand<Unit> command, UnityEngine.UI.Button button)
        {
            var d1 = command.CanExecute.SubscribeToInteractable(button);
            var d2 = button.OnClickAsObservable().SubscribeWithState(command, (x, c) => c.Execute(x));
            return StableCompositeDisposable.Create(d1, d2);
        }

        /// <summary>
        /// Bind RaectiveCommand to button's interactable and onClick and register onClick action to command.
        /// </summary>
        public static IDisposable BindToOnClick(this ReactiveCommand<Unit> command, UnityEngine.UI.Button button, Action<Unit> onClick)
        {
            var d1 = command.CanExecute.SubscribeToInteractable(button);
            var d2 = button.OnClickAsObservable().SubscribeWithState(command, (x, c) => c.Execute(x));
            var d3 = command.Subscribe(onClick);

            return StableCompositeDisposable.Create(d1, d2, d3);
        }

        /// <summary>
        /// Bind canExecuteSource to button's interactable and onClick and register onClick action to command.
        /// </summary>
        public static IDisposable BindToButtonOnClick(this IObservable<bool> canExecuteSource, UnityEngine.UI.Button button, Action<Unit> onClick, bool initialValue = true)
        {
            return ToReactiveCommand(canExecuteSource, initialValue).BindToOnClick(button, onClick);
        }

#endif

#endif
    }

    public static class AsyncReactiveCommandExtensions
    {
        public static AsyncReactiveCommand ToAsyncReactiveCommand(this IReactiveProperty<bool> sharedCanExecuteSource, int? throttleFrameCount = null)
        {
            return (throttleFrameCount == null)
                ? new AsyncReactiveCommand(sharedCanExecuteSource)
                : new AsyncReactiveCommand(sharedCanExecuteSource, throttleFrameCount.Value);
        }

        public static AsyncReactiveCommand<T> ToAsyncReactiveCommand<T>(this IReactiveProperty<bool> sharedCanExecuteSource, int? throttleFrameCount = null)
        {
            return (throttleFrameCount == null)
                ? new AsyncReactiveCommand<T>(sharedCanExecuteSource)
                : new AsyncReactiveCommand<T>(sharedCanExecuteSource, throttleFrameCount.Value);
        }

#if !UniRxLibrary

        // for uGUI(from 4.6)
#if !(UNITY_4_0 || UNITY_4_1 || UNITY_4_2 || UNITY_4_3 || UNITY_4_4 || UNITY_4_5)

        /// <summary>
        /// Bind AsyncRaectiveCommand to button's interactable and onClick.
        /// </summary>
        public static IDisposable BindTo(this AsyncReactiveCommand<Unit> command, UnityEngine.UI.Button button)
        {
            var d1 = command.CanExecute.SubscribeToInteractable(button);
            var d2 = button.OnClickAsObservable().SubscribeWithState(command, (x, c) => c.Execute(x));

            return StableCompositeDisposable.Create(d1, d2);
        }

        /// <summary>
        /// Bind AsyncRaectiveCommand to button's interactable and onClick and register async action to command.
        /// </summary>
        public static IDisposable BindToOnClick(this AsyncReactiveCommand<Unit> command, UnityEngine.UI.Button button, Func<Unit, IObservable<Unit>> asyncOnClick)
        {
            var d1 = command.CanExecute.SubscribeToInteractable(button);
            var d2 = button.OnClickAsObservable().SubscribeWithState(command, (x, c) => c.Execute(x));
            var d3 = command.Subscribe(asyncOnClick);

            return StableCompositeDisposable.Create(d1, d2, d3);
        }

        /// <summary>
        /// Bind sharedCanExecuteSource source to button's interactable and onClick and register async action to command.
        /// </summary>
        public static IDisposable BindToButton(this IReactiveProperty<bool> sharedCanExecuteSource, UnityEngine.UI.Button button, Func<Unit, IObservable<Unit>> asyncOnClick, int? throttleFrameCount = null)
        {
            return sharedCanExecuteSource.ToAsyncReactiveCommand(throttleFrameCount).BindToOnClick(button, asyncOnClick);
        }
#endif

#endif
    }
}