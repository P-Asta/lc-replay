using LCReplay.Core.Lifecycle;

var suite = new (string Name, Action Run)[]
{
    ("Initial orbit and repeated readiness do not create extra days", InitialOrbit),
    ("Joining an expedition begins observed day one", LateJoin),
    ("An entire three-day lobby produces exactly three days", ThreeDays),
    ("Duplicate observations and deaths cannot rotate an active expedition", RepeatedPolls),
    ("Leaving after return has no trailing empty day", NoTrailingDay),
    ("A new connection starts its own day one", Reconnect),
};
int failures = 0;
foreach (var test in suite)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + test.Name + "\n" + e); }
}
Console.WriteLine($"{suite.Length - failures}/{suite.Length} test groups passed.");
return failures == 0 ? 0 : 1;

static void InitialOrbit()
{
    var day = new DayLifecycle();
    Check(day.DayNumber == 1 && !day.HasDeparted && !day.HasReturned, "initial day");
    for (int i = 0; i < 600; i++) Check(day.Observe(true) == DayTransition.None, "waiting remains initial orbit");
    Check(day.DayNumber == 1 && !day.HasDeparted && !day.HasReturned, "waiting preserves day one");
    Check(day.Observe(false) == DayTransition.ExpeditionStarted, "first departure");
    Check(day.DayNumber == 1 && day.HasDeparted && !day.HasReturned, "first departure remains day one");
}

static void LateJoin()
{
    var day = new DayLifecycle();
    Check(day.Observe(false) == DayTransition.ExpeditionStarted, "active first observation");
    Check(day.DayNumber == 1 && day.HasDeparted && !day.HasReturned, "late join does not invent an earlier day");
    Check(day.Observe(true) == DayTransition.ReturnedToOrbit, "latejoin return");
    Check(day.Observe(false) == DayTransition.NextDayStarted && day.DayNumber == 2, "latejoin next day");
}

static void ThreeDays()
{
    var day = new DayLifecycle();
    var transitions = new List<DayTransition>();
    foreach (bool phase in new[] { true, true, false, false, true, true, false, false, true, false, false, true, true })
    {
        var transition = day.Observe(phase);
        if (transition != DayTransition.None) transitions.Add(transition);
    }
    Check(transitions.SequenceEqual(new[]
    {
        DayTransition.ExpeditionStarted, DayTransition.ReturnedToOrbit,
        DayTransition.NextDayStarted, DayTransition.ReturnedToOrbit,
        DayTransition.NextDayStarted, DayTransition.ReturnedToOrbit
    }), "exact day boundary order");
    Check(day.DayNumber == 3 && day.HasReturned, "three expeditions means three replay days");
}

static void RepeatedPolls()
{
    var day = new DayLifecycle();
    day.Observe(true);
    day.Observe(false);
    // Player death, ship departure animation, and end-of-day stats are all still
    // inShipPhase=false. Only the final readiness state completes the expedition.
    for (int i = 0; i < 10000; i++) Check(day.Observe(false) == DayTransition.None, "poll during expedition");
    Check(day.DayNumber == 1 && !day.HasReturned, "no premature next day");
    Check(day.Observe(true) == DayTransition.ReturnedToOrbit, "final readiness completes once");
    for (int i = 0; i < 10000; i++) Check(day.Observe(true) == DayTransition.None, "duplicate ready observations");
    Check(day.Observe(false) == DayTransition.NextDayStarted, "second departure completes one rollover");
    for (int i = 0; i < 10000; i++) Check(day.Observe(false) == DayTransition.None, "duplicate second departure observations");
    Check(day.DayNumber == 2 && day.HasDeparted && !day.HasReturned, "exactly one rollover");
}

static void NoTrailingDay()
{
    var day = new DayLifecycle();
    day.Observe(false);
    Check(day.Observe(true) == DayTransition.ReturnedToOrbit, "completed expedition");
    for (int i = 0; i < 500; i++) day.Observe(true);
    Check(day.DayNumber == 1 && day.HasReturned, "postflight recording belongs to completed day");
    // Disconnect consumes no additional phase observation and leaves this day intact.
}

static void Reconnect()
{
    var first = new DayLifecycle();
    first.Observe(false); first.Observe(true); first.Observe(false);
    Check(first.DayNumber == 2, "first connection reaches day two");
    var next = new DayLifecycle();
    Check(next.DayNumber == 1 && !next.HasDeparted && !next.HasReturned, "new connection reset");
    Check(next.Observe(true) == DayTransition.None, "new lobby waiting");
    Check(next.Observe(false) == DayTransition.ExpeditionStarted && next.DayNumber == 1, "new lobby first expedition");
}

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
}
