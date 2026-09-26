using System;

namespace LCReplay.Core.Lifecycle
{
    public enum DayTransition
    {
        None,
        ExpeditionStarted,
        ReturnedToOrbit,
        NextDayStarted
    }

    /// <summary>
    /// Groups one locally observed lobby connection into expedition days. Create a new
    /// instance for every connection; numbers intentionally do not use gameStats.daysSpent,
    /// which can be reset by firing and need not be synchronized to joining clients.
    /// </summary>
    public sealed class DayLifecycle
    {
        public int DayNumber { get; private set; } = 1;
        public bool HasDeparted { get; private set; }
        public bool HasReturned { get; private set; }

        /// <summary>
        /// Observe a known StartOfRound.inShipPhase value only while the same connected
        /// StartOfRound is available. Missing fields or transient missing scenes must not
        /// be converted into false observations. No hooks or RPC calls advance the number.
        /// </summary>
        public DayTransition Observe(bool inShipPhase)
        {
            if (inShipPhase)
            {
                if (!HasDeparted || HasReturned) return DayTransition.None;
                HasReturned = true;
                return DayTransition.ReturnedToOrbit;
            }

            if (HasReturned)
            {
                // Keep post-expedition orbit in the completed day until the next actual
                // departure. Ending the connection in orbit never creates an empty day.
                if (DayNumber == int.MaxValue) throw new InvalidOperationException("Replay day limit reached.");
                DayNumber++;
                HasReturned = false;
                return DayTransition.NextDayStarted;
            }

            if (HasDeparted) return DayTransition.None;
            // A first observation made after landing is still this recorder's day one.
            HasDeparted = true;
            return DayTransition.ExpeditionStarted;
        }
    }
}
