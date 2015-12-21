﻿using System;

namespace AudioSwitcher.AudioApi.Observables
{
    public sealed class FilteredBroadcaster<T> : Broadcaster<T>
    {
        private readonly Func<T, bool> _predicate;
        private readonly IDisposable _observerSubscription;

        internal FilteredBroadcaster(IObservable<T> observable, Func<T, bool> predicate)
        {
            _observerSubscription = observable.Subscribe(this);
            _predicate = predicate;
        }

        public override void OnNext(T value)
        {
            if (_predicate(value))
                base.OnNext(value);
        }

        public override void Dispose()
        {
            _observerSubscription.Dispose();
            base.Dispose();
        }
    }
}
